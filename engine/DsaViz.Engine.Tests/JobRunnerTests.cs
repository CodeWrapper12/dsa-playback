using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

public class JobRunnerTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["bubble.cs"] = Samples.Read("SortParamSample.cs"),
        ["merge.cs"] = Samples.Read("MergeSortSample.cs")
    };

    private static JobResponse Run(JobRequest request) =>
        JobRunner.Run(request, Files.ContainsKey, path => Files[path]);

    [Fact]
    public void A_job_returns_the_source_alongside_the_measurement()
    {
        // The view lays costs over the code. Sending the text back with the result is
        // what stops it from re-reading the file and rendering against a version that
        // has already been edited.
        var response = Run(new JobRequest { File = "bubble.cs", Entry = "Sort" });

        Assert.NotNull(response.Primary);
        Assert.Equal(RunStatus.Completed, response.Primary!.Status);
        Assert.Contains("class SortParamSample", response.Source);
        Assert.Equal("bubble", response.Label);
    }

    [Fact]
    public void Two_solutions_are_measured_over_identical_sizes()
    {
        var response = Run(new JobRequest
        {
            File = "bubble.cs", Entry = "Sort",
            CompareFile = "merge.cs", CompareEntry = "Sort"
        });

        Assert.NotNull(response.Comparison);
        Assert.Equal(
            response.Primary!.Points.Select(p => p.Size),
            response.Comparison!.Points.Select(p => p.Size));
    }

    [Fact]
    public void The_comparison_shows_the_difference_the_algorithms_make()
    {
        var response = Run(new JobRequest
        {
            File = "bubble.cs", Entry = "Sort",
            CompareFile = "merge.cs", CompareEntry = "Sort",
            Sizes = new[] { 10, 25, 50, 100, 250, 500, 1000 }
        });

        Assert.Equal(ComplexityClass.Quadratic, response.Primary!.Empirical.Best);
        Assert.Equal(ComplexityClass.Linearithmic, response.Comparison!.Empirical.Best);

        long Largest(MeasurementResult result) => result.Points[^1].Operations;
        Assert.True(Largest(response.Primary) > Largest(response.Comparison) * 5,
            "the quadratic solution should be dramatically more expensive at the largest size");
    }

    [Fact]
    public void A_missing_file_is_reported_rather_than_thrown()
    {
        var response = Run(new JobRequest { File = "nope.cs", Entry = "Sort" });

        Assert.Null(response.Primary);
        Assert.Contains(response.Errors, e => e.Contains("not found"));
    }

    [Fact]
    public void A_missing_comparison_file_does_not_lose_the_primary_result()
    {
        var response = Run(new JobRequest
        {
            File = "bubble.cs", Entry = "Sort", CompareFile = "gone.cs"
        });

        Assert.NotNull(response.Primary);
        Assert.Equal(RunStatus.Completed, response.Primary!.Status);
        Assert.Null(response.Comparison);
        Assert.Contains(response.Errors, e => e.Contains("Comparison file not found"));
    }
}
