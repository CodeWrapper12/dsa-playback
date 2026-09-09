using System.Collections;
using System.Diagnostics;
using System.Reflection;
using DsaViz.Engine.Compilation;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Instrumentation;
using DsaViz.Runtime;

namespace DsaViz.Engine;

/// <summary>
/// A compiled solution, ready to be invoked repeatedly.
///
/// Compilation is by far the expensive step, and a growth curve runs the same solution
/// at half a dozen sizes. Separating compile from invoke keeps that cost paid once.
/// </summary>
public sealed class CompiledSolution : IDisposable
{
    private readonly RunContext _context;

    internal CompiledSolution(
        RunContext context, MethodInfo entry, IReadOnlyList<StatementInfo> statements, bool instrumented)
    {
        _context = context;
        Entry = entry;
        Statements = statements;
        IsInstrumented = instrumented;
    }

    public MethodInfo Entry { get; }
    public IReadOnlyList<StatementInfo> Statements { get; }
    public bool IsInstrumented { get; }
    public ParameterInfo[] Parameters => Entry.GetParameters();

    public void Dispose() => _context.Dispose();
}

public sealed record CompileOutcome(
    CompiledSolution? Solution, RunStatus Status, IReadOnlyList<string> Errors)
{
    public bool Success => Solution is not null;
}

public static class Runner
{
    public const long DefaultBudget = 50_000_000;

    // Tracer state is static, so only one measured run may be in flight at a time.
    private static readonly object RunLock = new();

    public static CompileOutcome Compile(string source, string entryMethod, bool instrument = true)
    {
        var originalTree = Instrumenter.Parse(source);

        var syntaxErrors = originalTree.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        if (syntaxErrors.Count > 0)
            return new CompileOutcome(null, RunStatus.CompileError, syntaxErrors);

        var treeToCompile = originalTree;
        IReadOnlyList<StatementInfo> statements = Array.Empty<StatementInfo>();

        if (instrument)
        {
            var instrumented = Instrumenter.Instrument(originalTree);
            treeToCompile = instrumented.Tree;
            statements = instrumented.Statements;
        }

        var compiled = InMemoryCompiler.Compile(treeToCompile, $"Solution_{Guid.NewGuid():N}");
        if (!compiled.Success)
            return new CompileOutcome(null, RunStatus.CompileError, compiled.Errors);

        var context = new RunContext();
        try
        {
            using var stream = new MemoryStream(compiled.Assembly!);
            var assembly = context.LoadFromStream(stream);
            var entry = FindEntryPoint(assembly, entryMethod);
            return new CompileOutcome(
                new CompiledSolution(context, entry, statements, instrument),
                RunStatus.Completed,
                Array.Empty<string>());
        }
        catch (Exception ex)
        {
            // The context owns an assembly that will never be used; unload it rather
            // than leaving it resident for the life of the process.
            context.Dispose();
            return new CompileOutcome(null, RunStatus.RuntimeError, new[] { ex.Message });
        }
    }

