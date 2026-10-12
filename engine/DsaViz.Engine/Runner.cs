using System.Runtime.ExceptionServices;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Compilation;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Instrumentation;
using DsaViz.Runtime;

namespace DsaViz.Engine;

/// <summary>
/// A compiled solution, ready to be invoked repeatedly.
///
/// Compilation is by far the expensive step, and a growth curve runs the same solution
/// at half a dozen sizes. Separating compile from invoke keeps that cost paid once.
/// </summary>
/// <summary>Record a step-through: who watches the run, and how many steps to keep.</summary>
public sealed record StepRecording(IStepObserver Observer, int MaxSteps);

public sealed class CompiledSolution : IDisposable
{
    private readonly RunContext _context;

    internal CompiledSolution(
        RunContext context, MethodInfo entry, IReadOnlyList<StatementInfo> statements,
        IReadOnlyList<LibraryCallSite> sites, FieldInfo? slot)
    {
        _context = context;
        Entry = entry;
        Statements = statements;
        Sites = sites;
        Slot = slot;
    }

    /// <summary>The assembly's own tracer slot; null for an uninstrumented baseline build.</summary>
    internal FieldInfo? Slot { get; }

    /// <summary>
    /// Set once a run of this solution has been abandoned after a timeout. Its thread may
    /// still be running somewhere, and it reaches the tracer through this assembly's slot,
    /// so this assembly is never run again. A further measurement has to recompile.
    /// </summary>
    public bool Abandoned { get; private set; }

    internal void Abandon()
    {
        Abandoned = true;
        Slot?.SetValue(null, TraceState.Revoked);
    }

    public MethodInfo Entry { get; }
    public IReadOnlyList<StatementInfo> Statements { get; }
    public IReadOnlyList<LibraryCallSite> Sites { get; }
    public bool IsInstrumented => Slot is not null;
    public ParameterInfo[] Parameters => Entry.GetParameters();

    /// <summary>
    /// A fresh instance for every run, the way LeetCode constructs a new Solution per test.
    /// Reusing one would let a field left over from the previous size leak into the next
    /// measurement.
    /// </summary>
    internal object? CreateTarget() => Entry.IsStatic ? null : Activator.CreateInstance(Entry.DeclaringType!);

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

    /// <summary>
    /// Large enough that legitimately deep recursion — a skewed tree of tens of thousands
    /// of nodes — runs normally. The reservation is virtual address space, not memory, so
    /// it costs nothing until a solution actually recurses that far.
    /// </summary>
    public const int StackSize = 256 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Console capture is process-wide, so runs are serialised even though tracer state
    // is per thread.
    private static readonly object RunLock = new();

    public static CompileOutcome Compile(string source, string entryMethod, bool instrument = true)
        => Compile(SourceAnalysis.Create(source), entryMethod, instrument);

