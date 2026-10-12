using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;

namespace DsaViz.Engine.Measurement;

public static class Measurer
{
    /// <summary>Said beside a parameter that cannot be generated, when growth is asked for.</summary>
    public const string NotGeneratedNote =
        "Complexity is measured by running the method on inputs made at many sizes, so it needs every parameter " +
        "to be one that can be generated. You can still press Visualize to watch it run on your example.";

    /// <summary>
    /// Spans two orders of magnitude and includes 10, 100 and 1000 so the default run
    /// satisfies the stated acceptance criterion, with enough intermediate points that
    /// neighbouring classes can actually be told apart.
    /// </summary>
    public static readonly int[] DefaultSizes = { 10, 25, 50, 100, 250, 500, 1000 };

    /// <summary>
    /// Used when a ladder is too aggressive. An exponential solution exhausts the budget
    /// by n=30, so measuring it at all requires dropping to sizes that look absurd for a
    /// linear algorithm.
    /// </summary>
    public static readonly int[] SmallSizes = { 4, 6, 8, 10, 12, 14, 16, 18 };

    private const int MinimumPointsForCurve = 3;

    public static MeasurementResult Measure(
        string source,
        string entryMethod,
        IReadOnlyList<InputSpec>? inputs = null,
        IReadOnlyList<int>? sizes = null,
        long budget = Runner.DefaultBudget,
        GenerationStrategy? strategy = null)
        => Measure(SourceAnalysis.Create(source), entryMethod, inputs, sizes, budget, strategy);

    public static MeasurementResult Measure(
        SourceAnalysis analysis,
        string entryMethod,
        IReadOnlyList<InputSpec>? inputs = null,
        IReadOnlyList<int>? sizes = null,
        long budget = Runner.DefaultBudget,
        GenerationStrategy? strategy = null)
    {
        var outcome = Runner.Compile(analysis, entryMethod, instrument: true);
        if (!outcome.Success)
            return Failed(outcome.Status, outcome.Errors, inputs ?? Array.Empty<InputSpec>());

        using var target = new CSharpTarget(outcome.Solution!);
        return MeasureTarget(target, inputs, sizes, budget, strategy,
            () => StaticEstimator.Estimate(analysis, entryMethod),
            () => StaticEstimator.EstimateSpace(analysis, entryMethod));
    }

    /// <summary>
    /// A Python function, measured exactly as C# is: the same sizes, the same generated
    /// inputs, the same fitting and explanations. Only running it happens in Python.
    /// </summary>
    public static MeasurementResult MeasurePython(
        Python.PythonHost host,
        string source,
        string entry,
        IReadOnlyList<InputSpec>? inputs = null,
        IReadOnlyList<int>? sizes = null,
        long budget = Python.PythonSolution.DefaultBudget,
        GenerationStrategy? strategy = null)
    {
        var solution = Python.PythonSolution.Describe(host, source, entry);
        if (solution.CompileError is { } error)
            return Failed(RunStatus.CompileError, new[] { error }, inputs ?? Array.Empty<InputSpec>());
        if (solution.EntryInfo is null)
            return Failed(RunStatus.CompileError, new[] { $"No function named '{entry}' was found." }, inputs ?? Array.Empty<InputSpec>());

        var parameters = solution.Parameters(solution.Examples());
        using var target = solution.Target(parameters);
        var unknown = new StaticEstimate(ComplexityClass.Unknown, "The code could not be read.", 0, 0, 0);
        return MeasureTarget(target, inputs, sizes, budget, strategy,
            () => solution.Description.Time ?? unknown,
            () => solution.Description.Space ?? unknown);
    }

    private static MeasurementResult MeasureTarget(
        IRunTarget target,
        IReadOnlyList<InputSpec>? inputs,
        IReadOnlyList<int>? sizes,
        long budget,
        GenerationStrategy? strategy,
        Func<StaticEstimate> readTime,
        Func<StaticEstimate> readSpace)
    {
        var parameters = target.Parameters;
        IReadOnlyList<InputSpec> specs;
        IReadOnlyList<ParameterDescription> descriptions;
        var notes = new List<string>();

        try
        {
            descriptions = ParameterBinder.Describe(parameters);
            specs = inputs ?? ApplyStrategy(ParameterBinder.Infer(parameters), strategy, notes);
            ParameterBinder.Validate(parameters, specs);
        }
        catch (UnsupportedSignatureException ex)
        {
            // Measuring growth needs inputs made at many sizes; an example gives just one.
            return Failed(RunStatus.RuntimeError,
                new[] { ex.Message, NotGeneratedNote }, inputs ?? Array.Empty<InputSpec>());
        }

        var requested = sizes ?? DefaultSizes;
        var ladder = RunLadder(target, specs, requested, budget);

        // A ladder that hit the budget or the depth limit almost immediately left too
        // few points to say anything. Retry small — the range should fit the algorithm,
        // not the other way round. This applies to sizes the caller chose as well:
        // two points cannot be fitted, so honouring them would only report Unknown.
        // After a timeout a C# solution is abandoned and cannot be run again.
        // Recursion that overflows at a size this small is not a cost problem — an
        // infinite recursion fails at every size — so retrying would only pay for
        // unwinding a full stack a second time.
        bool overflowedEarly = ladder.StoppedBy == RunStatus.RecursionLimitExceeded &&
                               ladder.StoppedAt is { } stop && stop <= SmallSizes.Max();

        if (ladder.Points.Count < MinimumPointsForCurve && ladder.StoppedBy is RunStatus.BudgetExceeded
                or RunStatus.RecursionLimitExceeded && !overflowedEarly && target.Usable && !requested.SequenceEqual(SmallSizes))
        {
            var retry = RunLadder(target, specs, SmallSizes, budget);
            if (retry.Points.Count > ladder.Points.Count)
            {
                notes.Add((sizes is null ? "The default sizes" : "The sizes requested") +
                          " exhausted the limits almost immediately, so the measurement was retried at much smaller inputs.");
                ladder = retry;
            }
        }

        if (ladder.Note is not null) notes.Add(ladder.Note);

        // When nothing at all was measured, why it stopped is the error, not a footnote:
        // "missing base case" is the one thing the user needs to read.
        var errors = ladder.Errors;
        if (ladder.Points.Count == 0 && ladder.Note is not null) errors = new List<string>(errors) { ladder.Note };

        var empirical = CurveFitter.Fit(ladder.Points);
        var staticEstimate = readTime();
        var derivation = Derivations.Explain(ladder.Runs, empirical);
        var space = Derivations.Space(ladder.Runs, derivation, readSpace());

        bool disagreement =
            empirical.Best != ComplexityClass.Unknown &&
            staticEstimate.Class != ComplexityClass.Unknown &&
            empirical.Best != staticEstimate.Class;

        return new MeasurementResult
        {
            Status = ladder.Points.Count > 0 ? RunStatus.Completed : ladder.StoppedBy ?? RunStatus.RuntimeError,
            Points = ladder.Points,
            Empirical = empirical,
            Static = staticEstimate,
            Disagreement = disagreement,
            DisagreementNote = DescribeDisagreement(disagreement, empirical, staticEstimate),
            Detail = ladder.Detail,
            Derivation = derivation,
            Space = space,
            Inputs = specs,
            Parameters = descriptions,
            Errors = errors,
            SizeNote = notes.Count == 0 ? null : string.Join(" ", notes)
        };
    }

