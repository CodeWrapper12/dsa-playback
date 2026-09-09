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
        var primary = Measurer.Measure(
            primarySource, request.Entry, request.Inputs, request.Sizes, request.Budget);

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
                    request.Inputs ?? primary.Inputs,
                    request.Sizes ?? primary.Points.Select(p => p.Size).ToArray(),
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
