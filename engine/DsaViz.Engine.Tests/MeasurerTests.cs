using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// End-to-end: compile, generate inputs at several sizes, run, fit a curve, read the
/// code statically, and compare the two. Acceptance criteria 2b, 3 and 4.
/// </summary>
public class MeasurerTests
{
    private static MeasurementResult Measure(
        string sample, string entry, GenerationStrategy? strategy = null)
    {
        IReadOnlyList<InputSpec>? inputs = null;

        if (strategy is { } chosen)
        {
            var outcome = Runner.Compile(Samples.Read(sample), entry, instrument: false);
            Assert.True(outcome.Success, string.Join("; ", outcome.Errors));
            using var probe = outcome.Solution!;
            inputs = DsaViz.Engine.Inputs.ParameterBinder.Infer(probe.Parameters)
                .Select(spec => spec.Shape is InputShape.IntScalar or InputShape.BoolScalar
                    ? spec
                    : spec with { Strategy = chosen })
                .ToList();
        }

        return Measurer.Measure(Samples.Read(sample), entry, inputs);
    }

    [Theory]
    [InlineData("ConstantSample.cs", "First", null, ComplexityClass.Constant)]
    [InlineData("LinearSumSample.cs", "Sum", null, ComplexityClass.Linear)]
    [InlineData("SortParamSample.cs", "Sort", null, ComplexityClass.Quadratic)]
    [InlineData("BinarySearchParamSample.cs", "Search", GenerationStrategy.Sorted, ComplexityClass.Logarithmic)]
    [InlineData("FibonacciParamSample.cs", "Fib", null, ComplexityClass.Exponential)]
    [InlineData("EarlyExitSample.cs", "FindRepeatedValue", GenerationStrategy.Distinct, ComplexityClass.Quadratic)]
    public void Measured_growth_matches_the_known_complexity(
        string sample, string entry, GenerationStrategy? strategy, ComplexityClass expected)
    {
        var result = Measure(sample, entry, strategy);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(expected, result.Empirical.Best);
        Assert.True(result.Empirical.RSquared >= CurveFitterThreshold,
            $"fit quality {result.Empirical.RSquared:F4} was below the reporting threshold");
    }

    private const double CurveFitterThreshold = 0.99;

    [Fact]
    public void A_growth_curve_has_a_point_for_every_size_that_ran()   // criterion 3
    {
        var result = Measure("LinearSumSample.cs", "Sum");

        Assert.Contains(result.Points, p => p.Size == 10);
        Assert.Contains(result.Points, p => p.Size == 100);
        Assert.Contains(result.Points, p => p.Size == 1000);
        Assert.Equal(result.Points.OrderBy(p => p.Size), result.Points);
    }

    [Fact]
    public void Both_readings_are_reported_side_by_side()              // criterion 4
    {
        var result = Measure("SortParamSample.cs", "Sort");

        Assert.Equal(ComplexityClass.Quadratic, result.Empirical.Best);
        Assert.Equal(ComplexityClass.Quadratic, result.Static.Class);
        Assert.False(result.Disagreement);
    }

    [Fact]
    public void A_disagreement_is_flagged_and_explained()              // criterion 4
    {
        // Reads as O(n^2), but every comparison finds a duplicate immediately, so it
        // never gets past the first pair. The gap is the interesting part, and burying
        // it under whichever answer we trusted more would hide the actual lesson.
        var result = Measure("EarlyExitSample.cs", "FindRepeatedValue", GenerationStrategy.AllDuplicates);

        Assert.Equal(ComplexityClass.Constant, result.Empirical.Best);
        Assert.Equal(ComplexityClass.Quadratic, result.Static.Class);
        Assert.True(result.Disagreement);
        Assert.Contains("early exit", result.DisagreementNote!);
    }

    [Fact]
    public void The_input_strategy_changes_the_measured_curve()        // criterion 2b
    {
        // Same code, same sizes, different data arrangement: O(1) against O(n^2).
        var duplicates = Measure("EarlyExitSample.cs", "FindRepeatedValue", GenerationStrategy.AllDuplicates);
        var distinct = Measure("EarlyExitSample.cs", "FindRepeatedValue", GenerationStrategy.Distinct);

        Assert.Equal(ComplexityClass.Constant, duplicates.Empirical.Best);
        Assert.Equal(ComplexityClass.Quadratic, distinct.Empirical.Best);
    }

    [Fact]
    public void An_expensive_solution_is_retried_at_sizes_it_can_survive()
    {
        // The default ladder reaches n=1000, which an exponential algorithm cannot
        // survive. Reporting Unknown would be a measurement failure dressed up as a
        // finding, so the range adapts to the algorithm instead.
        var result = Measure("FibonacciParamSample.cs", "Fib");

        Assert.Equal(ComplexityClass.Exponential, result.Empirical.Best);
        Assert.NotNull(result.SizeNote);
        Assert.All(result.Points, point => Assert.True(point.Size <= 20));
    }

    [Fact]
    public void Per_statement_detail_is_kept_from_the_largest_run()
    {
        var result = Measure("SortParamSample.cs", "Sort");

        Assert.NotNull(result.Detail);
        Assert.NotEmpty(result.Detail!.Statements);
        Assert.Contains(result.Detail.Statements, s => s.Snippet.StartsWith("for ("));
    }

    [Fact]
    public void The_configuration_actually_used_is_reported_back()
    {
        // The user needs to see what was measured, especially when nothing was
        // configured and the engine inferred it.
        var result = Measure("BinarySearchParamSample.cs", "Search", GenerationStrategy.Sorted);

        Assert.Equal(2, result.Inputs.Count);
        Assert.Equal(GenerationStrategy.Sorted, result.Inputs[0].Strategy);
        Assert.False(result.Inputs[1].ScalesWithSize);
    }

    [Fact]
    public void An_unmeasurable_signature_explains_itself()
    {
        var result = Measurer.Measure(
            """
            public static class Custom
            {
                public class Node { public int Value; }
                public static int Walk(Node node) => 1;
            }
            """, "Walk");

        Assert.NotEqual(RunStatus.Completed, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("int[]"));
    }
}
