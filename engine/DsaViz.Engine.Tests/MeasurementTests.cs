using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

[Collection(ExactMemoryCollection.Name)]
public class MeasurementTests
{
    [Fact]
    public void Per_line_execution_counts_are_recorded()   // criterion 2
    {
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.NotEmpty(result.Lines);

        const int n = 60;
        const long comparisons = n * (n - 1) / 2;   // 1770 for n = 60

        // The innermost line holds both the comparison and the swap. The sample feeds
        // a reverse-sorted array, so every comparison also swaps, and the line
        // accumulates both statements.
        Assert.Contains(result.Lines, line => line.Hits == comparisons * 2);

        // The two enclosing loop headers are on their own lines and are reached far
        // less often — that contrast is the product.
        Assert.Contains(result.Lines, line => line.Hits == n - 1);
    }

    [Fact]
    public void Hot_line_is_distinguishable_from_cold_line()
    {
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");

        long hottest = result.Lines.Max(l => l.Hits);
        long coldest = result.Lines.Min(l => l.Hits);

        // The whole product rests on this contrast being visible: the nested loop body
        // must stand out by orders of magnitude from a line that runs once.
        Assert.Equal(1, coldest);
        Assert.True(hottest > 1000, $"expected a clearly hot line, hottest was {hottest}");
    }

    [Fact]
    public void Braceless_bodies_are_measured_not_silently_skipped()
    {
        // The failure this guards against is quiet: the program still produces the
        // right answer while its most expensive lines go uncounted. Asserted on
        // explicit numbers because "some lines were measured" would pass even then.
        var result = Runner.Run(
            """
            public static class Braceless
            {
                public static object Solve()
                {
                    int total = 0;
                    for (int i = 0; i < 10; i++) total += i;
                    return total;
                }
            }
            """, "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("45", result.ReturnValue);

        // The loop line carries the for-statement (1) plus ten executions of its
        // braceless body. Unnormalised, it would report 1.
        Assert.Contains(result.Lines, line => line.Hits == 11);
    }

    [Fact]
    public void Statements_sharing_a_line_are_reported_separately()
    {
        // A line total alone cannot distinguish one statement running 3,540 times from
        // two running 1,770 times each, and on this line that difference is the whole
        // question: how much is the comparison costing versus the swap?
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");

        const int n = BubbleSortSample_N;
        const long comparisons = n * (n - 1) / 2;

        var innermost = result.Lines.Single(line => line.Hits == comparisons * 2);
        Assert.Equal(2, innermost.StatementCount);

        var breakdown = result.Statements.Where(s => s.Line == innermost.Line).ToList();
        Assert.Equal(2, breakdown.Count);
        Assert.All(breakdown, statement => Assert.Equal(comparisons, statement.Hits));

        // The snippets are what make the breakdown readable in the UI, and they are
        // the header only — an if's full span would swallow its entire body.
        Assert.Contains(breakdown, s => s.Snippet.StartsWith("if (") && !s.Snippet.Contains("{"));
        Assert.Contains(breakdown, s => s.Snippet.Contains("=") && !s.Snippet.StartsWith("if"));

        // Distinct columns let the UI underline the right span on a shared line.
        Assert.Equal(2, breakdown.Select(s => s.Column).Distinct().Count());
    }

    [Fact]
    public void Statements_are_reported_in_source_order()
    {
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");
        var ordered = result.Statements
            .OrderBy(s => s.Line).ThenBy(s => s.Column).ToList();
        Assert.Equal(ordered, result.Statements);
    }

    private const int BubbleSortSample_N = 60;

    [Fact]
    public void Memory_allocation_is_attributed_to_lines()  // criterion 2, memory half
    {
        var result = Runner.Run(Samples.Read("TwoSumSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.True(result.TotalBytesAllocated > 0,
            "allocating a Dictionary and arrays should register bytes");
        Assert.Contains(result.Lines, line => line.BytesAllocated > 0);
    }

    [Fact]
    public void Recursive_calls_are_counted_across_invocations()
    {
        var result = Runner.Run(Samples.Read("FibonacciSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        // Fib is expression-bodied; if arrow-to-block normalisation failed it would
        // contribute no instrumented lines at all.
        Assert.True(result.TotalOperations > 1000,
            $"recursive fib should execute many statements, saw {result.TotalOperations}");
    }

    [Fact]
    public void Switch_sections_are_instrumented()
    {
        var result = Runner.Run(Samples.Read("SwitchSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("[Sun, Mon, Midweek, Midweek, Other, Other]", result.ReturnValue);
        Assert.True(result.Lines.Count > 3, "case bodies should appear as executed lines");
    }
}
