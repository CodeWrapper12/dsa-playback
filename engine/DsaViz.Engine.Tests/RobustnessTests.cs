using System.Diagnostics;
using System.Globalization;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Runtime;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// The ways a practice solution goes wrong — a missing base case, a hang, a typo — must
/// end in a readable result, never a dead engine or a frozen editor.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void A_missing_base_case_is_reported_instead_of_crashing_the_engine()
    {
        // Before this, a real StackOverflowException killed the engine process outright.
        var outcome = Runner.Compile(
            "public static class Bad { public static int Recurse(int n) { return Recurse(n + 1) + 1; } }", "Recurse");
        using var solution = outcome.Solution!;

        var result = Runner.Invoke(solution, new object?[] { 0 }, depthLimit: 2_000);

        Assert.Equal(RunStatus.RecursionLimitExceeded, result.Status);
        Assert.Contains("base case", result.Errors.Single());
    }

    [Fact]
    public void Runaway_recursion_fails_fast_and_says_why()
    {
        // Infinite recursion fails at every size, so it is reported once, quickly, with
        // the reason as the error rather than buried in a note.
        var stopwatch = Stopwatch.StartNew();
        var result = DsaViz.Engine.Measurement.Measurer.Measure(
            "public class Solution { public int Go(int n) { return Go(n + 1) + 1; } }", "Solution.Go");
        stopwatch.Stop();

        Assert.Equal(RunStatus.RecursionLimitExceeded, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("base case"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void Legitimately_deep_recursion_still_runs()
    {
        // 15,000 instrumented frames overflows the default 1 MB stack; runs get a much larger one.
        var result = Runner.Run(
            """
            public static class Deep
            {
                private static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1);
                public static object Solve() => Depth(15_000);
            }
            """, "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("15000", result.ReturnValue);
        Assert.True(result.MaxRecursionDepth > 15_000);
    }

    [Fact]
    public void A_single_statement_that_never_returns_times_out()
    {
        // The statement budget only counts at statement boundaries, so it cannot stop
        // this. The wall-clock limit can.
        var outcome = Runner.Compile(
            "public static class Sleepy { public static object Solve() { System.Threading.Thread.Sleep(20_000); return 1; } }", "Solve");
        var solution = outcome.Solution!;

        var stopwatch = Stopwatch.StartNew();
        var result = Runner.Invoke(solution, timeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal(RunStatus.TimedOut, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(solution.Abandoned);
    }

    [Fact]
    public void An_abandoned_solution_is_never_run_again()
    {
        var solution = Runner.Compile(
            "public static class Sleepy2 { public static object Solve() { System.Threading.Thread.Sleep(20_000); return 1; } }", "Solve").Solution!;
        Runner.Invoke(solution, timeout: TimeSpan.FromMilliseconds(200));

        var again = Runner.Invoke(solution);

        Assert.Equal(RunStatus.RuntimeError, again.Status);
        Assert.Contains("abandoned", again.Errors.Single());
    }

    [Fact]
    public void An_abandoned_thread_stops_itself_at_its_next_statement()
    {
        // A spinning loop is abandoned after its timeout. Its tracer slot is revoked, so the
        // very next instrumented statement throws and the thread unwinds instead of
        // burning a core for the life of the process.
        var solution = Runner.Compile(
            """
            public static class Spinner
            {
                public static long Ticks;
                public static object Solve() { while (true) { Ticks++; } }
            }
            """, "Solve").Solution!;

        var result = Runner.Invoke(solution, budget: long.MaxValue, timeout: TimeSpan.FromMilliseconds(300));
        Assert.Equal(RunStatus.TimedOut, result.Status);

        var ticks = solution.Entry.DeclaringType!.GetField("Ticks")!;
        Thread.Sleep(100);
        long first = (long)ticks.GetValue(null)!;
        Thread.Sleep(300);
        long second = (long)ticks.GetValue(null)!;

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_later_run_is_unaffected_by_an_abandoned_one()
    {
        var hung = Runner.Compile(
            "public static class Hung { public static object Solve() { while (true) { } } }", "Solve").Solution!;
        Runner.Invoke(hung, budget: long.MaxValue, timeout: TimeSpan.FromMilliseconds(200));

        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains(result.Lines, line => line.Hits == 3540);
    }

    [Fact]
    public void Compile_errors_point_at_the_line_in_the_users_file()
    {
        // Instrumentation inserts statements and shifts every line below them. Errors are
        // taken from the user's own file so the line number is the one they can see.
        var result = Runner.Run(
            "public static class Typo\n{\n    public static object Solve()\n    {\n        int x = 1;\n        return undefinedName;\n    }\n}",
            "Solve");

        Assert.Equal(RunStatus.CompileError, result.Status);
        Assert.Contains(result.Errors, e => e.StartsWith("line 6,") && e.Contains("undefinedName"));
    }

    [Fact]
    public void Limits_are_described_the_same_way_on_every_machine()
    {
        // On an Indian-locale machine N0 formatting prints 5,00,00,000 — while the editor
        // prints 50,000,000. Messages are formatted invariantly so the two always agree.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-IN");
            Assert.Contains("50,000,000", new ExecutionBudgetExceededException(50_000_000).Message);
            Assert.Contains("20,000", new RecursionLimitExceededException(20_000).Message);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}

/// <summary>Where bytes and work are attributed when calls return and the tracer itself allocates.</summary>
public class AttributionTests
{
    [Fact]
    public void Allocation_after_a_call_returns_belongs_to_the_calling_statement()
    {
        // `new int[Size()]` calls Size first, then allocates. Without restoring the caller
        // on return, the 424 bytes were charged to Size's `return k;` instead.
        var result = Runner.Run(
            """
            public static class Calls
            {
                private static int Size() { int k = 100; return k; }
                public static object Solve()
                {
                    int[] a = new int[Size()];
                    return a.Length;
                }
            }
            """, "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(24 + 100 * sizeof(int), result.Statements.Single(s => s.Snippet.StartsWith("int[] a")).BytesAllocated);
        Assert.Equal(0, result.Statements.Single(s => s.Snippet == "return k;").BytesAllocated);
    }

    private sealed class CountedBag { public int Count => 7; }

    [Fact]
    public void The_tracers_own_allocation_is_never_charged_to_the_solution()
    {
        // The first time a collection type is measured, the tracer compiles a reader for
        // its Count property, which allocates. That must not show up as the user's memory.
        // Created before the statement begins, so the only thing that can allocate inside
        // statement 0 is the tracer reading its size for the first time.
        var bag = new CountedBag();

        var state = TraceState.Begin(statementCount: 2, siteCount: 1, budget: 1_000);
        state.Hit(0);
        state.Through(bag, site: 0, kind: CostKind.Linear);
        state.Hit(1);
        state.End();

        var snapshot = state.Snapshot();
        Assert.Equal(0, snapshot.Bytes[0]);
        Assert.Equal(7, snapshot.SiteUnits[0]);   // and the reader still saw Count = 7
    }
}

public class StaticRecursionTests
{
    [Theory]
    [InlineData("leetcode/MaxDepth.cs", "MaxDepth", ComplexityClass.Linear, "child nodes")]
    [InlineData("leetcode/NumIslands.cs", "NumIslands", ComplexityClass.Unknown, "visited")]
    [InlineData("leetcode/ClimbStairsMemo.cs", "ClimbStairs", ComplexityClass.Unknown, "caches results")]
    [InlineData("FibonacciParamSample.cs", "Fib", ComplexityClass.Exponential, "multiplies")]
    [InlineData("MergeSortSample.cs", "Sort", ComplexityClass.Unknown, "divide and conquer")]
    [InlineData("leetcode/ArrowLocalRecursion.cs", "CountDown", ComplexityClass.Unknown, "calls itself once")]
    public void Recursion_is_classified_by_what_it_recurses_on(
        string sample, string entry, ComplexityClass expected, string explanation)
    {
        // Two self-calls are exponential in Fibonacci and linear in a tree traversal; the
        // difference is the arguments and the bookkeeping around the calls.
        var estimate = StaticEstimator.Estimate(Samples.Read(sample), entry);

        Assert.Equal(expected, estimate.Class);
        Assert.Contains(explanation, estimate.Reasoning);
    }

    [Fact]
    public void A_sliding_window_is_read_as_linear_not_quadratic()
    {
        // An inner while inside a for looks like O(n²), but the window's left edge only
        // moves forward, so across the whole loop it advances at most n times.
        var estimate = StaticEstimator.Estimate(Samples.Read("leetcode/LongestSubstring.cs"), "LengthOfLongestSubstring");

        Assert.Equal(ComplexityClass.Linear, estimate.Class);
        Assert.Contains("'left' forward", estimate.Reasoning);
    }

    [Fact]
    public void A_bfs_is_not_misread_as_cubic()
    {
        // for → while(queue) → foreach(neighbour) is three loops deep and O(V + E).
        // The depth reading is withheld rather than reported.
        var estimate = StaticEstimator.Estimate(Samples.Read("leetcode/CountComponents.cs"), "CountComponents");

        Assert.Equal(ComplexityClass.Unknown, estimate.Class);
        Assert.Contains("O(V + E)", estimate.Reasoning);
    }
}
