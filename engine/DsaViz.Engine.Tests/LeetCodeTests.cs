using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// The audit's headline failure: code written in LeetCode's own template could not be
/// measured at all. These run real solutions to the problems in the practice rotation —
/// arrays, strings, trees, graphs, grids, heaps, DP — exactly as they would be written.
/// </summary>
public class LeetCodeTests
{
    internal static MeasurementResult Measure(string sample, string entry, GenerationStrategy? strategy = null)
        => Measurer.Measure(Samples.Read(sample), entry, inputs: null, sizes: null, Runner.DefaultBudget, strategy);

    [Theory]
    [InlineData("leetcode/TwoSum.cs", "TwoSum", GenerationStrategy.Distinct, ComplexityClass.Linear)]
    [InlineData("leetcode/TwoSumBrute.cs", "TwoSum", GenerationStrategy.Distinct, ComplexityClass.Quadratic)]
    [InlineData("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Random, ComplexityClass.Linear)]
    [InlineData("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Skewed, ComplexityClass.Linear)]
    [InlineData("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Balanced, ComplexityClass.Linear)]
    [InlineData("leetcode/ReverseList.cs", "ReverseList", GenerationStrategy.Random, ComplexityClass.Linear)]
    [InlineData("leetcode/NumIslands.cs", "NumIslands", GenerationStrategy.Random, ComplexityClass.Quadratic)]
    [InlineData("leetcode/CountComponents.cs", "CountComponents", GenerationStrategy.Random, ComplexityClass.Linear)]
    [InlineData("leetcode/ClimbStairsMemo.cs", "ClimbStairs", null, ComplexityClass.Linear)]
    [InlineData("leetcode/LongestSubstring.cs", "LengthOfLongestSubstring", GenerationStrategy.Random, ComplexityClass.Linear)]
    [InlineData("leetcode/KthLargest.cs", "FindKthLargest", GenerationStrategy.Random, ComplexityClass.Linear)]
    [InlineData("leetcode/ArrowLocalRecursion.cs", "CountDown", null, ComplexityClass.Linear)]
    public void A_leetcode_solution_measures_as_its_known_complexity(
        string sample, string entry, GenerationStrategy? strategy, ComplexityClass expected)
    {
        var result = Measure(sample, entry, strategy);

        Assert.True(result.Status == RunStatus.Completed, string.Join("; ", result.Errors.Concat(new[] { result.SizeNote ?? "" })));
        Assert.Equal(expected, result.Empirical.Best);
    }

    [Fact]
    public void A_fresh_solution_instance_is_made_for_every_run()
    {
        // LeetCode constructs a new Solution per test. Reusing one would carry a memo
        // filled at n=500 into the n=1000 run and make the larger size look cheaper.
        var result = Measure("leetcode/ClimbStairsMemo.cs", "ClimbStairs");

        var points = result.Points.ToList();
        for (int i = 1; i < points.Count; i++)
            Assert.True(points[i].Operations > points[i - 1].Operations,
                $"n={points[i].Size} cost {points[i].Operations}, not more than n={points[i - 1].Size} at {points[i - 1].Operations}");
    }

    [Fact]
    public void Tree_shape_changes_recursion_depth_but_not_time()
    {
        // The space story for trees: the same O(n) traversal recurses log n deep on a
        // balanced tree and n deep on a skewed one.
        var balanced = Measure("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Balanced);
        var skewed = Measure("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Skewed);

        var balancedLargest = balanced.Points[^1];
        var skewedLargest = skewed.Points[^1];

        Assert.Equal(skewedLargest.Size + 1, skewedLargest.MaxRecursionDepth);   // n nodes plus the null below the last
        Assert.True(balancedLargest.MaxRecursionDepth <= 2 + (int)Math.Ceiling(Math.Log2(balancedLargest.Size + 1)),
            $"a balanced tree of {balancedLargest.Size} nodes recursed {balancedLargest.MaxRecursionDepth} deep");
    }

    [Fact]
    public void A_returned_tree_is_rendered_the_way_leetcode_prints_it()
    {
        var source = Samples.Read("leetcode/MaxDepth.cs") + """

            public class Builder {
                public static TreeNode Make() => new TreeNode(3, new TreeNode(9), new TreeNode(20, new TreeNode(15), new TreeNode(7)));
            }
            """;
        var result = Runner.Run(source, "Builder.Make");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("[3,9,20,null,null,15,7]", result.ReturnValue);
    }
}