    public static CompileOutcome Compile(SourceAnalysis analysis, string entryMethod, bool instrument = true, bool stepping = false)
    {
        // Errors come from the user's own file, so the line numbers are the ones they see.
        if (analysis.HasErrors)
            return new CompileOutcome(null, RunStatus.CompileError, analysis.Errors);

        IReadOnlyList<Microsoft.CodeAnalysis.SyntaxTree> trees = new[] { analysis.Tree };
        IReadOnlyList<StatementInfo> statements = Array.Empty<StatementInfo>();
        IReadOnlyList<LibraryCallSite> sites = Array.Empty<LibraryCallSite>();

        if (instrument)
        {
            var instrumented = Instrumenter.Instrument(analysis, stepping);
            trees = instrumented.Trees;
            statements = instrumented.Statements;
            sites = instrumented.Sites;
        }

        var compiled = InMemoryCompiler.Compile(trees, $"Solution_{Guid.NewGuid():N}");
        if (!compiled.Success)
        {
            // The original compiled, so this is a defect in the instrumentation rather
            // than in the user's code. Saying so plainly matters: reporting it as their
            // compile error would send them hunting for a mistake they did not make.
            var errors = new List<string>
            {
                "Internal error: the solution compiles, but the instrumented version of it does not. " +
                "This is a bug in DSA Visualizer, not in your code."
            };
            errors.AddRange(compiled.Errors.Take(5));
            return new CompileOutcome(null, RunStatus.RuntimeError, errors);
        }

        var context = new RunContext();
        try
        {
            using var stream = new MemoryStream(compiled.Assembly!);
            var assembly = context.LoadFromStream(stream);
            var entry = FindEntryPoint(assembly, entryMethod);
            var slot = instrument
                ? assembly.GetType(TraceCalls.HolderType)?.GetField(TraceCalls.SlotField)
                  ?? throw new InvalidOperationException("The instrumented assembly has no tracer slot.")
                : null;
            return new CompileOutcome(
                new CompiledSolution(context, entry, statements, sites, slot),
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
        CompiledSolution solution,
        object?[]? arguments = null,
        long budget = DefaultBudget,
        TimeSpan? timeout = null,
        int depthLimit = TraceState.DefaultDepthLimit,
        StepRecording? recording = null)
    {
        if (solution.Abandoned)
            return Failure(RunStatus.RuntimeError, new[]
            {
                "This compiled solution was abandoned after an earlier run timed out, and cannot be run again."
            });

        lock (RunLock)
        {
            var capturedOutput = new StringWriter(CultureInfo.InvariantCulture);
            var previousOut = Console.Out;
            Console.SetOut(capturedOutput);

            var outcome = new RunOutcome();
            var worker = new Thread(() => Execute(solution, arguments, budget, depthLimit, recording, outcome), StackSize)
            {
                IsBackground = true,
                Name = "DsaViz solution",
                CurrentCulture = CultureInfo.InvariantCulture,
                CurrentUICulture = CultureInfo.InvariantCulture
            };

            var limit = timeout ?? DefaultTimeout;
            worker.Start();
            bool finished = worker.Join(limit);

            Console.SetOut(previousOut);

            if (!finished)
            {
                // A thread cannot be aborted in .NET Core. It is abandoned instead: it is a
                // background thread, so it cannot keep the process alive; its assembly's
                // slot is revoked, so the next instrumented statement it reaches unwinds it;
                // and nothing else reads that slot, so it cannot write into any later run.
                solution.Abandon();
                return Failure(RunStatus.TimedOut, new[]
                {
                    $"The run did not finish within {limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} " +
                    "seconds. The statement budget cannot stop a single statement that never returns — " +
                    "a sleep, a blocking call, or one enormous library call."
                });
            }

            return Collect(solution, outcome, capturedOutput.ToString());
        }
    }

    private sealed class RunOutcome
    {
        public RunStatus Status = RunStatus.Completed;
        public readonly List<string> Errors = new();
        public object? ReturnValue;
        public double ElapsedMs;
        public TraceState? State;
        public int? ErrorLine;
        public Exception? Thrown;
        public int ThrownAt = -1;
    }

    private static void Execute(
        CompiledSolution solution, object?[]? arguments, long budget, int depthLimit, StepRecording? recording, RunOutcome outcome)
    {
        try
        {
            // Construction is setup, not the solution's work, so it happens before tracing
            // starts — the slot is still Inactive, and constructor statements are no-ops.
            var target = solution.CreateTarget();

            TraceState? state = null;
            if (solution.Slot is { } slot)
            {
                // Begun on this thread: the allocation baseline is per thread.
                state = recording is not null
                    ? TraceState.BeginStepping(solution.Statements.Count, solution.Sites.Count, budget, recording.MaxSteps,
                        recording.Observer, depthLimit)
                    : TraceState.Begin(solution.Statements.Count, solution.Sites.Count, budget, depthLimit);
                outcome.State = state;
                slot.SetValue(null, state);
            }

            // Which statement threw is only knowable while the exception is being thrown:
            // by the time it reaches this method, every frame has unwound and the tracer has
            // moved back to the outermost call. So the statement is noted at the throw, on
            // this thread only, and matched by identity to whatever finally escapes.
            int runThread = Environment.CurrentManagedThreadId;
            EventHandler<FirstChanceExceptionEventArgs> onThrow = (_, e) =>
            {
                if (Environment.CurrentManagedThreadId != runThread || state is null) return;
                // Reflection wraps what escapes the solution in a TargetInvocationException,
                // thrown on this same thread after the real one. Noting it would overwrite
                // the student's exception with the wrapper.
                if (e.Exception is TargetInvocationException) return;
                outcome.Thrown = e.Exception;
                outcome.ThrownAt = state.PendingStatement;
            };

            var stopwatch = Stopwatch.StartNew();
            AppDomain.CurrentDomain.FirstChanceException += onThrow;
            try
            {
                outcome.ReturnValue = solution.Entry.Invoke(target, arguments);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= onThrow;
                stopwatch.Stop();
                outcome.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                if (state is not null)
                {
                    state.End();
                    if (!solution.Abandoned) solution.Slot!.SetValue(null, TraceState.Inactive);
                }
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ExecutionBudgetExceededException budgetEx)
        {
            outcome.Status = RunStatus.BudgetExceeded;
            outcome.Errors.Add(budgetEx.Message);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is RecursionLimitExceededException depthEx)
        {
            outcome.Status = RunStatus.RecursionLimitExceeded;
            outcome.Errors.Add(depthEx.Message);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } thrown)
        {
            // The student's own exception, said the way they would want to hear it. The
            // stack trace is left out: its frames are the instrumented copy, which has no
            // line numbers and names helpers the student never wrote.
            outcome.Status = RunStatus.RuntimeError;
            int? line = ReferenceEquals(thrown, outcome.Thrown) &&
                        outcome.ThrownAt is >= 0 and var at && at < solution.Statements.Count
                ? solution.Statements[at].Line
                : null;
            outcome.ErrorLine = line;
            outcome.Errors.Add(
                $"Your code threw {thrown.GetType().Name}{(line is { } l ? $" at line {l}" : "")}: {thrown.Message}");
        }
        catch (TargetInvocationException ex)
        {
            outcome.Status = RunStatus.RuntimeError;
            outcome.Errors.Add(ex.ToString());
        }
        catch (Exception ex)
        {
            outcome.Status = RunStatus.RuntimeError;
            outcome.Errors.Add(ex.Message);
        }
    }

    private static TraceResult Collect(CompiledSolution solution, RunOutcome outcome, string output)
    {
        var snapshot = outcome.State?.Snapshot();
        var hits = snapshot?.Hits ?? Array.Empty<long>();
        var bytes = snapshot?.Bytes ?? Array.Empty<long>();
        var work = snapshot?.LibraryWork ?? Array.Empty<long>();
        var largest = snapshot?.LargestAllocation ?? Array.Empty<long>();

        var statements = new List<StatementCost>();
        for (int id = 0; id < hits.Length && id < solution.Statements.Count; id++)
        {
            if (hits[id] == 0) continue;
            var info = solution.Statements[id];
            statements.Add(new StatementCost(
                info.Id, info.Line, info.Column, info.Length, info.Snippet, hits[id], bytes[id])
            {
                LibraryWork = work[id],
                LargestAllocation = id < largest.Length ? largest[id] : 0
            });
        }

        // Source order, so a report reads like the file rather than like the order the
        // rewriter happened to visit nodes in.
        statements.Sort((left, right) => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Column.CompareTo(right.Column));

        var lines = LinesOf(statements);

        var libraryCalls = new List<LibraryCall>();
        if (snapshot is not null)
        {
            foreach (var site in solution.Sites)
            {
                if (site.Site >= snapshot.SiteCalls.Length || snapshot.SiteCalls[site.Site] == 0) continue;
                libraryCalls.Add(new LibraryCall(site.Site, site.Line, site.Column, site.Member, site.Cost,
                    snapshot.SiteCalls[site.Site], snapshot.SiteUnits[site.Site]));
            }
        }

        return new TraceResult
        {
            Status = outcome.Status,
            Statements = statements,
            Lines = lines,
            TotalOperations = snapshot?.TotalOperations ?? 0,
            TotalStatements = hits.Sum(),
            TotalBytesAllocated = bytes.Sum(),
            MaxRecursionDepth = snapshot?.MaxDepth ?? 0,
            CollectionsDuringRun = outcome.State?.CollectionsDuringRun ?? 0,
            LibraryCalls = libraryCalls,
            ElapsedMs = outcome.ElapsedMs,
            Errors = outcome.Errors,
            Output = output,
            ReturnValue = outcome.Status == RunStatus.Completed ? Render(outcome.ReturnValue) : null,
            ReturnObject = outcome.Status == RunStatus.Completed ? outcome.ReturnValue : null,
            ErrorLine = outcome.ErrorLine,
            StepsTruncated = outcome.State?.StepsTruncated ?? false,
            RecorderFailure = outcome.State?.ObserverFailure?.ToString()
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

    private static TraceResult CompileAndInvoke(string source, string entryMethod, bool instrument, long budget)
    {
        // Each attempt gets a fresh compile, so static state cannot carry over between them.
        return Cleanest(() =>
        {
            var outcome = Compile(source, entryMethod, instrument);
            if (!outcome.Success) return Failure(outcome.Status, outcome.Errors);
            using var solution = outcome.Solution!;
            return Invoke(solution, arguments: null, budget);
        });
    }

    /// <summary>Above this, an 8 KB phantom is under 1% of the reading and not worth repeating for.</summary>
    public const long PhantomSensitiveBytes = 1_024 * 1_024;

    private const int MaxAttempts = 5;

    /// <summary>
    /// Repeats a run that allocated little and keeps the smallest reading. Noise in an
    /// allocation count can only ever add bytes, never remove real ones — a collection set
    /// off by another thread, or other work the runtime does on this one — so for a
    /// deterministic solution the smallest reading is the faithful one.
    ///
    /// A small run is always measured twice, not only when a collection was counted: on a
    /// busy CI machine a merge sort's allocation curve — 64 bytes to 4 KB across the sizes —
    /// came out irregular in one run of the suite and clean in the next, and one stray
    /// reading of a few kilobytes is enough to break a curve that small. A run that saw a
    /// collection is repeated up to <see cref="MaxAttempts"/> times.
    /// <paramref name="attempt"/> must build fresh inputs each time: many solutions change
    /// their arguments in place.
    /// </summary>
    public static TraceResult Cleanest(Func<TraceResult> attempt)
    {
        var best = attempt();
        for (int i = 1; i < MaxAttempts && NeedsAnotherAttempt(best, i); i++)
        {
            var next = attempt();
            if (next.Status != best.Status) break;     // behaving differently is not noise
            if (next.TotalBytesAllocated < best.TotalBytesAllocated ||
                next.TotalBytesAllocated == best.TotalBytesAllocated && next.CollectionsDuringRun < best.CollectionsDuringRun)
                best = next;
        }
        return best;
    }

    private static bool NeedsAnotherAttempt(TraceResult trace, int attemptsSoFar)
        => trace.Status == RunStatus.Completed
           && trace.TotalBytesAllocated < PhantomSensitiveBytes
           && (attemptsSoFar < MinimumSmallRunAttempts || trace.CollectionsDuringRun > 0);

    /// <summary>A small run is measured at least this many times.</summary>
    private const int MinimumSmallRunAttempts = 2;

    /// <summary>
    /// Accepts "Method" or "Type.Method". Instance methods are measured too: LeetCode's C#
    /// template is <c>public class Solution { public int[] TwoSum(...) }</c>, and a tool
    /// that only finds static methods cannot run anything written in that template.
    /// </summary>
    private static MethodInfo FindEntryPoint(Assembly assembly, string name)
    {
        string? typeName = null;
        string methodName = name;
        int dot = name.LastIndexOf('.');
        if (dot > 0) { typeName = name[..dot]; methodName = name[(dot + 1)..]; }

        var all = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && !m.IsAbstract && !m.IsGenericMethodDefinition)
            .ToList();

        var candidates = all
            .Where(m => m.Name == methodName && (typeName is null || m.DeclaringType?.Name == typeName))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No public method named '{name}' was found. Available: " +
                string.Join(", ", all.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct()));

        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"'{name}' is ambiguous — found in {string.Join(", ", candidates.Select(c => c.DeclaringType?.Name).Distinct())}. " +
                "Name it as Type.Method.");

        var entry = candidates[0];
        if (!entry.IsStatic && entry.DeclaringType!.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException(
                $"{entry.DeclaringType.Name}.{entry.Name} is an instance method, so the engine has to construct " +
                $"{entry.DeclaringType.Name}, and it has no public parameterless constructor.");

        return entry;
    }

    /// <summary>
    /// Renders a value deterministically so instrumented and baseline runs can be
    /// compared — the check behind criterion 8 — and so a returned tree or list reads the
    /// way LeetCode prints it.
    /// </summary>
    public static string? Render(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;

        var type = value.GetType();
        if (NodeShapes.AsTree(type) is { } tree) return RenderTree(value, tree);
        if (NodeShapes.AsList(type) is { } list) return RenderList(value, list);

        if (value is IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var item in enumerable) parts.Add(Render(item) ?? "null");
            return "[" + string.Join(", ", parts) + "]";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private const int RenderNodeLimit = 10_000;

    private static string RenderTree(object root, TreeShape shape)
    {
        // Level order with nulls, trailing nulls trimmed: [3,9,20,null,null,15,7].
        var parts = new List<string>();
        var queue = new Queue<object?>();
        queue.Enqueue(root);

        while (queue.Count > 0 && parts.Count < RenderNodeLimit)
        {
            var node = queue.Dequeue();
            if (node is null) { parts.Add("null"); continue; }
            parts.Add(Convert.ToString(shape.Value.Get(node), CultureInfo.InvariantCulture)!);
            queue.Enqueue(shape.Left.Get(node));
            queue.Enqueue(shape.Right.Get(node));
        }

        while (parts.Count > 0 && parts[^1] == "null") parts.RemoveAt(parts.Count - 1);
        return "[" + string.Join(",", parts) + "]";
    }

    private static string RenderList(object head, ListShape shape)
    {
        var parts = new List<string>();
        for (object? node = head; node is not null && parts.Count < RenderNodeLimit; node = shape.Next.Get(node))
            parts.Add(Convert.ToString(shape.Value.Get(node), CultureInfo.InvariantCulture)!);

        // The limit also stops a cyclic list from rendering forever.
        return "[" + string.Join(",", parts) + (parts.Count == RenderNodeLimit ? ",…" : "") + "]";
    }

    /// <summary>
    /// Line totals are a convenience over the statement data, never a replacement for it:
    /// StatementCount is what tells the UI a total is a sum.
    /// </summary>
    internal static List<LineCost> LinesOf(IEnumerable<StatementCost> statements)
        => statements
            .GroupBy(statement => statement.Line)
            .OrderBy(group => group.Key)
            .Select(group => new LineCost(
                group.Key,
                group.Sum(statement => statement.Hits),
                group.Sum(statement => statement.BytesAllocated),
                group.Count())
            {
                LibraryWork = group.Sum(statement => statement.LibraryWork)
            })
            .ToList();

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
