using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using DsaViz.Engine.Compilation;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Instrumentation;
using DsaViz.Runtime;

namespace DsaViz.Engine;

public static class Runner
{
    public const long DefaultBudget = 50_000_000;

    // Tracer state is static, so only one measured run may be in flight at a time.
    private static readonly object RunLock = new();

    public static TraceResult Run(string source, string entryMethod, long budget = DefaultBudget)
        => Execute(source, entryMethod, budget, instrument: true);

    /// <summary>
    /// Runs the solution exactly as written, with no instrumentation. Used to prove
    /// that instrumentation did not change behaviour.
    /// </summary>
    public static TraceResult RunBaseline(string source, string entryMethod)
        => Execute(source, entryMethod, DefaultBudget, instrument: false);

    private static TraceResult Execute(string source, string entryMethod, long budget, bool instrument)
    {
        var originalTree = Instrumenter.Parse(source);
        var syntaxErrors = originalTree.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        if (syntaxErrors.Count > 0) return Failure(RunStatus.CompileError, syntaxErrors);

        var treeToCompile = originalTree;
        IReadOnlyList<StatementInfo> statementMap = Array.Empty<StatementInfo>();

        if (instrument)
        {
            var instrumented = Instrumenter.Instrument(originalTree);
            treeToCompile = instrumented.Tree;
            statementMap = instrumented.Statements;
        }

        var compiled = InMemoryCompiler.Compile(treeToCompile, $"Solution_{Guid.NewGuid():N}");
        if (!compiled.Success) return Failure(RunStatus.CompileError, compiled.Errors);

        lock (RunLock)
        {
            using var context = new RunContext();
            using var assemblyStream = new MemoryStream(compiled.Assembly!);
            var assembly = context.LoadFromStream(assemblyStream);

            MethodInfo entry;
            try
            {
                entry = FindEntryPoint(assembly, entryMethod);
            }
            catch (InvalidOperationException ex)
            {
                return Failure(RunStatus.RuntimeError, new[] { ex.Message });
            }

            var capturedOutput = new StringWriter();
            var previousOut = Console.Out;
            Console.SetOut(capturedOutput);

            var stopwatch = Stopwatch.StartNew();
            object? returnValue = null;
            RunStatus status = RunStatus.Completed;
            var errors = new List<string>();

            try
            {
                if (instrument) Tracer.Begin(statementMap.Count, budget);
                returnValue = entry.Invoke(null, null);
                if (instrument) Tracer.End();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ExecutionBudgetExceededException budgetEx)
            {
                status = RunStatus.BudgetExceeded;
                errors.Add(budgetEx.Message);
            }
            catch (TargetInvocationException ex)
            {
                status = RunStatus.RuntimeError;
                errors.Add(ex.InnerException?.ToString() ?? ex.ToString());
            }
            finally
            {
                stopwatch.Stop();
                Console.SetOut(previousOut);
            }

            var hits = instrument ? Tracer.HitsSnapshot() : Array.Empty<long>();
            var bytes = instrument ? Tracer.BytesSnapshot() : Array.Empty<long>();

            var statements = new List<StatementCost>();
            for (int id = 0; id < hits.Length && id < statementMap.Count; id++)
            {
                if (hits[id] == 0) continue;
                var info = statementMap[id];
                statements.Add(new StatementCost(
                    info.Id, info.Line, info.Column, info.Length, info.Snippet, hits[id], bytes[id]));
            }

            // Source order, so a report reads like the file rather than like the
            // order the rewriter happened to visit nodes in.
            statements.Sort((left, right) => left.Line != right.Line
                ? left.Line.CompareTo(right.Line)
                : left.Column.CompareTo(right.Column));

            // Line totals are a convenience over the statement data, never a
            // replacement for it: StatementCount is what tells the UI a total is a sum.
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
                TotalOperations = instrument ? Tracer.TotalOperations : 0,
                TotalBytesAllocated = bytes.Sum(),
                ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                Errors = errors,
                Output = capturedOutput.ToString(),
                ReturnValue = status == RunStatus.Completed ? Render(returnValue) : null
            };
        }
    }

    private static MethodInfo FindEntryPoint(Assembly assembly, string name)
    {
        var candidates = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == name && m.GetParameters().Length == 0)
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No public static parameterless method named '{name}' was found. " +
                $"Available: {string.Join(", ", assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)).Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct())}");

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

    private static TraceResult Failure(RunStatus status, IReadOnlyList<string> errors) => new()
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
