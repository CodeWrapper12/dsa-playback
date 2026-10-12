using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Library calls whose cost is not visible as statements. Each pair is a trap and its
/// fix: the same loop, the same line count, a different class. Statement counting alone
/// reported every trap here as the cheaper class.
/// </summary>
public class HiddenCostTests
{
    [Theory]
    [InlineData("leetcode/DedupeWithList.cs", "leetcode/DedupeWithSet.cs", "CountDistinct", GenerationStrategy.Distinct)]
    [InlineData("leetcode/JoinWithPlus.cs", "leetcode/JoinWithBuilder.cs", "Interleave", GenerationStrategy.Random)]
    [InlineData("leetcode/RemoveFront.cs", "leetcode/RemoveBack.cs", "Drain", GenerationStrategy.Random)]
    public void The_trap_measures_quadratic_and_the_fix_linear(
        string trap, string fix, string entry, GenerationStrategy strategy)
    {
        var slow = LeetCodeTests.Measure(trap, entry, strategy);
        var fast = LeetCodeTests.Measure(fix, entry, strategy);

        Assert.Equal(ComplexityClass.Quadratic, slow.Empirical.Best);
        Assert.Equal(ComplexityClass.Linear, fast.Empirical.Best);
    }

    [Theory]
    [InlineData("leetcode/DedupeWithList.cs", "CountDistinct", ComplexityClass.Quadratic, "List<T>.Contains")]
    [InlineData("leetcode/DedupeWithSet.cs", "CountDistinct", ComplexityClass.Linear, null)]
    [InlineData("leetcode/JoinWithPlus.cs", "Interleave", ComplexityClass.Quadratic, "string +=")]
    [InlineData("leetcode/RemoveFront.cs", "Drain", ComplexityClass.Quadratic, "List<T>.RemoveAt")]
    [InlineData("leetcode/RemoveBack.cs", "Drain", ComplexityClass.Linear, null)]
    [InlineData("leetcode/AllocateInLoop.cs", "Scratch", ComplexityClass.Quadratic, "array allocation")]
    [InlineData("leetcode/SortOnce.cs", "Median", ComplexityClass.Linearithmic, "Array.Sort")]
    [InlineData("leetcode/WindowSubstrings.cs", "DistinctTriples", ComplexityClass.Linear, null)]
    public void The_static_reading_counts_library_cost_and_names_the_call(
        string sample, string entry, ComplexityClass expected, string? named)
    {
        var estimate = Analysis.StaticEstimator.Estimate(Samples.Read(sample), entry);

        Assert.Equal(expected, estimate.Class);
        if (named is not null) Assert.Contains(named, estimate.Reasoning);
    }

    [Fact]
    public void Substring_of_a_fixed_width_is_charged_its_width_not_the_whole_string()
    {
        // Charging the full length would make a sliding window over fixed-width
        // substrings look O(n²) and teach the opposite of the truth.
        var result = LeetCodeTests.Measure("leetcode/WindowSubstrings.cs", "DistinctTriples", GenerationStrategy.Random);

        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);
        var substring = Assert.Single(result.Detail!.LibraryCalls, c => c.Member == "string.Substring");
        Assert.Equal(substring.Calls * 3, substring.Work);
    }

    [Fact]
    public void Sorting_once_measures_as_n_log_n()
        => Assert.Equal(ComplexityClass.Linearithmic,
            LeetCodeTests.Measure("leetcode/SortOnce.cs", "Median", GenerationStrategy.Random).Empirical.Best);

    [Fact]
    public void Hidden_work_is_reported_per_call_site_and_per_statement()
    {
        var result = LeetCodeTests.Measure("leetcode/DedupeWithList.cs", "CountDistinct", GenerationStrategy.Distinct);
        var detail = result.Detail!;
        int n = result.Points[^1].Size;

        // Scanning a list that grows 0, 1, 2 ... n-1: n(n-1)/2 comparisons in all.
        var contains = Assert.Single(detail.LibraryCalls);
        Assert.Equal("List<T>.Contains", contains.Member);
        Assert.Equal(n, contains.Calls);
        Assert.Equal((long)n * (n - 1) / 2, contains.Work);

        // ...and that work belongs to the line that made the call.
        var line = detail.Statements.Single(s => s.Snippet.StartsWith("if (!seen.Contains"));
        Assert.Equal(contains.Work, line.LibraryWork);
        Assert.Equal(n, line.Hits);
    }

    [Fact]
    public void A_heap_capped_at_k_is_charged_log_k_not_log_n()
    {
        // FindKthLargest keeps at most k+1 items in its heap. Charging log n would bend a
        // linear curve toward n log n.
        var result = LeetCodeTests.Measure("leetcode/KthLargest.cs", "FindKthLargest", GenerationStrategy.Random);
        var enqueue = result.Detail!.LibraryCalls.Single(c => c.Member == "PriorityQueue.Enqueue");

        Assert.True(enqueue.Work <= enqueue.Calls * 3, $"enqueue charged {enqueue.Work} for {enqueue.Calls} calls into a heap of size <= 4");
    }
}
