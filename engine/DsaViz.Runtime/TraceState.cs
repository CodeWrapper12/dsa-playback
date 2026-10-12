using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace DsaViz.Runtime;

/// <summary>
/// Thrown when instrumented code executes more work than its budget allows. Every
/// instrumented statement and every charged library call checks the budget, which
/// gives a safe interrupt point without aborting a thread.
/// </summary>
public sealed class ExecutionBudgetExceededException : Exception
{
    public long Budget { get; }

    public ExecutionBudgetExceededException(long budget)
        : base($"Execution budget of {budget.ToString("N0", CultureInfo.InvariantCulture)} operations was exceeded.")
        => Budget = budget;
}

/// <summary>
/// Thrown before the real stack overflows. A genuine StackOverflowException cannot be
/// caught in .NET and takes the whole process down with it, so a missing base case —
/// one of the commonest mistakes while practising recursion — would otherwise end in a
/// crash with no explanation.
/// </summary>
public sealed class RecursionLimitExceededException : Exception
{
    public int Limit { get; }

    public RecursionLimitExceededException(int limit)
        : base($"Recursion went deeper than {limit.ToString("N0", CultureInfo.InvariantCulture)} calls. " +
               "That usually means a missing or unreachable base case — or an input large enough that this " +
               "recursion should be rewritten as a loop.")
        => Limit = limit;
}

/// <summary>
/// Thrown into a run that was abandoned after timing out, the next time it reaches an
/// instrumented statement. A thread cannot be aborted from outside, but it can be made
/// to unwind itself at the first opportunity.
/// </summary>
public sealed class RunAbandonedException : Exception
{
    public RunAbandonedException() : base("This run was abandoned after it exceeded its time limit.") { }
}

/// <summary>How a charged library call's cost grows with the size it is applied to.</summary>
public static class CostKind
{
    /// <summary>Proportional to the size — List.Contains, string.IndexOf.</summary>
    public const int Linear = 1;

    /// <summary>n log n — sorting.</summary>
    public const int Linearithmic = 2;

    /// <summary>log n — heap and balanced-tree operations, binary search.</summary>
    public const int Logarithmic = 3;

    /// <summary>
    /// Elements after an index have to move — List.Insert, List.RemoveAt. The cost is
    /// size minus index, which is why RemoveAt(Count - 1) is O(1) and RemoveAt(0) is
    /// O(n). Charging the full size for both would teach the wrong lesson.
    /// </summary>
    public const int Shift = 4;

    /// <summary>The cost is an explicit count argument — Substring(start, length).</summary>
    public const int CountArgument = 5;
}

/// <summary>
/// Watches a step-through run. The tracer calls it before every statement with the
/// variables in scope, and as every value is returned.
///
/// It sees live objects, not renderings, so it can follow references: two variables
/// holding the same list are one list, which is the whole difference between a linked
/// list that makes sense on screen and one that does not. Whatever it allocates is kept
/// out of the solution's numbers by the tracer.
/// </summary>
public interface IStepObserver
{
    /// <param name="allocatedBytes">What the solution had allocated before this statement.</param>
    void Step(int statementId, int depth, object?[] values, long allocatedBytes);

    void Returned(int depth, object? value);
}

public sealed record TraceSnapshot(
    long[] Hits,
    long[] Bytes,
    long[] LibraryWork,
    long[] SiteCalls,
    long[] SiteUnits,
    long TotalOperations,
    int MaxDepth)
{
    /// <summary>The most any single execution of each statement allocated.</summary>
    public long[] LargestAllocation { get; init; } = Array.Empty<long>();
}

/// <summary>
/// Per-statement execution counts, allocation, library work and call depth for one run.
///
/// Instrumented code reaches its state through a static field in its own compiled
/// assembly — <c>__DsaVizTrace.S</c> — rather than through a shared static or
/// thread-local storage. That one choice settles two problems at once. It keeps the hot
/// path to a plain static field load, where thread-local access was measured at roughly
/// 40× the cost on macOS and made exponential solutions time out. And it isolates
/// runs: a solution abandoned after a timeout can only ever reach its own assembly's
/// slot, which is then revoked, so it cannot write into any other measurement.
/// </summary>
public sealed class TraceState
{
    /// <summary>
    /// Deep enough for legitimate recursion well beyond the default sizes — a skewed tree
    /// at n = 1,000 recurses 1,001 deep — and shallow enough to fail fast. Stopping runaway
    /// recursion means unwinding every frame, and .NET records stack-trace data per frame as
    /// it does: at 100,000 frames that took about eight seconds per failure.
    /// </summary>
    public const int DefaultDepthLimit = 20_000;

    /// <summary>The slot's value outside a run: every call is a no-op.</summary>
    public static readonly TraceState Inactive = new(active: false, revoked: false);

