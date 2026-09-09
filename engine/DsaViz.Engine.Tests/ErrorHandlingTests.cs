using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

public class ErrorHandlingTests
{
    [Fact]
    public void Syntax_errors_are_reported_not_thrown()
    {
        var result = Runner.Run("public static class Broken { public static object Solve() { return", "Solve");

        Assert.Equal(RunStatus.CompileError, result.Status);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_missing_entry_point_names_what_was_available()
    {
        var result = Runner.Run(
            "public static class Thing { public static object Run() { return 1; } }", "Solve");

        Assert.Equal(RunStatus.RuntimeError, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("Solve") && e.Contains("Run"));
    }

    [Fact]
    public void A_solution_that_throws_reports_the_exception_rather_than_crashing()
    {
        var result = Runner.Run(
            """
            public static class Boom
            {
                public static object Solve()
                {
                    int[] a = new int[2];
                    return a[5];
                }
            }
            """, "Solve");

        Assert.Equal(RunStatus.RuntimeError, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("IndexOutOfRange"));
    }
}
