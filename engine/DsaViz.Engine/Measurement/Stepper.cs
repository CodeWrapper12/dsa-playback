using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;

namespace DsaViz.Engine.Measurement;

/// <summary>
/// Records one run at a small size, statement by statement, for stepping through.
///
/// Uses its own build. Capturing every variable at every statement allocates and costs
/// time, and if that happened in the measurement build it would show up in the measured
/// numbers. Kept separate, it cannot.
/// </summary>
public static class Stepper
{
    public const int DefaultSize = 8;
    public const int DefaultMaxSteps = 2_000;

    public static StepTrace Record(
        SourceAnalysis analysis,
        string entryMethod,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Runner.DefaultBudget)
    {
        var outcome = Runner.Compile(analysis, entryMethod, instrument: true, stepping: true);
        if (!outcome.Success) return Failed(outcome.Status, outcome.Errors, size);

        var solution = outcome.Solution!;
        try
        {
            var parameters = solution.Parameters;
            IReadOnlyList<InputSpec> specs;
            try
            {
                specs = inputs ?? Measurer.ApplyStrategy(ParameterBinder.Infer(parameters), strategy);
                ParameterBinder.Validate(parameters, specs);
            }
            catch (UnsupportedSignatureException ex)
            {
                return Failed(RunStatus.RuntimeError, new[] { ex.Message }, size);
            }

            var arguments = ParameterBinder.Bind(specs, parameters, size);

            // Captured before the run, because many solutions change their inputs in place
            // and the point of showing the arguments is to show where it started.
            var shownArguments = parameters.Select((p, i) => Variable(p.Name ?? $"arg{i}", ValueCapture.Capture(arguments[i]))).ToList();

            var trace = Runner.Invoke(solution, arguments, budget, recordSteps: maxSteps);
            var statements = solution.Statements;

            var steps = trace.RecordedSteps.Select((step, index) =>
            {
                var info = statements[step.StatementId];
                var variables = info.Variables.Select((name, i) => Variable(name, step.Values[i])).ToList();
                return new ExecutionStep(index, info.Id, info.Line, info.Column, step.Depth, info.Method, variables);
            }).ToList();

            return new StepTrace
            {
                Status = trace.Status,
                Size = size,
                Steps = steps,
                Statements = statements,
                Truncated = trace.StepsTruncated,
                MaxSteps = maxSteps,
                Arguments = shownArguments,
                ReturnValue = trace.ReturnValue,
                Errors = trace.Errors
            };
        }
        finally
        {
            if (!solution.Abandoned) solution.Dispose();
        }
    }

    private static StepVariable Variable(string name, DsaViz.Runtime.CapturedValue value)
        => new(name, value.Display, value.Kind, value.Items, value.Number);

    private static StepTrace Failed(RunStatus status, IReadOnlyList<string> errors, int size) => new()
    {
        Status = status,
        Size = size,
        Steps = Array.Empty<ExecutionStep>(),
        Statements = Array.Empty<StatementInfo>(),
        Errors = errors
    };
}