    /// <summary>The slot's value after a timeout: every call unwinds the abandoned thread.</summary>
    public static readonly TraceState Revoked = new(active: false, revoked: true);

    private bool _active;
    private readonly bool _revoked;

    private long[] _hits = Array.Empty<long>();
    private long[] _bytes = Array.Empty<long>();
    private long[] _largestAllocation = Array.Empty<long>();
    private long[] _largestSuspectAllocation = Array.Empty<long>();
    private int _collectionsAtLastFlush;
    private long[] _libraryWork = Array.Empty<long>();
    private long[] _siteCalls = Array.Empty<long>();
    private long[] _siteUnits = Array.Empty<long>();
    private long[] _shiftSize = Array.Empty<long>();
    private int[] _callerStatement = Array.Empty<int>();
    private long _budget;
    private long _totalOperations;
    private int _depth;
    private int _maxDepth;
    private int _depthLimit;

    // Allocation is attributed to the *pending* statement — the one whose Hit came last —
    // because Hit(n) runs before statement n executes. GetAllocatedBytesForCurrentThread
    // is a monotonic count of bytes allocated, so bytes are never moved between statements.
    //
    // It is exact with one exception, found under parallel load: a garbage collection
    // triggered by *another* thread retires this thread's half-used allocation block and
    // counts the unused remainder — up to about 8 KB — as allocated by whichever statement
    // is running. Collections during the run are therefore counted, so the runner can tell
    // a clean measurement from one that may carry that phantom, and repeat it.
    private long _allocatedAtLastHit;
    private int _pendingStatement = -1;
    private int _collectionsAtBegin;

    // Step recording, used only by the step-through build. The measurement build never
    // calls Step, so none of this can affect a measured number.
    private IStepObserver? _observer;
    private int _maxSteps;
    private int _recordedSteps;
    private bool _truncated;
    private long _allocatedBeforeCapture;

    /// <summary>Everything the solution has allocated so far, as charged to its statements.</summary>
    private long _solutionBytes;

    private static readonly ConcurrentDictionary<Type, Func<object, int>?> CountReaders = new();

    private TraceState(bool active, bool revoked)
    {
        _active = active;
        _revoked = revoked;
    }

    /// <summary>Must be called on the thread that will run the solution: the allocation baseline is per thread.</summary>
    public static TraceState Begin(int statementCount, int siteCount, long budget, int depthLimit = DefaultDepthLimit)
    {
        if (statementCount < 0) throw new ArgumentOutOfRangeException(nameof(statementCount));
        if (siteCount < 0) throw new ArgumentOutOfRangeException(nameof(siteCount));
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
        if (depthLimit <= 0) throw new ArgumentOutOfRangeException(nameof(depthLimit));

        var state = new TraceState(active: true, revoked: false)
        {
            _hits = new long[statementCount],
            _bytes = new long[statementCount],
            _largestAllocation = new long[statementCount],
            _largestSuspectAllocation = new long[statementCount],
            _libraryWork = new long[statementCount],
            _siteCalls = new long[siteCount],
            _siteUnits = new long[siteCount],
            _shiftSize = new long[siteCount],
            _callerStatement = new int[depthLimit + 2],
            _budget = budget,
            _depthLimit = depthLimit
        };

        // Last, so the arrays allocated above are not charged to the solution.
        state._collectionsAtBegin = GC.CollectionCount(0);
        state._collectionsAtLastFlush = state._collectionsAtBegin;
        state._allocatedAtLastHit = GC.GetAllocatedBytesForCurrentThread();
        return state;
    }

    /// <summary>
    /// Begins a run that also records every step. Only the step-through build calls Step,
    /// and it runs at a small n, so the cost of capturing every variable at every
    /// statement is paid where it is wanted and nowhere else.
    /// </summary>
    public static TraceState BeginStepping(
        int statementCount, int siteCount, long budget, int maxSteps,
        IStepObserver observer, int depthLimit = DefaultDepthLimit)
    {
        if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        var state = Begin(statementCount, siteCount, budget, depthLimit);
        state._observer = observer ?? throw new ArgumentNullException(nameof(observer));
        state._maxSteps = maxSteps;
        state._allocatedAtLastHit = GC.GetAllocatedBytesForCurrentThread();
        return state;
    }

    /// <summary>The statement running now: the one whose Hit came last. -1 outside a run.</summary>
    public int PendingStatement => _pendingStatement;

    /// <summary>True when more steps ran than were kept.</summary>
    public bool StepsTruncated => _truncated;

    /// <summary>
    /// Set when the observer failed. Recording stops there, so what was recorded is
    /// consistent; the run itself carries on unaffected.
    /// </summary>
    public Exception? ObserverFailure { get; private set; }

