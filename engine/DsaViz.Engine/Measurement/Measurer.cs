using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;

namespace DsaViz.Engine.Measurement;

public static class Measurer
{
    /// <summary>
    /// Spans two orders of magnitude and includes 10, 100 and 1000 so the default run
    /// satisfies the stated acceptance criterion, with enough intermediate points that
    /// neighbouring classes can actually be told apart.
    /// </summary>
    public static readonly int[] DefaultSizes = { 10, 25, 50, 100, 250, 500, 1000 };

    /// <summary>
    /// Used when the default ladder is too aggressive. An exponential solution exhausts
    /// the budget by n=30, so measuring it at all requires dropping to sizes that look
    /// absurd for a linear algorithm.
    /// </summary>
    public static readonly int[] SmallSizes = { 4, 6, 8, 10, 12, 14, 16, 18 };

    private const int MinimumPointsForCurve = 3;

    public static MeasurementResult Measure(
        string source,
        string entryMethod,
        IReadOnlyList<InputSpec>? inputs = null,
        IReadOnlyList<int>? sizes = null,
        long budget = Runner.DefaultBudget)
    {
        var outcome = Runner.Compile(source, entryMethod, instrument: true);
        if (!outcome.Success)
            return Failed(outcome.Status, outcome.Errors, inputs ?? Array.Empty<InputSpec>());

        using var solution = outcome.Solution!;

        IReadOnlyList<InputSpec> specs;
        try
        {
            specs = inputs ?? ParameterBinder.Infer(solution.Parameters);
            ParameterBinder.Validate(solution.Parameters, specs);
        }
        catch (UnsupportedSignatureException ex)
        {
            return Failed(RunStatus.RuntimeError, new[] { ex.Message }, inputs ?? Array.Empty<InputSpec>());
        }

        var requested = sizes ?? DefaultSizes;
        var (points, detail, note, errors) = RunLadder(solution, specs, requested, budget);

        // An algorithm that exhausts the budget almost immediately produced too few
        // points to say anything. Rather than reporting Unknown, retry small — the
        // measurement range should fit the algorithm, not the other way round.
        if (points.Count < MinimumPointsForCurve && sizes is null && requested != SmallSizes)
        {
            var retry = RunLadder(solution, specs, SmallSizes, budget);
            if (retry.Points.Count > points.Count)
            {
                (points, detail, note, errors) = retry;
                note = "The default sizes exhausted the execution budget immediately, so the " +
                       "measurement was retried at much smaller inputs. " + (note ?? "");
            }
        }

        var empirical = CurveFitter.Fit(points);
        var staticEstimate = StaticEstimator.Estimate(source, entryMethod);

        bool disagreement =
            empirical.Best != ComplexityClass.Unknown &&
            staticEstimate.Class != ComplexityClass.Unknown &&
            empirical.Best != staticEstimate.Class;

        return new MeasurementResult
        {
            Status = points.Count > 0 ? RunStatus.Completed : RunStatus.RuntimeError,
            Points = points,
            Empirical = empirical,
            Static = staticEstimate,
            Disagreement = disagreement,
            DisagreementNote = DescribeDisagreement(disagreement, empirical, staticEstimate),
            Detail = detail,
            Inputs = specs,
            Errors = errors,
            SizeNote = string.IsNullOrWhiteSpace(note) ? null : note!.Trim()
        };
    }

    private static (List<SizePoint> Points, TraceResult? Detail, string? Note, List<string> Errors)
        RunLadder(CompiledSolution solution, IReadOnlyList<InputSpec> specs, IReadOnlyList<int> sizes, long budget)
    {
        var points = new List<SizePoint>();
        var errors = new List<string>();
        TraceResult? detail = null;
        string? note = null;

        foreach (int size in sizes.OrderBy(s => s))
        {
            object?[] arguments;
            try
            {
                arguments = ParameterBinder.Bind(specs, size);
            }
            catch (Exception ex)
            {
                errors.Add($"n={size}: could not build inputs — {ex.Message}");
                continue;
            }

            var trace = Runner.Invoke(solution, arguments, budget);

            if (trace.Status == RunStatus.BudgetExceeded)
            {
                // Larger sizes can only cost more, so there is nothing to learn by
                // continuing up the ladder.
                note = $"Stopped at n={size}: the execution budget of {budget:N0} statements was " +
                       "exceeded. Points below this size are still valid.";
                break;
            }

            if (trace.Status != RunStatus.Completed)
            {
                errors.Add($"n={size}: {string.Join("; ", trace.Errors)}");
                continue;
            }

            points.Add(new SizePoint(size, trace.TotalOperations, trace.TotalBytesAllocated, trace.ElapsedMs));
            detail = trace;   // keep the largest completed run for the per-statement view
        }

        return (points, detail, note, errors);
    }

    private static string? DescribeDisagreement(bool disagreement, CurveFit empirical, StaticEstimate estimate)
    {
        if (!disagreement)
        {
            if (estimate.Class == ComplexityClass.Unknown && empirical.Best != ComplexityClass.Unknown)
                return "Reading the code alone was not enough to name a class, so the measured " +
                       "curve is the only evidence here.";
            return null;
        }

        return $"The code reads as {ComplexityNames.Format(estimate.Class)} " +
               $"({estimate.Reasoning}) but it measured as {ComplexityNames.Format(empirical.Best)}. " +
               "The measurement is what actually happened on this input; the usual explanations are " +
               "an early exit that keeps the worst case from occurring, input data that avoids it, " +
               "or a loop whose bound does not grow with n.";
    }

    private static MeasurementResult Failed(
        RunStatus status, IReadOnlyList<string> errors, IReadOnlyList<InputSpec> inputs) => new()
    {
        Status = status,
        Points = Array.Empty<SizePoint>(),
        Empirical = new CurveFit(ComplexityClass.Unknown, 0, Array.Empty<ClassFit>(), "Nothing was measured."),
        Static = new StaticEstimate(ComplexityClass.Unknown, "Not analysed.", 0, 0, 0),
        Disagreement = false,
        Inputs = inputs,
        Errors = errors
    };
}
