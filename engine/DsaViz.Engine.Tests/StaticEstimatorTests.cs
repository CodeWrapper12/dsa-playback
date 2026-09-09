using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

public class StaticEstimatorTests
{
    [Theory]
    [InlineData("ConstantSample.cs", "First", ComplexityClass.Constant)]
    [InlineData("LinearSumSample.cs", "Sum", ComplexityClass.Linear)]
    [InlineData("SortParamSample.cs", "Sort", ComplexityClass.Quadratic)]
    [InlineData("BinarySearchParamSample.cs", "Search", ComplexityClass.Logarithmic)]
    [InlineData("FibonacciParamSample.cs", "Fib", ComplexityClass.Exponential)]
    [InlineData("EarlyExitSample.cs", "FindRepeatedValue", ComplexityClass.Quadratic)]
    public void Recognisable_shapes_are_classified(string sample, string entry, ComplexityClass expected)
        => Assert.Equal(expected, StaticEstimator.Estimate(Samples.Read(sample), entry).Class);

    [Fact]
    public void A_single_recursive_call_is_admitted_as_undecidable()
    {
        // Walking a list and halving a range are structurally identical here — O(n) and
        // O(log n) look the same. Guessing would be a coin flip presented as analysis.
        var estimate = StaticEstimator.Estimate(
            """
            public static class Walk
            {
                public static int Count(int[] values, int index)
                {
                    if (index >= values.Length) return 0;
                    return 1 + Count(values, index + 1);
                }
            }
            """, "Count");

        Assert.Equal(ComplexityClass.Unknown, estimate.Class);
        Assert.Contains("calls itself once", estimate.Reasoning);
    }

    [Fact]
    public void Work_inside_a_called_method_stacks_on_the_caller_loops()
    {
        // A loop calling a method that loops is quadratic even though neither method
        // contains nested loops on its own.
        var estimate = StaticEstimator.Estimate(
            """
            public static class Stacked
            {
                private static int Inner(int[] values)
                {
                    int total = 0;
                    for (int i = 0; i < values.Length; i++) total += values[i];
                    return total;
                }

                public static int Outer(int[] values)
                {
                    int total = 0;
                    for (int i = 0; i < values.Length; i++) total += Inner(values);
                    return total;
                }
            }
            """, "Outer");

        Assert.Equal(ComplexityClass.Quadratic, estimate.Class);
    }

    [Fact]
    public void A_missing_entry_point_is_reported_rather_than_assumed_constant()
    {
        var estimate = StaticEstimator.Estimate("public static class A { }", "Nope");

        Assert.Equal(ComplexityClass.Unknown, estimate.Class);
        Assert.Contains("No method named", estimate.Reasoning);
    }

    [Fact]
    public void A_multiplying_loop_counter_is_read_as_logarithmic()
    {
        var estimate = StaticEstimator.Estimate(
            """
            public static class Doubling
            {
                public static int Count(int n)
                {
                    int steps = 0;
                    for (int i = 1; i < n; i *= 2) steps++;
                    return steps;
                }
            }
            """, "Count");

        Assert.Equal(ComplexityClass.Logarithmic, estimate.Class);
    }
}
