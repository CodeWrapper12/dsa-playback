using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// "How the calculation happens" — the part of the original request the first spec
/// dropped. A Big-O label says what; the derivation says why: which statement makes the
/// method that class, and by what formula.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class DerivationTests
{
    [Fact]
    public void Bubble_sort_is_derived_from_its_comparison_running_half_n_squared_times()
    {
        // The textbook analysis: the comparison runs n(n−1)/2 times. Measured, that is
        // ≈ 0.5·n², and it is that statement — not the loops around it — that sets the class.
        var result = LeetCodeTests.Measure("SortParamSample.cs", "Sort");
        var derivation = result.Derivation!;

        Assert.StartsWith("O(n²) because", derivation.Summary);

        var comparison = derivation.Statements.Single(s => s.Snippet.StartsWith("if (values[j] > values[j + 1])"));
        Assert.Equal(ComplexityClass.Quadratic, comparison.Cost);
        Assert.Equal("≈ 0.5·n²", comparison.CostFormula);
        Assert.Contains(comparison.Id, derivation.Dominant);
    }

    [Fact]
    public void Every_statement_carries_its_own_growth()
    {
        var derivation = LeetCodeTests.Measure("SortParamSample.cs", "Sort").Derivation!;

        ComplexityClass Of(string prefix) => derivation.Statements.Single(s => s.Snippet.StartsWith(prefix)).Runs;

        Assert.Equal(ComplexityClass.Constant, Of("for (int i = 0;"));      // the outer header runs once
        Assert.Equal(ComplexityClass.Linear, Of("for (int j = 0;"));        // the inner header, once per outer pass
        Assert.Equal(ComplexityClass.Quadratic, Of("if (values[j]"));      // the body, n²/2 times
    }

    [Fact]
    public void A_statement_that_costs_more_than_it_runs_is_explained_by_its_library_call()
    {
        // The derivation for the List.Contains trap: the line runs n times — which every
        // count would show — but costs n²/2, and the reason is the call on that line.
        var derivation = LeetCodeTests.Measure("leetcode/DedupeWithList.cs", "CountDistinct", GenerationStrategy.Distinct).Derivation!;
        var line = derivation.Statements.Single(s => s.Snippet.StartsWith("if (!seen.Contains"));

        Assert.Equal(ComplexityClass.Linear, line.Runs);
        Assert.Equal(ComplexityClass.Quadratic, line.Cost);
        Assert.Contains("List<T>.Contains", line.Because);
        Assert.Contains("List<T>.Contains is O(n) per call", derivation.Summary);
    }

    [Fact]
    public void An_exponential_derivation_names_the_gap_between_the_bound_and_the_measurement()
    {
        var derivation = LeetCodeTests.Measure("FibonacciParamSample.cs", "Fib").Derivation!;

        Assert.Contains("O(2ⁿ) is the usual bound", derivation.Summary);
        Assert.Contains("1.6", derivation.Summary);
    }

    [Theory]
    [InlineData(ComplexityClass.Quadratic, 0.5, "≈ 0.5·n²")]
    [InlineData(ComplexityClass.Linear, 1.0, "≈ n")]
    [InlineData(ComplexityClass.Linear, 2.0, "≈ 2·n")]
    [InlineData(ComplexityClass.Linearithmic, 1.0, "≈ n log n")]
    [InlineData(ComplexityClass.Exponential, 1.618, "≈ 1.62ⁿ")]
    public void Formulas_read_like_the_textbook(ComplexityClass value, double coefficient, string expected)
    {
        var fit = new CurveFit(value, 1, new[] { new ClassFit(value, 1, coefficient) }, "");
        Assert.Equal(expected, Growth.Formula(fit));
    }
}

