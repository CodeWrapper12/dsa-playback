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

    /// <summary>
    /// Record a step-through at this size instead of measuring. Asked for separately, when
    /// the user opens the step view, so a measurement never pays for recording steps.
    /// </summary>
    public int? StepSize { get; init; }
    public int MaxSteps { get; init; } = DsaViz.Engine.Measurement.Stepper.DefaultMaxSteps;

    /// <summary>
    /// For a step-through: values typed by the student, one LeetCode-format text per
    /// parameter, null where the parameter keeps its default source.
    /// </summary>
    public string?[]? Values { get; init; }

    /// <summary>
    /// For a step-through: which example found in the code to use. Omitted means the
    /// first one found; a negative number means none, so every value is generated.
    /// </summary>
    public int? Example { get; init; }

    /// <summary>
    /// Run every example found in the code and report each result against its expected
    /// output, instead of recording one run to step through.
    /// </summary>
    public bool AllExamples { get; init; }

    /// <summary>The Python interpreter to run Python files with: the one selected in VS Code, when there is one.</summary>
    public string? Python { get; init; }

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

    /// <summary>
    /// Every parameter can be generated, so the method's growth can be measured. A method
    /// that runs only on an example's values can be visualized but not measured.
    /// </summary>
    public bool Generatable { get; init; } = true;

    /// <summary>The 1-based line of the method's name, where the editor puts its buttons.</summary>
    public int Line { get; init; }

    /// <summary>"Solution.TwoSum" — unambiguous even when two classes share a method name. A top-level Python function is just its name.</summary>
    public string Qualified => DeclaringType.Length == 0 ? Name : $"{DeclaringType}.{Name}";

    public string Signature =>
        $"{Qualified}({string.Join(", ", Parameters.Select(p => $"{p.Type} {p.Name}"))})";
}

public sealed record JobResponse
{
    public MeasurementResult? Primary { get; init; }
    public MeasurementResult? Comparison { get; init; }

    /// <summary>Set when the request asked for a step-through rather than a measurement.</summary>
    public StepTrace? Steps { get; init; }

    /// <summary>Set when the request asked for every example to be run.</summary>
    public ExampleReport? Examples { get; init; }

    /// <summary>Source text, so the view can lay costs over the code without re-reading it.</summary>
    public string? Source { get; init; }
    public string? ComparisonSource { get; init; }

    public string? Label { get; init; }
    public string? ComparisonLabel { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set when nothing could run for a reason the editor can help with: <c>python-missing</c>
    /// or <c>python-version</c>, for which it offers to install Python.
    /// </summary>
    public string? ErrorCode { get; init; }
}