    /// <summary>
    /// The first half of a step: everything Hit does, so the budget and depth limits still
    /// apply. Instrumented code calls it as <c>Values(Step(id), new object[] { … })</c>, so
    /// it runs before the array of variables is built — which is what lets that array, and
    /// every value boxed into it, be kept out of the solution's allocation.
    /// </summary>
    public int Step(int statementId)
    {
        Hit(statementId);
        _allocatedBeforeCapture = GC.GetAllocatedBytesForCurrentThread();
        return statementId;
    }

    /// <summary>The second half of a step: hands the variables to the observer.</summary>
    public void Values(int statementId, object?[] values)
    {
        if (!_active) return;
        if (_observer is not null && ObserverFailure is null)
        {
            if (_recordedSteps >= _maxSteps) _truncated = true;
            else
            {
                _recordedSteps++;
                // Observing a run must never be able to break it. A failure here is a bug
                // in the recorder, and letting it escape would report it as an error in
                // the student's own code — the most misleading thing this tool could do.
                try { _observer.Step(statementId, _depth, values, _solutionBytes); }
                catch (Exception ex) { ObserverFailure = ex; }
            }
        }
        // Nothing but reading variables ran since Step, so everything allocated since —
        // the array, the boxes, the observer's own work — belongs to the tracer.
        _allocatedAtLastHit += GC.GetAllocatedBytesForCurrentThread() - _allocatedBeforeCapture;
    }

