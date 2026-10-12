namespace DsaViz.Engine.Contracts;

/// <summary>
/// A variable at one step. Number lets an integer be drawn as a pointer; Items lets a
/// string be drawn as cells. A variable holding an object carries its <see cref="Ref"/>,
/// and the object itself is in the step's heap.
/// </summary>
public sealed record StepVariable(string Name, string Display, string Kind, IReadOnlyList<string>? Items, long? Number)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Ref { get; init; }
}

/// <summary>One statement about to run, with every variable it can see.</summary>
public sealed record ExecutionStep(
    int Index, int StatementId, int Line, int Column, int Depth, string Method, IReadOnlyList<StepVariable> Variables)
{
    /// <summary>The objects that changed since the previous step, or appeared for the first time.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HeapObject>? Heap { get; init; }

    /// <summary>Bytes the solution had allocated before this statement ran.</summary>
    public long Allocated { get; init; }
}

/// <summary>
/// A recorded run at a small size, for stepping through: what the code does, one
/// statement at a time, rather than only what it costs.
/// </summary>
public sealed record StepTrace
{
    public required RunStatus Status { get; init; }
    public required int Size { get; init; }
    public required IReadOnlyList<ExecutionStep> Steps { get; init; }
    public required IReadOnlyList<StatementInfo> Statements { get; init; }

    /// <summary>True when the run had more steps than were kept.</summary>
    public bool Truncated { get; init; }
    public int MaxSteps { get; init; }

    /// <summary>The arguments as they were passed in, captured before the run could change them.</summary>
    public IReadOnlyList<StepVariable> Arguments { get; init; } = Array.Empty<StepVariable>();
    public string? ReturnValue { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>The line the student's code failed on, when it threw.</summary>
    public int? ErrorLine { get; init; }

    /// <summary>Each parameter's value as LeetCode-format text, and where it came from.</summary>
    public IReadOnlyList<StepInput> Inputs { get; init; } = Array.Empty<StepInput>();

    /// <summary>Input values found in the code, offered as a choice.</summary>
    public IReadOnlyList<FoundExample> Examples { get; init; } = Array.Empty<FoundExample>();

    /// <summary>Calls seen in the code that could not be used, with the reason.</summary>
    public IReadOnlyList<string> ExampleNotes { get; init; } = Array.Empty<string>();

    /// <summary>Index into <see cref="Examples"/> of the one used; null when none was.</summary>
    public int? SelectedExample { get; init; }

    /// <summary>The result against the example's expected output, when it gives one.</summary>
    public OutputCheck? Check { get; init; }

    /// <summary>Every value returned by a method or local function, in order.</summary>
    public IReadOnlyList<ReturnEvent> Returns { get; init; } = Array.Empty<ReturnEvent>();

    /// <summary>Bytes the solution allocated over the whole recorded run.</summary>
    public long TotalAllocated { get; init; }

    public IReadOnlyList<MethodParameters> Methods { get; init; } = Array.Empty<MethodParameters>();

    /// <summary>The input to draw as a graph, when there is one.</summary>
    public GraphInput? Graph { get; init; }
}

public enum InputSource
{
    /// <summary>Found in the code: an example comment, a call, or a declared variable.</summary>
    Example,

    /// <summary>Typed by the student.</summary>
    Typed,

    /// <summary>Generated at the chosen size, because nothing else gave a value.</summary>
    Generated
}

/// <summary>
/// One parameter's input. <see cref="Error"/> is set when the text could not be read, in
/// which case nothing ran; <see cref="Note"/> explains a value the code did not supply.
/// </summary>
public sealed record StepInput(string Name, string Type, string Literal, InputSource Source)
{
    public string? Note { get; init; }
    public string? Error { get; init; }
}

public sealed record OutputCheck(string Expected, string Actual, bool Matches)
{
    /// <summary>Compared ignoring order, because the problem says any order is accepted.</summary>
    public bool AnyOrder { get; init; }
}
