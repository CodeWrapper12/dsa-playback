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

    /// <summary>The input configuration actually used, inferred or supplied.</summary>
    public required IReadOnlyList<InputSpec> Inputs { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Set when measurement stopped early because a size exceeded the budget.</summary>
    public string? SizeNote { get; init; }
}
