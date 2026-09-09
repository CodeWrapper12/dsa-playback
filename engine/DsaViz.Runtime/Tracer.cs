namespace DsaViz.Runtime;

/// <summary>
/// Thrown when instrumented code executes more statements than its budget allows.
/// This is how a runaway or infinite loop is stopped: every instrumented statement
/// calls <see cref="Tracer.Hit"/>, so the budget check runs at every line and gives
/// us a safe interrupt point without needing to abort a thread.
/// </summary>
public sealed class ExecutionBudgetExceededException : Exception
{
    public long Budget { get; }

    public ExecutionBudgetExceededException(long budget)
        : base($"Execution budget of {budget:N0} statements was exceeded.")
        => Budget = budget;
}

/// <summary>
/// Collects per-statement execution counts and allocation deltas for instrumented code.
///
/// State is static because instrumented assemblies call into it by name. That makes
/// the tracer single-run: <see cref="Begin"/> resets everything, and a run must
/// complete before another starts. <see cref="Runner"/> enforces that with a lock.
/// </summary>
public static class Tracer
{
    private static long[] _hits = Array.Empty<long>();
    private static long[] _bytes = Array.Empty<long>();
    private static long _budget;
    private static long _totalOperations;

    // Allocation is attributed to the *previous* statement, because Hit(n) runs before
    // statement n executes: the bytes allocated between Hit(a) and Hit(b) belong to a.
    //
    // GetAllocatedBytesForCurrentThread is a monotonic cumulative counter of bytes
    // allocated, so a garbage collection landing between two hits cannot decrease it
    // and cannot shift bytes between statements. The deltas are exact.
    private static long _allocatedAtLastHit;
    private static int _pendingStatement = -1;

    public static void Begin(int statementCount, long budget)
    {
        if (statementCount < 0) throw new ArgumentOutOfRangeException(nameof(statementCount));
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));

        _hits = new long[statementCount];
        _bytes = new long[statementCount];
        _budget = budget;
        _totalOperations = 0;
        _pendingStatement = -1;
        _allocatedAtLastHit = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Called by instrumented code immediately before each statement.</summary>
    public static void Hit(int statementId)
    {
        long allocatedNow = GC.GetAllocatedBytesForCurrentThread();

        if (_pendingStatement >= 0)
            _bytes[_pendingStatement] += allocatedNow - _allocatedAtLastHit;

        _allocatedAtLastHit = allocatedNow;
        _pendingStatement = statementId;

        if ((uint)statementId < (uint)_hits.Length) _hits[statementId]++;

        if (++_totalOperations > _budget) throw new ExecutionBudgetExceededException(_budget);
    }

    /// <summary>Flushes the allocation still owed to the last executed statement.</summary>
    public static void End()
    {
        if (_pendingStatement >= 0)
        {
            _bytes[_pendingStatement] += GC.GetAllocatedBytesForCurrentThread() - _allocatedAtLastHit;
            _pendingStatement = -1;
        }
    }

    public static long TotalOperations => _totalOperations;
    public static long[] HitsSnapshot() => (long[])_hits.Clone();
    public static long[] BytesSnapshot() => (long[])_bytes.Clone();
}
