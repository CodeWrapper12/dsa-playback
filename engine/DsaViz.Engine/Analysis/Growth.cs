using System.Globalization;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Analysis;

/// <summary>Shared vocabulary for growth classes: ordering, and formulas readable at a glance.</summary>
public static class Growth
{
    public static int Rank(ComplexityClass value) => value switch
    {
        ComplexityClass.Constant => 0,
        ComplexityClass.Logarithmic => 1,
        ComplexityClass.Linear => 2,
        ComplexityClass.Linearithmic => 3,
        ComplexityClass.Quadratic => 4,
        ComplexityClass.Cubic => 5,
        ComplexityClass.Exponential => 6,
        _ => -1
    };

    public static ComplexityClass Max(ComplexityClass a, ComplexityClass b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>Fits a plain series of counts, one per measured size.</summary>
    public static CurveFit Fit(IReadOnlyList<int> sizes, IReadOnlyList<long> values, bool allowIntercept = false)
        => CurveFitter.Fit(sizes.Zip(values, (n, v) => new SizePoint(n, v, 0, 0)).ToList(), allowIntercept);

    /// <summary>
    /// "≈ 0.5·n²", "≈ 1.63ⁿ" — the fitted curve with its constant, which is what turns a
    /// label into a derivation: bubble sort's comparison line is not just O(n²), it is
    /// ≈ 0.5·n², and that half is the n(n−1)/2 of the textbook analysis.
    /// </summary>
    public static string? Formula(CurveFit fit, string unit = "")
    {
        if (fit.Best == ComplexityClass.Unknown || fit.Ranked.Count == 0) return null;
        double c = fit.Ranked.First(f => f.Class == fit.Best).Coefficient;

        string body = fit.Best switch
        {
            ComplexityClass.Constant => Number(c),
            ComplexityClass.Logarithmic => $"{Scale(c)}log n",
            ComplexityClass.Linear => $"{Scale(c)}n",
            ComplexityClass.Linearithmic => $"{Scale(c)}n log n",
            ComplexityClass.Quadratic => $"{Scale(c)}n²",
            ComplexityClass.Cubic => $"{Scale(c)}n³",
            // The exponential fit reports its growth base rather than a multiplier.
            ComplexityClass.Exponential => $"{c.ToString("0.00", CultureInfo.InvariantCulture)}ⁿ",
            _ => ""
        };
        return "≈ " + body + unit;
    }

    private static string Scale(double c) => Math.Abs(c - 1) < 0.005 ? "" : Number(c) + "·";

    private static string Number(double value)
    {
        double magnitude = Math.Abs(value);
        string format = magnitude >= 100 ? "0" : magnitude >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
