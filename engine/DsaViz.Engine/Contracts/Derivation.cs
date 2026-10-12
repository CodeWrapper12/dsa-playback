namespace DsaViz.Engine.Contracts;

/// <summary>
/// How one statement's cost grows with n — fitted from its own counts across every
/// measured size, so each line carries its own complexity instead of only the method
/// as a whole having one.
/// </summary>
public sealed record StatementGrowth
{
    public required int Id { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required string Snippet { get; init; }

    /// <summary>How the number of times it runs grows.</summary>
    public required ComplexityClass Runs { get; init; }
    public string? RunsFormula { get; init; }

    /// <summary>How its total cost — runs plus library work inside it — grows.</summary>
    public required ComplexityClass Cost { get; init; }
    public string? CostFormula { get; init; }

    /// <summary>How the bytes it allocates in total grow, and the most it allocates in one go.</summary>
    public required ComplexityClass Memory { get; init; }
    public string? MemoryFormula { get; init; }
    public required ComplexityClass LargestAllocation { get; init; }

    /// <summary>The library call on this statement that explains a gap between Runs and Cost, if any.</summary>
    public string? Because { get; init; }

    /// <summary>Cost at each measured size, in the same order as the measurement's points.</summary>
    public required IReadOnlyList<long> Costs { get; init; }
}

/// <summary>
/// Why the method has the complexity it has: which statements dominate, and the formula
/// each follows. This is the derivation a Big-O label on its own does not give.
/// </summary>
public sealed record Derivation(string Summary, IReadOnlyList<int> Dominant, IReadOnlyList<StatementGrowth> Statements);

/// <summary>
/// Space, read three ways from the measurement and once from the code.
///
/// Total allocation and peak space are different things, and conflating them is a common
/// interview mistake. A loop that allocates a fresh n-element buffer each pass allocates
/// O(n²) in total but never holds more than O(n). The largest single allocation is what
/// separates that from a structure that genuinely grows to O(n²).
/// </summary>
public sealed record SpaceReading
{
    /// <summary>The most memory held at once, where it was measured directly (Python).</summary>
    public CurveFit? Held { get; init; }

    public required CurveFit Allocation { get; init; }
    public required CurveFit LargestAllocation { get; init; }
    public required CurveFit CallDepth { get; init; }

    /// <summary>The space most likely held at once, combining the readings above.</summary>
    public required ComplexityClass LikelyPeak { get; init; }
    public required string Summary { get; init; }

    public required StaticEstimate Static { get; init; }
    public bool Disagreement { get; init; }
    public string? DisagreementNote { get; init; }
}
