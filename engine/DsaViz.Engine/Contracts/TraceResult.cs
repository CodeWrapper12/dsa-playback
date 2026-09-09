namespace DsaViz.Engine.Contracts;

public enum RunStatus
{
    Completed,
    BudgetExceeded,
    CompileError,
    RuntimeError
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
    long BytesAllocated);

/// <summary>
/// Per-line totals, summed across every statement on the line.
/// <paramref name="StatementCount"/> tells the UI when a line needs expanding: any
/// value above one means the total is a sum and the breakdown is the real story.
/// </summary>
public sealed record LineCost(int Line, long Hits, long BytesAllocated, int StatementCount);

public sealed record TraceResult
{
    public required RunStatus Status { get; init; }

    /// <summary>Executed statements, finest granularity available.</summary>
    public required IReadOnlyList<StatementCost> Statements { get; init; }

    /// <summary>The same costs aggregated per source line.</summary>
    public required IReadOnlyList<LineCost> Lines { get; init; }

    public required long TotalOperations { get; init; }
    public required long TotalBytesAllocated { get; init; }
    public required double ElapsedMs { get; init; }

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
