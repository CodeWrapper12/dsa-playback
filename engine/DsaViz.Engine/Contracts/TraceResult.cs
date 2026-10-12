namespace DsaViz.Engine.Contracts;

public enum RunStatus
{
    Completed,
    BudgetExceeded,
    CompileError,
    RuntimeError,

    /// <summary>Recursion passed the depth limit — usually a missing base case.</summary>
    RecursionLimitExceeded,

    /// <summary>
    /// The run did not finish within the wall-clock limit. The statement budget cannot
    /// catch this case: a single statement — a sleep, or one enormous library call —
    /// can run for as long as it likes without ever reaching the next tracer call.
    /// </summary>
    TimedOut
}

/// <summary>Where an instrumented statement sits in the original source.</summary>
public sealed record StatementInfo(int Id, int Line, int Column, int Length, string Snippet);

/// <summary>
/// The cost of one statement. Several statements can share a line, which is why this
/// exists: a line total alone cannot say whether 3,540 visits were one statement run
/// 3,540 times or two statements run 1,770 times each.
/// </summary>
public sealed record StatementCost(
    int Id,
    int Line,
    int Column,
    int Length,
    string Snippet,
    long Hits,
    long BytesAllocated)
{
    /// <summary>
    /// Work done inside library calls made by this statement — the scanning inside
    /// List.Contains, the copying inside string concatenation. A statement that runs
    /// 1,000 times can cost 500,000 because of what it calls, and an execution count on
    /// its own would hide exactly that.
    /// </summary>
    public long LibraryWork { get; init; }
}

/// <summary>Where a costed library call sits, and what it is.</summary>
public sealed record LibraryCallSite(int Site, int Line, int Column, string Member, string Cost);

/// <summary>A costed library call as it actually behaved during a run.</summary>
public sealed record LibraryCall(
    int Site, int Line, int Column, string Member, string Cost, long Calls, long Work);

/// <summary>
/// Per-line totals, summed across every statement on the line.
/// <paramref name="StatementCount"/> tells the UI when a line needs expanding: any
/// value above one means the total is a sum and the breakdown is the real story.
/// </summary>
public sealed record LineCost(int Line, long Hits, long BytesAllocated, int StatementCount)
{
    public long LibraryWork { get; init; }
}

public sealed record TraceResult
{
    public required RunStatus Status { get; init; }

    /// <summary>Executed statements, finest granularity available.</summary>
    public required IReadOnlyList<StatementCost> Statements { get; init; }

    /// <summary>The same costs aggregated per source line.</summary>
    public required IReadOnlyList<LineCost> Lines { get; init; }

    /// <summary>Statements executed plus library work — the figure the growth curve is fitted to.</summary>
    public required long TotalOperations { get; init; }
    public required long TotalBytesAllocated { get; init; }
    public required double ElapsedMs { get; init; }

    /// <summary>Statements executed, without library work.</summary>
    public long TotalStatements { get; init; }

    /// <summary>The deepest the call stack went, counting only the solution's own methods.</summary>
    public int MaxRecursionDepth { get; init; }

    public IReadOnlyList<LibraryCall> LibraryCalls { get; init; } = Array.Empty<LibraryCall>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string Output { get; init; } = "";
    public string? ReturnValue { get; init; }

    /// <summary>
    /// What the byte counts mean, for the UI to show verbatim rather than inventing a
    /// caption. The numbers are exact — verified against known object sizes — so the
    /// honest caveats are about scope, not precision.
    /// </summary>
    public string MemoryModel =>
        "Bytes allocated on the GC heap by the executing thread, measured exactly per statement. " +
        "This counts allocation, not retention: a temporary that is immediately garbage still " +
        "appears. Stack allocations and any work on other threads are not counted.";
}
