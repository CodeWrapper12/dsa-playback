namespace DsaViz.Engine.Contracts;

public sealed record MeasurementResult
{
    public required RunStatus Status { get; init; }

    /// <summary>The sizes that actually ran, with what each cost.</summary>
    public required IReadOnlyList<SizePoint> Points { get; init; }

    public required CurveFit Empirical { get; init; }
    public required StaticEstimate Static { get; init; }

    /// <summary>
    /// True when both readings named a class and the classes differ. Surfaced rather
    /// than reconciled: the measurement is the evidence, but a mismatch usually means
    /// something worth understanding, such as an early exit that stops the worst case
    /// from ever happening on this input.
    /// </summary>
    public required bool Disagreement { get; init; }
    public string? DisagreementNote { get; init; }

    /// <summary>Per-statement detail from the largest size that completed.</summary>
    public TraceResult? Detail { get; init; }

    /// <summary>Why it is the class it is: which statements dominate, and by what formula.</summary>
    public Derivation? Derivation { get; init; }

    /// <summary>Space: allocation, largest single allocation, call depth, and the reading from the code.</summary>
    public SpaceReading? Space { get; init; }

    /// <summary>The input configuration actually used, inferred or supplied.</summary>
    public required IReadOnlyList<InputSpec> Inputs { get; init; }

    /// <summary>
    /// Each parameter as the engine understands it, with the arrangements that apply to it,
    /// so the editor offers only choices that make sense — Skewed for a tree, never for an array.
    /// </summary>
    public IReadOnlyList<DsaViz.Engine.Inputs.ParameterDescription> Parameters { get; init; }
        = Array.Empty<DsaViz.Engine.Inputs.ParameterDescription>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Set when measurement stopped early because a size exceeded the budget.</summary>
    public string? SizeNote { get; init; }

    /// <summary>For a design problem: how each operation's cost per call grows. Null otherwise.</summary>
    public DesignGrowth? Design { get; init; }
}

/// <summary>
/// A design problem measured the way LeetCode states its requirement — "get and put must
/// each run in O(1) average time": the cost of one call to each method, averaged over all
/// its calls, as the number of calls made grows.
/// </summary>
public sealed record DesignGrowth
{
    /// <summary>What calls were made at each size, in words.</summary>
    public required string Workload { get; init; }

    /// <summary>One per method the calls used, in the order the example first calls them.</summary>
    public required IReadOnlyList<OperationGrowth> Operations { get; init; }
}

public sealed record OperationGrowth
{
    /// <summary>The operation as the example names it: "put".</summary>
    public required string Name { get; init; }

    /// <summary>How the average cost of one call grows with n.</summary>
    public required ComplexityClass PerCall { get; init; }

    /// <summary>"≈ 6 operations per call", "≈ 0.5·n per call".</summary>
    public string? Formula { get; init; }

    /// <summary>The finding in plain words.</summary>
    public required string Plain { get; init; }

    /// <summary>The line that makes a call cost what it does, when one does.</summary>
    public string? Because { get; init; }
    public int? Line { get; init; }

    /// <summary>At each size: how many calls were made, and their average and largest cost.</summary>
    public required IReadOnlyList<OperationPoint> Points { get; init; }
}

public sealed record OperationPoint(int Size, int Calls, double Average, long Largest);
