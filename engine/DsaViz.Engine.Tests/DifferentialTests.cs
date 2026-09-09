using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Acceptance criterion 8. Every number this tool ever reports describes the
/// instrumented program; if that program behaves differently from the one the user
/// wrote, every later measurement is a confident description of something that does
/// not exist. This is therefore the foundational test of the whole engine.
/// </summary>
public class DifferentialTests
{
    [Theory]
    [MemberData(nameof(Samples.Terminating), MemberType = typeof(Samples))]
    public void Instrumentation_does_not_change_the_return_value(string sample)
    {
        string source = Samples.Read(sample);

        var baseline = Runner.RunBaseline(source, "Solve");
        var instrumented = Runner.Run(source, "Solve");

        Assert.Equal(RunStatus.Completed, baseline.Status);
        Assert.Equal(RunStatus.Completed, instrumented.Status);
        Assert.Equal(baseline.ReturnValue, instrumented.ReturnValue);
    }

    [Theory]
    [MemberData(nameof(Samples.Terminating), MemberType = typeof(Samples))]
    public void Instrumentation_does_not_change_console_output(string sample)
    {
        string source = Samples.Read(sample);

        var baseline = Runner.RunBaseline(source, "Solve");
        var instrumented = Runner.Run(source, "Solve");

        Assert.Equal(baseline.Output, instrumented.Output);
    }

    [Fact]
    public void Braceless_early_return_still_returns_the_right_values()
    {
        // Named explicitly because this is the exact failure brace normalisation
        // exists to prevent: without it, Classify returns -1 for every input and the
        // result would be a list of ten -1s.
        var result = Runner.Run(Samples.Read("EarlyReturnSample.cs"), "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("[-1, -1, -1, 0, 1, 2, 1, 2, 1, 2]", result.ReturnValue);
    }
}
