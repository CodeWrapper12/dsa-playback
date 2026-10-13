using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Python;

// What dsaviz_py.py answers with, read straight into records. Kept apart from the
// engine's own contracts: these are the runner's wire format, not what the editor sees.

public sealed record PythonParameter(string Name, string Type, bool Supported);

public sealed record PythonEntry(string DeclaringType, string Name, IReadOnlyList<PythonParameter> Parameters, string? Reason, int Line);

public sealed record PythonCommentLine(string Text, int Line);

/// <summary>Where a design problem's call began: its number and the step count at that moment.</summary>
public sealed record PythonCallStart(int Index, int Step);

/// <summary>A class in the file that could be a design problem's: whether it can be created without arguments, and its public methods.</summary>
public sealed record PythonClass(string Name, int Line, bool Constructible, IReadOnlyList<string> Methods)
{
    /// <summary>Each method's parameters with their hints, "__init__" for the constructor.</summary>
    public Dictionary<string, List<PythonParameter>> Signatures { get; init; } = new();
}

public sealed record PythonCall(string Function, int Line, Dictionary<string, string?> Values, IReadOnlyList<ExampleNote> Notes, bool Complete);

public sealed record PythonDescription
{
    public string? CompileError { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<PythonEntry> Entries { get; init; } = Array.Empty<PythonEntry>();
    public IReadOnlyList<PythonCommentLine> Comments { get; init; } = Array.Empty<PythonCommentLine>();
    public IReadOnlyList<PythonClass> Classes { get; init; } = Array.Empty<PythonClass>();
    public Dictionary<string, List<PythonCall>> Calls { get; init; } = new();
    public StaticEstimate? Time { get; init; }
    public StaticEstimate? Space { get; init; }
    public IReadOnlyList<MethodParameters> Methods { get; init; } = Array.Empty<MethodParameters>();
    public IReadOnlyList<string> StackLike { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HeapLike { get; init; } = Array.Empty<string>();
}

public sealed record PythonRun
{
    public RunStatus Status { get; init; }
    public IReadOnlyList<StatementCost> Statements { get; init; } = Array.Empty<StatementCost>();
    public long TotalOperations { get; init; }
    public long TotalStatements { get; init; }
    public long TotalBytesAllocated { get; init; }
    public int MaxRecursionDepth { get; init; }
    public long PeakBytes { get; init; }
    public double ElapsedMs { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public int? ErrorLine { get; init; }
    public string? ReturnValue { get; init; }
    public IReadOnlyList<LibraryCall> LibraryCalls { get; init; } = Array.Empty<LibraryCall>();
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public string? Message { get; init; }

    /// <summary>A design problem's run: what each call cost, the constructor first.</summary>
    public IReadOnlyList<long>? CallCosts { get; init; }

    /// <summary>The method each statement is in, by statement number.</summary>
    public IReadOnlyList<string> StatementMethods { get; init; } = Array.Empty<string>();
}

public sealed record PythonSteps
{
    public RunStatus Status { get; init; }
    public List<ExecutionStep> Steps { get; init; } = new();
    public List<ReturnEvent> Returns { get; init; } = new();
    public List<StatementInfo> Statements { get; init; } = new();
    public List<PythonCallStart> Calls { get; init; } = new();
    public bool Truncated { get; init; }
    public long TotalAllocated { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public int? ErrorLine { get; init; }
    public string? ReturnValue { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
}
