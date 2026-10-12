namespace DsaViz.Engine.Contracts;

public enum ComplexityClass
{
    Unknown,
    Constant,
    Logarithmic,
    Linear,
    Linearithmic,
    Quadratic,
    Cubic,
    Exponential
}

public static class ComplexityNames
{
    public static string Format(ComplexityClass value) => value switch
    {
        ComplexityClass.Constant => "O(1)",
        ComplexityClass.Logarithmic => "O(log n)",
        ComplexityClass.Linear => "O(n)",
        ComplexityClass.Linearithmic => "O(n log n)",
        ComplexityClass.Quadratic => "O(n²)",
        ComplexityClass.Cubic => "O(n³)",
        ComplexityClass.Exponential => "O(2ⁿ)",
        _ => "unknown"
    };
}

/// <summary>One measured point on the growth curve.</summary>
public sealed record SizePoint(int Size, long Operations, long BytesAllocated, double ElapsedMs)
{
    public long Statements { get; init; }
    public long LibraryWork { get; init; }
    public int MaxRecursionDepth { get; init; }
}

public sealed record ClassFit(ComplexityClass Class, double RSquared, double Coefficient);

/// <summary>
/// The empirical verdict. <paramref name="Ranked"/> is kept because the runner-up
/// matters: over a short range of sizes O(n) and O(n log n) are genuinely hard to
/// separate, and hiding that would present a guess as a finding.
/// </summary>
public sealed record CurveFit(
    ComplexityClass Best,
    double RSquared,
    IReadOnlyList<ClassFit> Ranked,
    string Explanation);

public sealed record StaticEstimate(
    ComplexityClass Class,
    string Reasoning,
    int LinearLoopDepth,
    int LogLoopDepth,
    int RecursiveSelfCalls);
