namespace DsaViz.Engine.Contracts;

public enum RunStatus
{
    Completed,
    BudgetExceeded,
    CompileError,
    RuntimeError,

    /// <summary>Recursion passed the depth limit — usually a missing base case.</summary>
    RecursionLimitExceeded,

    /// <summary>An input could not be read, so nothing ran. Each input carries its own error.</summary>
    InvalidInput,

    /// <summary>
    /// The run did not finish within the wall-clock limit. The statement budget cannot
    /// catch this case: a single statement — a sleep, or one enormous library call —
    /// can run for as long as it likes without ever reaching the next tracer call.
    /// </summary>
    TimedOut
}

/// <summary>Where an instrumented statement sits in the original source.</summary>
public sealed record StatementInfo(int Id, int Line, int Column, int Length, string Snippet)
{
    /// <summary>The method or local function the statement belongs to.</summary>
    public string Method { get; init; } = "";

    /// <summary>The variables a step-through build captures before this statement.</summary>
    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();
}

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

    /// <summary>The most a single execution of this statement allocated.</summary>
    public long LargestAllocation { get; init; }
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

    /// <summary>
    /// Garbage collections during the run. Non-zero means the byte counts may include a
    /// phantom of up to about 8 KB from a collection another thread triggered.
    /// </summary>
    public int CollectionsDuringRun { get; init; }

    public IReadOnlyList<LibraryCall> LibraryCalls { get; init; } = Array.Empty<LibraryCall>();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool StepsTruncated { get; init; }

    /// <summary>The step recorder's own failure, when it had one. Internal, never serialised.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecorderFailure { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string Output { get; init; } = "";
    public string? ReturnValue { get; init; }

    /// <summary>The returned object itself, for callers that format it their own way. Never serialised.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? ReturnObject { get; init; }

    /// <summary>The line of the student's code that threw, when it threw.</summary>
    public int? ErrorLine { get; init; }

    /// <summary>
    /// What the byte counts mean, for the UI to show verbatim rather than inventing a
    /// caption. The numbers are exact — verified against known object sizes — so the
    /// honest caveats are about scope, not precision.
    /// </summary>
    /// <summary>
    /// The most memory held at once above where the run started, when the language can
    /// measure it directly — Python's tracemalloc can. Null for C#, where the peak is
    /// inferred from allocation, the largest single allocation and call depth.
    /// </summary>
    public long? PeakBytes { get; init; }

    /// <summary>The language the solution is in: "csharp" or "python".</summary>
    public string Language { get; init; } = "csharp";

    public string MemoryModel => Language == "cpp"
        ? "Exact: every byte allocated with new — every container's storage — counted per statement by replacing " +
          "the program's operator new and delete, and the peak held at once measured directly. Variables on the stack, " +
          "and the visualizer's own bookkeeping, are not counted."
        : Language == "python"
        ? "Approximate, from Python's tracemalloc: for each statement, how far traced memory rose above where it " +
          "stood when the statement began — a temporary freed before the statement ends still counts. Python " +
          "allocates even for some integers (any above 256), so a statement can show a few dozen bytes where C# " +
          "would show none. The visualizer's own bookkeeping is left out."
        : "Bytes allocated on the GC heap by the executing thread, measured per statement. Exact, with one " +
        "exception: a garbage collection set off by another thread can add up to about 8 KB to whichever " +
        "statement is running, so a small run that sees a collection is repeated and its cleanest result kept. " +
        "This counts allocation, not retention: a temporary that is immediately garbage still appears. " +
        "Stack allocations and any work on other threads are not counted.";
}
