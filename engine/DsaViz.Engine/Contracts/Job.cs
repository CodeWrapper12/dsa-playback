namespace DsaViz.Engine.Contracts;

/// <summary>
/// A measurement request from the editor. Sent as JSON so the extension can express a
/// full input configuration, which the flag-based CLI cannot carry.
/// </summary>
public sealed record JobRequest
{
    public required string File { get; init; }
    public string Entry { get; init; } = "Solve";

    public int[]? Sizes { get; init; }
    public long Budget { get; init; } = Runner.DefaultBudget;
    public InputSpec[]? Inputs { get; init; }

    /// <summary>
    /// An arrangement to apply to every collection input it suits, when no full input
    /// configuration is given. The engine decides which inputs it applies to, so the
    /// editor never has to reproduce the engine's inference to change one setting.
    /// </summary>
    public GenerationStrategy? Strategy { get; init; }

    /// <summary>A second solution to measure alongside the first, for comparison.</summary>
    public string? CompareFile { get; init; }
    public string? CompareEntry { get; init; }
}

/// <summary>One candidate entry point, as read from the source without compiling.</summary>
public sealed record EntryParameter(string Name, string Type, bool Supported);

public sealed record EntryPoint(
    string DeclaringType,
    string Name,
    IReadOnlyList<EntryParameter> Parameters)
{
    /// <summary>Why it cannot be measured, in a sentence the user can act on. Null when it can.</summary>
    public string? Reason { get; init; }

    public bool Measurable => Reason is null;

    /// <summary>"Solution.TwoSum" — unambiguous even when two classes share a method name.</summary>
    public string Qualified => $"{DeclaringType}.{Name}";

    public string Signature =>
        $"{DeclaringType}.{Name}({string.Join(", ", Parameters.Select(p => $"{p.Type} {p.Name}"))})";
}

public sealed record JobResponse
{
    public MeasurementResult? Primary { get; init; }
    public MeasurementResult? Comparison { get; init; }

    /// <summary>Source text, so the view can lay costs over the code without re-reading it.</summary>
    public string? Source { get; init; }
    public string? ComparisonSource { get; init; }

    public string? Label { get; init; }
    public string? ComparisonLabel { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