    public static TraceResult Invoke(
        CompiledSolution solution, object?[]? arguments = null, long budget = DefaultBudget)
    {
        lock (RunLock)
        {
            var capturedOutput = new StringWriter();
            var previousOut = Console.Out;
            Console.SetOut(capturedOutput);

            var stopwatch = Stopwatch.StartNew();
            object? returnValue = null;
            var status = RunStatus.Completed;
            var errors = new List<string>();

            try
            {
                if (solution.IsInstrumented) Tracer.Begin(solution.Statements.Count, budget);
                returnValue = solution.Entry.Invoke(null, arguments);
                if (solution.IsInstrumented) Tracer.End();
            }
            catch (TargetInvocationException ex)
                when (ex.InnerException is ExecutionBudgetExceededException budgetEx)
            {
                status = RunStatus.BudgetExceeded;
                errors.Add(budgetEx.Message);
            }
            catch (TargetInvocationException ex)
            {
                status = RunStatus.RuntimeError;
                errors.Add(ex.InnerException?.ToString() ?? ex.ToString());
            }
            catch (Exception ex)
            {
                status = RunStatus.RuntimeError;
                errors.Add(ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                Console.SetOut(previousOut);
            }

            return Collect(solution, status, errors, stopwatch, capturedOutput.ToString(), returnValue);
        }
    }

    private static TraceResult Collect(
        CompiledSolution solution, RunStatus status, List<string> errors,
        Stopwatch stopwatch, string output, object? returnValue)
    {
        var hits = solution.IsInstrumented ? Tracer.HitsSnapshot() : Array.Empty<long>();
        var bytes = solution.IsInstrumented ? Tracer.BytesSnapshot() : Array.Empty<long>();

        var statements = new List<StatementCost>();
        for (int id = 0; id < hits.Length && id < solution.Statements.Count; id++)
        {
            if (hits[id] == 0) continue;
            var info = solution.Statements[id];
            statements.Add(new StatementCost(
                info.Id, info.Line, info.Column, info.Length, info.Snippet, hits[id], bytes[id]));
        }

        // Source order, so a report reads like the file rather than like the order the
        // rewriter happened to visit nodes in.
        statements.Sort((left, right) => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Column.CompareTo(right.Column));

        // Line totals are a convenience over the statement data, never a replacement
        // for it: StatementCount is what tells the UI a total is a sum.
        var lines = statements
            .GroupBy(statement => statement.Line)
            .OrderBy(group => group.Key)
            .Select(group => new LineCost(
                group.Key,
                group.Sum(statement => statement.Hits),
                group.Sum(statement => statement.BytesAllocated),
                group.Count()))
            .ToList();

        return new TraceResult
        {
            Status = status,
            Statements = statements,
            Lines = lines,
            TotalOperations = solution.IsInstrumented ? Tracer.TotalOperations : 0,
            TotalBytesAllocated = bytes.Sum(),
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Errors = errors,
            Output = output,
            ReturnValue = status == RunStatus.Completed ? Render(returnValue) : null
        };
    }

    /// <summary>Compiles and runs a parameterless entry point once.</summary>
    public static TraceResult Run(string source, string entryMethod, long budget = DefaultBudget)
        => CompileAndInvoke(source, entryMethod, instrument: true, budget);

    /// <summary>
    /// Runs the solution exactly as written, with no instrumentation. Used to prove
    /// that instrumentation did not change behaviour.
    /// </summary>
    public static TraceResult RunBaseline(string source, string entryMethod)
        => CompileAndInvoke(source, entryMethod, instrument: false, DefaultBudget);

    private static TraceResult CompileAndInvoke(
        string source, string entryMethod, bool instrument, long budget)
    {
        var outcome = Compile(source, entryMethod, instrument);
        if (!outcome.Success) return Failure(outcome.Status, outcome.Errors);

        using var solution = outcome.Solution!;
        return Invoke(solution, arguments: null, budget);
    }

    private static MethodInfo FindEntryPoint(Assembly assembly, string name)
    {
        var candidates = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == name)
            .ToList();

        if (candidates.Count == 0)
        {
            var available = assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
                .Distinct();
            throw new InvalidOperationException(
                $"No public static method named '{name}' was found. Available: {string.Join(", ", available)}");
        }

        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"'{name}' is ambiguous — found in {string.Join(", ", candidates.Select(c => c.DeclaringType?.Name))}.");

        return candidates[0];
    }

    /// <summary>
    /// Renders a value deterministically so instrumented and baseline runs can be
    /// compared. Equality of rendered text is the check behind criterion 8.
    /// </summary>
    public static string? Render(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;

        if (value is IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var item in enumerable) parts.Add(Render(item) ?? "null");
            return "[" + string.Join(", ", parts) + "]";
        }

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static TraceResult Failure(RunStatus status, IReadOnlyList<string> errors) => new()
    {
        Status = status,
        Statements = Array.Empty<StatementCost>(),
        Lines = Array.Empty<LineCost>(),
        TotalOperations = 0,
        TotalBytesAllocated = 0,
        ElapsedMs = 0,
        Errors = errors
    };
}
