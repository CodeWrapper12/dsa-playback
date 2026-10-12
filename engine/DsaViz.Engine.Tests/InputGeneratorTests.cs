using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using Xunit;

namespace DsaViz.Engine.Tests;

public class InputGeneratorTests
{
    private static int[] Ints(GenerationStrategy strategy, int size) =>
        (int[])InputGenerator.Generate(
            new InputSpec { Shape = InputShape.IntArray, Strategy = strategy }, size);

    [Theory]
    [InlineData(GenerationStrategy.Random)]
    [InlineData(GenerationStrategy.Distinct)]
    [InlineData(GenerationStrategy.Sorted)]
    [InlineData(GenerationStrategy.ReverseSorted)]
    [InlineData(GenerationStrategy.AllDuplicates)]
    [InlineData(GenerationStrategy.NearlySorted)]
    public void Every_strategy_produces_the_requested_size(GenerationStrategy strategy)
        => Assert.Equal(64, Ints(strategy, 64).Length);

    [Fact]
    public void Generation_is_reproducible_for_a_given_size()
    {
        // A growth curve compares runs against each other. If the data changed between
        // runs the curve would be measuring the generator as much as the algorithm.
        Assert.Equal(Ints(GenerationStrategy.Random, 50), Ints(GenerationStrategy.Random, 50));
    }

    [Fact]
    public void Sorted_is_ascending_and_reverse_sorted_is_descending()
    {
        var ascending = Ints(GenerationStrategy.Sorted, 40);
        var descending = Ints(GenerationStrategy.ReverseSorted, 40);

        Assert.Equal(ascending.OrderBy(v => v), ascending);
        Assert.Equal(descending.OrderByDescending(v => v), descending);
    }

    [Fact]
    public void Distinct_contains_no_repeats_but_is_not_in_order()
    {
        // Both halves matter: uniqueness is what exposes a worst case, and being
        // unordered is what stops it from accidentally being the sorted case too.
        var values = Ints(GenerationStrategy.Distinct, 500);

        Assert.Equal(500, values.Distinct().Count());
        Assert.NotEqual(values.OrderBy(v => v), values);
    }

    [Fact]
    public void All_duplicates_produces_a_single_repeated_value()
        => Assert.Single(Ints(GenerationStrategy.AllDuplicates, 100).Distinct());

    [Fact]
    public void Nearly_sorted_is_mostly_ordered_but_not_completely()
    {
        var values = Ints(GenerationStrategy.NearlySorted, 200);
        int inversions = Enumerable.Range(0, values.Length - 1).Count(i => values[i] > values[i + 1]);

        Assert.True(inversions > 0, "nearly-sorted should not be perfectly sorted");
        Assert.True(inversions < values.Length / 4, $"should stay mostly ordered; saw {inversions}");
    }

    [Fact]
    public void A_scalar_can_either_track_the_size_or_stay_fixed()
    {
        var scaling = new InputSpec { Shape = InputShape.IntScalar, ScalesWithSize = true };
        var fixedValue = new InputSpec { Shape = InputShape.IntScalar, ScalesWithSize = false, FixedValue = 7 };

        Assert.Equal(64, InputGenerator.Generate(scaling, 64));
        Assert.Equal(7, InputGenerator.Generate(fixedValue, 64));
    }

    [Fact]
    public void Matrix_and_string_shapes_scale_with_size()
    {
        var matrix = (int[][])InputGenerator.Generate(new InputSpec { Shape = InputShape.Matrix }, 12);
        var text = (string)InputGenerator.Generate(new InputSpec { Shape = InputShape.StringValue }, 30);

        Assert.Equal(12, matrix.Length);
        Assert.All(matrix, row => Assert.Equal(12, row.Length));
        Assert.Equal(30, text.Length);
    }
}

public class ReproducibleInputTests
{
    [Fact]
    public void Generated_data_is_the_same_in_every_process()
    {
        // Pinned values, not a comparison of two runs: within one process a per-process
        // seed (HashCode.Combine) agrees with itself, and only differs in the next process
        // — which is exactly how it went unnoticed.
        var spec = new DsaViz.Engine.Contracts.InputSpec { Shape = DsaViz.Engine.Contracts.InputShape.IntArray };
        var values = (int[])DsaViz.Engine.Inputs.InputGenerator.Generate(spec, 6)!;
        Assert.Equal(new[] { 981, 197, 74, 622, 512, 530 }, values);
    }
}
