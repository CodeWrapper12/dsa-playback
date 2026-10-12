using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Criterion 8, extended to every rewrite added for Phase A: library charging, depth
/// wrapping, arrow-function normalisation. Each fixture is run uninstrumented and
/// instrumented on identical generated input, and must return the same thing. If a
/// rewrite changed behaviour, every number reported about that code would describe a
/// program the user never wrote.
/// </summary>
public class BehaviourPreservedTests
{
    public static IEnumerable<object?[]> Fixtures() => new[]
    {
        new object?[] { "leetcode/TwoSum.cs", "TwoSum", GenerationStrategy.Distinct },
        new object?[] { "leetcode/TwoSumBrute.cs", "TwoSum", GenerationStrategy.Random },
        new object?[] { "leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Random },
        new object?[] { "leetcode/ReverseList.cs", "ReverseList", GenerationStrategy.Random },
        new object?[] { "leetcode/NumIslands.cs", "NumIslands", GenerationStrategy.Random },
        new object?[] { "leetcode/CountComponents.cs", "CountComponents", GenerationStrategy.Random },
        new object?[] { "leetcode/ClimbStairsMemo.cs", "ClimbStairs", null },
        new object?[] { "leetcode/LongestSubstring.cs", "LengthOfLongestSubstring", GenerationStrategy.Random },
        new object?[] { "leetcode/KthLargest.cs", "FindKthLargest", GenerationStrategy.Random },
        new object?[] { "leetcode/DedupeWithList.cs", "CountDistinct", GenerationStrategy.Random },
        new object?[] { "leetcode/DedupeWithSet.cs", "CountDistinct", GenerationStrategy.Random },
        new object?[] { "leetcode/JoinWithPlus.cs", "Interleave", GenerationStrategy.Random },
        new object?[] { "leetcode/JoinWithBuilder.cs", "Interleave", GenerationStrategy.Random },
        new object?[] { "leetcode/RemoveFront.cs", "Drain", GenerationStrategy.Random },
        new object?[] { "leetcode/RemoveBack.cs", "Drain", GenerationStrategy.Random },
        new object?[] { "leetcode/WindowSubstrings.cs", "DistinctTriples", GenerationStrategy.Random },
        new object?[] { "leetcode/SortOnce.cs", "Median", GenerationStrategy.Random },
        new object?[] { "leetcode/AllocateInLoop.cs", "Scratch", GenerationStrategy.Random },
        new object?[] { "leetcode/ArrowLocalRecursion.cs", "CountDown", null },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Instrumented_and_uninstrumented_runs_return_the_same_thing(string sample, string entry, GenerationStrategy? strategy)
    {
        string source = Samples.Read(sample);

        var plain = Runner.Compile(source, entry, instrument: false);
        var traced = Runner.Compile(source, entry, instrument: true);
        Assert.True(plain.Success, string.Join("; ", plain.Errors));
        Assert.True(traced.Success, string.Join("; ", traced.Errors));

        using var baseline = plain.Solution!;
        using var instrumented = traced.Solution!;
        var specs = Measurer.ApplyStrategy(ParameterBinder.Infer(baseline.Parameters), strategy);

        foreach (int size in new[] { 0, 1, 7, 64 })
        {
            // Inputs are regenerated for each run: several fixtures mutate them in place.
            var expected = Runner.Invoke(baseline, ParameterBinder.Bind(specs, baseline.Parameters, size));
            var actual = Runner.Invoke(instrumented, ParameterBinder.Bind(specs, instrumented.Parameters, size));

            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.ReturnValue, actual.ReturnValue);
            Assert.Equal(expected.Output, actual.Output);
        }
    }
}
