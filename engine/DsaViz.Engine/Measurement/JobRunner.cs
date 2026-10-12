using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Measurement;

/// <summary>
/// Runs a measurement request from the editor, optionally against a second solution.
/// Lives here rather than in the CLI entry point so it can be tested directly.
/// </summary>
public static class JobRunner
{
    public static JobResponse Run(JobRequest request, Func<string, bool>? exists = null,
        Func<string, string>? read = null)
    {
        exists ??= File.Exists;
        read ??= File.ReadAllText;

        if (!exists(request.File))
            return new JobResponse { Errors = new[] { $"File not found: {request.File}" } };

        var errors = new List<string>();

        string primarySource = read(request.File);

        if (request.StepSize is { } stepSize)
        {
            var steps = Stepper.Record(
                DsaViz.Engine.Analysis.SourceAnalysis.Create(primarySource), request.Entry, request.Inputs,
                stepSize, request.MaxSteps, request.Strategy, request.Budget, request.Values, request.Example);
            return new JobResponse
            {
                Steps = steps,
                Source = primarySource,
                Label = Path.GetFileNameWithoutExtension(request.File)
            };
        }

        var primary = Measurer.Measure(
            primarySource, request.Entry, request.Inputs, request.Sizes, request.Budget, request.Strategy);

        MeasurementResult? comparison = null;
        string? comparisonSource = null;

        if (request.CompareFile is { } comparePath)
        {
            if (!exists(comparePath))
            {
                errors.Add($"Comparison file not found: {comparePath}");
            }
            else
            {
                comparisonSource = read(comparePath);

                // Both solutions must see the same sizes and the same generated data,
                // or the gap between the curves would partly be the inputs rather than
                // the algorithms. So the primary's resolved configuration is reused
                // instead of being inferred again from the second signature.
                comparison = Measurer.Measure(
                    comparisonSource,
                    request.CompareEntry ?? request.Entry,
                    primary.Inputs.Count > 0 ? primary.Inputs : request.Inputs,
                    primary.Points.Count > 0 ? primary.Points.Select(p => p.Size).ToArray() : request.Sizes,
                    request.Budget);
            }
        }

        return new JobResponse
        {
            Primary = primary,
            Comparison = comparison,
            Source = primarySource,
            ComparisonSource = comparisonSource,
            Label = Path.GetFileNameWithoutExtension(request.File),
            ComparisonLabel = request.CompareFile is null
                ? null
                : Path.GetFileNameWithoutExtension(request.CompareFile),
            Errors = errors
        };
    }
}
