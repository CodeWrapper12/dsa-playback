using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

public class CurveFitterTests
{
    private static IReadOnlyList<SizePoint> Points(Func<int, long> work, params int[] sizes)
        => sizes.Select(n => new SizePoint(n, work(n), 0, 0)).ToList();

    private static readonly int[] Ladder = { 10, 25, 50, 100, 250, 500, 1000 };

    [Fact]
    public void Constant_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Constant, CurveFitter.Fit(Points(_ => 42, Ladder)).Best);

    [Fact]
    public void Linear_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Linear, CurveFitter.Fit(Points(n => 3L * n, Ladder)).Best);

    [Fact]
    public void Logarithmic_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Logarithmic,
            CurveFitter.Fit(Points(n => (long)(5 * Math.Log2(n)), Ladder)).Best);

    [Fact]
    public void Linearithmic_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Linearithmic,
            CurveFitter.Fit(Points(n => (long)(n * Math.Log2(n)), Ladder)).Best);

    [Fact]
    public void Quadratic_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Quadratic, CurveFitter.Fit(Points(n => (long)n * n / 2, Ladder)).Best);

    [Fact]
    public void Cubic_growth_is_recognised()
        => Assert.Equal(ComplexityClass.Cubic, CurveFitter.Fit(Points(n => (long)n * n * n, Ladder)).Best);

    [Fact]
    public void Exponential_growth_is_recognised_at_any_base()
    {
        // Fitting a fixed 2^n basis would miss this: naive Fibonacci grows as phi^n,
        // roughly 1.618^n, which scores poorly against 2^n despite being exponential.
        var fit = CurveFitter.Fit(Points(n => (long)Math.Pow(1.618, n), 4, 6, 8, 10, 12, 14, 16, 18));

        Assert.Equal(ComplexityClass.Exponential, fit.Best);
        Assert.Contains("1.6", fit.Explanation);
    }

    [Fact]
    public void Growth_that_matches_nothing_is_reported_as_unknown()
    {
        // Deliberately erratic. Naming the least-bad class here would present a coin
        // flip as a finding, which is worse than admitting the shape is not standard.
        var erratic = new long[] { 5, 900, 12, 4000, 7, 30, 2 };
        var points = Ladder.Zip(erratic, (n, ops) => new SizePoint(n, ops, 0, 0)).ToList();

        var fit = CurveFitter.Fit(points);
        Assert.Equal(ComplexityClass.Unknown, fit.Best);
        Assert.Contains("does not match a standard shape", fit.Explanation);
    }

    [Fact]
    public void Too_few_points_is_reported_rather_than_guessed()
    {
        var fit = CurveFitter.Fit(Points(n => 3L * n, 10, 20));

        Assert.Equal(ComplexityClass.Unknown, fit.Best);
        Assert.Contains("at least 3", fit.Explanation);
    }

    [Fact]
    public void A_near_tie_between_classes_is_disclosed()
    {
        // Over a narrow range n and n log n are genuinely hard to separate. The report
        // should say so rather than pick one and sound certain.
        var fit = CurveFitter.Fit(Points(n => (long)(n * Math.Log2(n)), 100, 110, 120, 130));

        if (fit.Ranked.Count > 1 && fit.Ranked[0].RSquared - fit.Ranked[1].RSquared < 0.01)
            Assert.Contains("almost as well", fit.Explanation);
    }

    [Fact]
    public void The_ranking_is_kept_so_the_runner_up_is_visible()
    {
        var fit = CurveFitter.Fit(Points(n => (long)n * n, Ladder));

        Assert.True(fit.Ranked.Count > 1);
        Assert.Equal(fit.Best, fit.Ranked[0].Class);
        Assert.True(fit.Ranked[0].RSquared >= fit.Ranked[1].RSquared);
    }
}