    /// <summary>Applies an arrangement to every input it suits, and says which it did not.</summary>
    public static IReadOnlyList<InputSpec> ApplyStrategy(
        IReadOnlyList<InputSpec> specs, GenerationStrategy? strategy, List<string>? notes = null)
    {
        if (strategy is not { } chosen) return specs;

        bool appliedAnywhere = false;
        var result = specs.Select(spec =>
        {
            if (!ParameterBinder.StrategiesFor(spec.Shape).Contains(chosen)) return spec;
            appliedAnywhere = true;
            return spec with { Strategy = chosen };
        }).ToList();

        if (!appliedAnywhere)
            notes?.Add($"'{chosen}' does not apply to any input of this method, so the default arrangement was used.");

        return result;
    }

    private sealed record Ladder(
        List<SizePoint> Points, List<MeasuredRun> Runs, TraceResult? Detail, string? Note, List<string> Errors,
        RunStatus? StoppedBy, int? StoppedAt = null);

    private static Ladder RunLadder(IRunTarget target, IReadOnlyList<InputSpec> specs, IReadOnlyList<int> sizes, long budget)
    {
        var parameters = target.Parameters;
        var points = new List<SizePoint>();
        var runs = new List<MeasuredRun>();
        var errors = new List<string>();
        TraceResult? detail = null;

        foreach (int size in sizes.Distinct().OrderBy(s => s))
        {
            try
            {
                ParameterBinder.Bind(specs, parameters, size);
            }
            catch (Exception ex)
            {
                errors.Add($"n={size}: could not build inputs — {ex.Message}");
                continue;
            }

            // Inputs are rebuilt for every attempt: a repeated run must start from the same
            // data, and many solutions sort or mark their input in place.
            var trace = target.Run(() => ParameterBinder.Bind(specs, parameters, size), budget);

            if (trace.Status is RunStatus.BudgetExceeded or RunStatus.RecursionLimitExceeded or RunStatus.TimedOut)
            {
                // Larger sizes can only cost more, so there is nothing to learn by going
                // further up the ladder — and after a timeout the solution is abandoned.
                return new Ladder(points, runs, detail,
                    points.Count == 0
                        ? $"Stopped at n={size}: {trace.Errors.FirstOrDefault()}"
                        : $"Stopped at n={size}: {trace.Errors.FirstOrDefault()} Points below this size are still valid.",
                    errors, trace.Status, size);
            }

            if (trace.Status != RunStatus.Completed)
            {
                errors.Add($"n={size}: {string.Join("; ", trace.Errors)}");
                continue;
            }

            points.Add(new SizePoint(size, trace.TotalOperations, trace.TotalBytesAllocated, trace.ElapsedMs)
            {
                Statements = trace.TotalStatements,
                LibraryWork = trace.TotalOperations - trace.TotalStatements,
                MaxRecursionDepth = trace.MaxRecursionDepth
            });
            runs.Add(new MeasuredRun(size, trace));
            detail = trace;   // keep the largest completed run for the per-statement view
        }

        return new Ladder(points, runs, detail, null, errors, null);
    }

    private static string? DescribeDisagreement(bool disagreement, CurveFit empirical, StaticEstimate estimate)
    {
        if (!disagreement)
        {
            if (estimate.Class == ComplexityClass.Unknown && empirical.Best != ComplexityClass.Unknown)
                return "Reading the code alone was not enough to name a class, so the measured curve is the " +
                       "only evidence here.";
            return null;
        }

        return $"The code reads as {ComplexityNames.Format(estimate.Class)} ({estimate.Reasoning}) but it measured " +
               $"as {ComplexityNames.Format(empirical.Best)}. The measurement is what actually happened on this " +
               "input. The usual explanations: an early exit that keeps the worst case from occurring; input data " +
               "that avoids it (try another arrangement); a collection that never grows with n, such as a heap kept " +
               "at size k; or nested loops whose combined iterations are bounded, as in a two-pointer scan.";
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