/// <summary>
/// "How memory is allocated" — the other half of the original request. Space is read
/// three ways from the measurement, because total allocation and space held at once are
/// different things, and confusing them is a common interview mistake.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class SpaceTests
{
    private static SpaceReading Space(string sample, string entry, GenerationStrategy? strategy = null)
        => LeetCodeTests.Measure(sample, entry, strategy).Space!;

    /// <summary>Each size's total, largest single allocation and collections, for a failure message.</summary>
    private static string Evidence(SpaceReading space)
        => string.Join("; ", space.Evidence.Select(e => $"n={e.Size}: total {e.Total}, largest {e.Largest}, collections {e.Collections}"));

    [Fact]
    public void An_in_place_sort_uses_constant_space()
    {
        var space = Space("SortParamSample.cs", "Sort");
        Assert.Equal(ComplexityClass.Constant, space.LikelyPeak);
        Assert.Equal(ComplexityClass.Constant, space.Static.Class);
    }

    [Fact]
    public void A_hash_map_that_grows_to_n_is_linear_space()
    {
        var space = Space("leetcode/TwoSum.cs", "TwoSum", GenerationStrategy.Distinct);

        Assert.Equal(ComplexityClass.Linear, space.Allocation.Best);
        Assert.Equal(ComplexityClass.Linear, space.LikelyPeak);
        Assert.Equal(ComplexityClass.Linear, space.Static.Class);
    }

    [Theory]
    [InlineData("leetcode/AllocateInLoop.cs", "Scratch")]
    [InlineData("leetcode/JoinWithPlus.cs", "Interleave")]
    public void Churn_allocates_quadratic_but_holds_linear(string sample, string entry)
    {
        // A fresh n-sized buffer, or a fresh copy of a growing string, on every pass:
        // O(n²) bytes allocated, O(n) ever held at once. Reporting O(n²) as the space would
        // be the interview mistake this exists to prevent.
        var space = Space(sample, entry, GenerationStrategy.Random);

        Assert.True(space.Allocation.Best == ComplexityClass.Quadratic && space.LargestAllocation.Best == ComplexityClass.Linear,
            $"allocation {space.Allocation.Best}, largest {space.LargestAllocation.Best}. Measured: {Evidence(space)}");
        Assert.Equal(ComplexityClass.Quadratic, space.Allocation.Best);
        Assert.Equal(ComplexityClass.Linear, space.LargestAllocation.Best);
        Assert.Equal(ComplexityClass.Linear, space.LikelyPeak);
        Assert.Contains("never more than O(n) at once", space.Summary);
    }

    [Fact]
    public void One_outlying_reading_does_not_hide_a_line()
    {
        // The readings from the run on GitHub's Intel Mac that failed: int[n] allocated n times,
        // and at n=1,000 one execution read 6,400 bytes where the array is 4,024, with no
        // collection during the run.
        int[] sizes = { 10, 25, 50, 100, 250, 500, 1000 };
        long[] totals = { 640, 3200, 11200, 42400, 256000, 1012000, 4026376 };
        long[] largest = { 64, 128, 224, 424, 1024, 2024, 6400 };
        var runs = sizes.Select((n, i) => new MeasuredRun(n, new TraceResult
        {
            Status = RunStatus.Completed,
            Statements = new[] { new StatementCost(0, 5, 9, 40, "int[] buffer = new int[values.Length];", n, totals[i]) { LargestAllocation = largest[i] } },
            Lines = Array.Empty<LineCost>(),
            TotalOperations = n,
            TotalBytesAllocated = totals[i],
            ElapsedMs = 1
        })).ToList();

        var space = Derivations.Space(runs, null, new StaticEstimate(ComplexityClass.Unknown, "", 0, 0, 0))!;

        Assert.Equal(ComplexityClass.Linear, space.LargestAllocation.Best);
        Assert.Contains("n=1,000, was left out", space.LargestAllocation.Explanation);
        Assert.Equal(ComplexityClass.Linear, space.LikelyPeak);
    }

    [Fact]
    public void The_churn_explanation_points_at_the_line_and_the_fix()
    {
        var space = Space("leetcode/AllocateInLoop.cs", "Scratch", GenerationStrategy.Random);

        Assert.True(space.Summary.Contains("int[] buffer = new int[values.Length];"), $"{space.Summary} Measured: {Evidence(space)}");
        Assert.Contains("outside the loop", space.Summary);
    }

    [Fact]
    public void Tree_shape_decides_stack_space()
    {
        var skewed = Space("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Skewed);
        var balanced = Space("leetcode/MaxDepth.cs", "MaxDepth", GenerationStrategy.Balanced);

        Assert.Equal(ComplexityClass.Linear, skewed.CallDepth.Best);
        Assert.Equal(ComplexityClass.Logarithmic, balanced.CallDepth.Best);

        // The code's reading is the worst case, O(h) = O(n); a balanced tree measures less,
        // and the gap is reported rather than reconciled.
        Assert.Equal(ComplexityClass.Linear, balanced.Static.Class);
        Assert.True(balanced.Disagreement);
        Assert.Contains("shape", balanced.DisagreementNote);
    }

    [Fact]
    public void Divide_and_conquer_recurses_log_n_deep()
    {
        var space = Space("MergeSortSample.cs", "Sort");

        Assert.Equal(ComplexityClass.Logarithmic, space.CallDepth.Best);
        Assert.True(space.Allocation.Best == ComplexityClass.Linear,    // the merge buffer
            $"Allocation read as {space.Allocation.Best}: {space.Allocation.Explanation}");
        Assert.Equal(ComplexityClass.Linear, space.LikelyPeak);
    }

    [Fact]
    public void Branching_recursion_holds_only_one_path_on_the_stack()
    {
        // Fibonacci makes an exponential number of calls but is only ever n deep.
        var space = Space("FibonacciParamSample.cs", "Fib");

        Assert.Equal(ComplexityClass.Linear, space.CallDepth.Best);
        Assert.Equal(ComplexityClass.Linear, space.Static.Class);
    }

    [Fact]
    public void Each_allocating_statement_carries_its_own_memory_growth()
    {
        // The per-declaration view: the buffer line allocates O(n²) in total, O(n) each time.
        var derivation = LeetCodeTests.Measure("leetcode/AllocateInLoop.cs", "Scratch", GenerationStrategy.Random).Derivation!;
        var buffer = derivation.Statements.Single(s => s.Snippet.StartsWith("int[] buffer"));

        Assert.Equal(ComplexityClass.Quadratic, buffer.Memory);
        Assert.Equal(ComplexityClass.Linear, buffer.LargestAllocation);
    }
}

public class StaticSpaceTests
{
    [Theory]
    [InlineData("leetcode/ReverseList.cs", "ReverseList", ComplexityClass.Constant, "Nothing it allocates")]
    [InlineData("leetcode/TwoSum.cs", "TwoSum", ComplexityClass.Linear, "gains an entry")]
    [InlineData("leetcode/ClimbStairsMemo.cs", "ClimbStairs", ComplexityClass.Linear, "memo table")]
    [InlineData("leetcode/AllocateInLoop.cs", "Scratch", ComplexityClass.Linear, "only one is held at a time")]
    [InlineData("leetcode/JoinWithPlus.cs", "Interleave", ComplexityClass.Linear, "final string")]
    [InlineData("leetcode/CountComponents.cs", "CountComponents", ComplexityClass.Linear, null)]
    [InlineData("leetcode/NumIslands.cs", "NumIslands", ComplexityClass.Unknown, "rows × columns")]
    [InlineData("MergeSortSample.cs", "Sort", ComplexityClass.Linear, "n-sized array")]
    public void The_code_is_read_for_space(string sample, string entry, ComplexityClass expected, string? reason)
    {
        var estimate = StaticEstimator.EstimateSpace(Samples.Read(sample), entry);

        Assert.Equal(expected, estimate.Class);
        if (reason is not null) Assert.Contains(reason, estimate.Reasoning);
    }
}

[Collection(ExactMemoryCollection.Name)]
public class LargestAllocationTests
{
    [Fact]
    public void The_largest_single_allocation_is_kept_per_statement()
    {
        var state = DsaViz.Runtime.TraceState.Begin(statementCount: 1, siteCount: 0, budget: 1_000);
        for (int i = 0; i < 3; i++)
        {
            state.Hit(0);
            _ = new byte[i == 1 ? 4_000 : 100];
        }
        state.End();

        var snapshot = state.Snapshot();
        Assert.InRange(snapshot.LargestAllocation[0], 4_000, 4_100);
        Assert.True(snapshot.Bytes[0] > snapshot.LargestAllocation[0]);
    }
}
