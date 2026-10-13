using System.Diagnostics;
using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

[Collection(TimingCollection.Name)]
public class BudgetTests
{
    [Fact]
    public void Infinite_loop_is_stopped_by_the_budget()   // criterion 7
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Runner.Run(Samples.Read("InfiniteLoopSample.cs"), "Solve", budget: 200_000);
        stopwatch.Stop();

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.NotEmpty(result.Errors);
        Assert.True(stopwatch.Elapsed.TotalSeconds < 10,
            $"a runaway solution must be cut off promptly; took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void Budget_exceeded_still_reports_the_lines_that_ran()
    {
        var result = Runner.Run(Samples.Read("InfiniteLoopSample.cs"), "Solve", budget: 50_000);

        // A timed-out run is still informative — it shows exactly which line spun.
        Assert.NotEmpty(result.Lines);
        Assert.Contains(result.Lines, line => line.Hits > 10_000);
    }

    [Fact]
    public void A_braceless_infinite_loop_is_also_stopped()
    {
        // The budget can only fire from inside an instrumented statement. A braceless
        // loop body is not in a block, so it is only reachable because BraceNormalizer
        // put it in one. Without that, this hangs forever rather than failing — which
        // is how it behaved when the normaliser was disabled during development.
        var result = Runner.Run(
            """
            public static class Spin
            {
                public static object Solve()
                {
                    int i = 0;
                    while (true) i++;
                }
            }
            """, "Solve", budget: 100_000);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
    }

    [Fact]
    public void A_generous_budget_does_not_interfere_with_normal_solutions()
    {
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");
        Assert.Equal(RunStatus.Completed, result.Status);
    }
}
