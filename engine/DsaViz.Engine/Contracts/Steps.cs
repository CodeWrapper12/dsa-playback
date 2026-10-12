namespace DsaViz.Engine.Contracts;

/// <summary>A variable at one step. Items and Number let the editor draw arrays with index pointers.</summary>
public sealed record StepVariable(string Name, string Display, string Kind, IReadOnlyList<string>? Items, long? Number);

/// <summary>One statement about to run, with every variable it can see.</summary>
public sealed record ExecutionStep(
    int Index, int StatementId, int Line, int Column, int Depth, string Method, IReadOnlyList<StepVariable> Variables);

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
}