    /// <summary>
    /// Wraps every value a method returns in the step build, so the recursion tree can show
    /// what each call gave back. Returns the value unchanged.
    /// </summary>
    public T Returned<T>(T value)
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return value; }
        if (_observer is null || ObserverFailure is not null || _truncated) return value;

        long before = GC.GetAllocatedBytesForCurrentThread();
        try { _observer.Returned(_depth, value); }
        catch (Exception ex) { ObserverFailure = ex; }
        ExcludeOwnAllocation(before);
        return value;
    }

    /// <summary>Called by instrumented code immediately before each statement.</summary>
    public void Hit(int statementId)
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return; }

        FlushAllocation();
        _pendingStatement = statementId;

        if ((uint)statementId < (uint)_hits.Length) _hits[statementId]++;
        if (++_totalOperations > _budget) throw new ExecutionBudgetExceededException(_budget);
    }

    /// <summary>
    /// Called on entry to every instrumented method. Remembers which statement in the
    /// caller made the call, so that when the callee returns, anything the rest of that
    /// statement allocates is charged to the caller rather than to the callee's last line.
    /// </summary>
    public void Enter()
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return; }

        _callerStatement[_depth] = _pendingStatement;
        _depth++;
        if (_depth > _maxDepth) _maxDepth = _depth;
        if (_depth > _depthLimit) throw new RecursionLimitExceededException(_depthLimit);
    }

    /// <summary>
    /// Called from a finally block on every exit from an instrumented method. Never
    /// throws, even when revoked: it runs during unwinding, and throwing here would
    /// replace whatever exception is already in flight.
    /// </summary>
    public void Exit()
    {
        if (!_active || _depth <= 0) return;

        FlushAllocation();
        _depth--;
        _pendingStatement = _callerStatement[_depth];
    }

    /// <summary>
    /// Charges a library call by the size of the object it operates on, then hands that
    /// object back unchanged so the original call proceeds exactly as written. Evaluating
    /// the receiver once, here, is what keeps the rewrite free of double evaluation.
    /// </summary>
    public T Through<T>(T value, int site, int kind) where T : class?
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return value; }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long size = SizeOf(value);
        ExcludeOwnAllocation(before);

        if (kind == CostKind.Shift) _shiftSize[site] = size;   // charged once the index is known
        else Charge(site, UnitsFor(kind, size));

        return value;
    }

    /// <summary>
    /// Charges by the size of <paramref name="measured"/> and returns <paramref name="value"/>.
    /// Used for <c>text += piece</c>, where the cost is the length of what is already there
    /// but the expression that must be preserved is the piece being added.
    /// </summary>
    public T Before<T>(object? measured, int site, int kind, T value)
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return value; }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long size = SizeOf(measured);
        ExcludeOwnAllocation(before);

        Charge(site, UnitsFor(kind, size));
        return value;
    }

    /// <summary>Completes a Shift charge: the elements after the index are the ones that move.</summary>
    public int ShiftIndex(int index, int site)
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return index; }
        Charge(site, Math.Max(0, _shiftSize[site] - index));
        return index;
    }

    /// <summary>Charges an explicit count — the length argument of Substring, GetRange, Copy.</summary>
    public int Units(int count, int site)
    {
        if (!_active) { if (_revoked) throw new RunAbandonedException(); return count; }
        Charge(site, Math.Max(0, count));
        return count;
    }

    /// <summary>Flushes the allocation owed to the last statement. Call on the run's own thread.</summary>
    public void End()
    {
        if (!_active) return;
        FlushAllocation();
        CollectionsDuringRun = GC.CollectionCount(0) - _collectionsAtBegin;
        _pendingStatement = -1;
        _active = false;
    }

    /// <summary>Garbage collections, of any generation, that happened while this run was traced.</summary>
    public int CollectionsDuringRun { get; private set; }

    public TraceSnapshot Snapshot() => new(
        (long[])_hits.Clone(), (long[])_bytes.Clone(), (long[])_libraryWork.Clone(),
        (long[])_siteCalls.Clone(), (long[])_siteUnits.Clone(), _totalOperations, _maxDepth)
    {
        // A clean execution's figure wherever one exists; a suspect one only for a
        // statement whose every allocating execution saw a collection.
        LargestAllocation = _largestAllocation
            .Select((clean, id) => clean > 0 ? clean : _largestSuspectAllocation[id])
            .ToArray()
    };

    /// <summary>
    /// Charges what was allocated since the last flush to the pending statement, and
    /// records the largest single amount. The largest single allocation is what separates
    /// two programs with the same total: one that builds an n-element structure once, and
    /// one that throws away an n-element buffer on every pass. Same peak, very different
    /// total — and the difference is the "hoist it out of the loop" lesson.
    /// </summary>
    private void FlushAllocation()
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        if ((uint)_pendingStatement < (uint)_bytes.Length)
        {
            long delta = now - _allocatedAtLastHit;
            _bytes[_pendingStatement] += delta;
            _solutionBytes += delta;

            if (delta > 0)
            {
                // An execution during which a collection happened may carry the phantom
                // remainder of a retired allocation block — up to about 8 KB — on top of
                // what it really allocated. Across a whole run that is noise, but in a
                // single execution it can triple a 4 KB array. So such executions only
                // count as the largest when no clean execution of the statement exists.
                // Checked only when something was allocated, which keeps the cost off
                // the statements that allocate nothing.
                int collections = GC.CollectionCount(0);
                bool suspect = collections != _collectionsAtLastFlush;
                _collectionsAtLastFlush = collections;

                var largest = suspect ? _largestSuspectAllocation : _largestAllocation;
                if (delta > largest[_pendingStatement]) largest[_pendingStatement] = delta;
            }
        }
        _allocatedAtLastHit = now;
    }

    /// <summary>
    /// Anything the tracer itself allocated is moved out of the solution's account, so a
    /// statement that calls a charged library method still reports exactly what it
    /// allocated and nothing the measurement machinery did.
    /// </summary>
    private void ExcludeOwnAllocation(long before)
        => _allocatedAtLastHit += GC.GetAllocatedBytesForCurrentThread() - before;

    private void Charge(int site, long units)
    {
        _siteCalls[site]++;
        _siteUnits[site] += units;

        if ((uint)_pendingStatement < (uint)_libraryWork.Length)
            _libraryWork[_pendingStatement] += units;

        _totalOperations += units;
        if (_totalOperations > _budget) throw new ExecutionBudgetExceededException(_budget);
    }

    private static long UnitsFor(int kind, long size) => kind switch
    {
        CostKind.Linearithmic => size <= 1 ? size : (long)Math.Ceiling(size * Math.Log2(size)),
        CostKind.Logarithmic => size <= 1 ? 1 : (long)Math.Ceiling(Math.Log2(size + 1)),
        _ => size
    };

    /// <summary>
    /// The number of elements in whatever a library call is operating on. Common shapes
    /// are matched directly; anything else exposing an int Count is read through a
    /// delegate compiled once per type. A lazy sequence has no size without enumerating
    /// it, which would change what the program does, so it reports zero.
    /// </summary>
    public static long SizeOf(object? value)
    {
        switch (value)
        {
            case null: return 0;
            case string text: return text.Length;
            case System.Text.StringBuilder builder: return builder.Length;
            case Array array: return array.Length;
            case ICollection collection: return collection.Count;
        }

        var reader = CountReaders.GetOrAdd(value.GetType(), BuildCountReader);
        return reader is null ? 0 : reader(value);
    }

    private static Func<object, int>? BuildCountReader(Type type)
    {
        var property = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(int) || property.GetIndexParameters().Length > 0)
            return null;

        try
        {
            var parameter = Expression.Parameter(typeof(object), "value");
            var body = Expression.Property(Expression.Convert(parameter, type), property);
            return Expression.Lambda<Func<object, int>>(body, parameter).Compile();
        }
        catch (Exception)
        {
            // A type the expression compiler cannot reach — non-public, for instance.
            // Reflection still works; it is slower, and its boxing is excluded from the
            // solution's allocation by the caller either way.
            return value => (int)property.GetValue(value)!;
        }
    }
}
