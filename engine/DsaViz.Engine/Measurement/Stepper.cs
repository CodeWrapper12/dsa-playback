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
        long budget = Runner.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
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

            var names = parameters.Select((p, i) => p.Name ?? $"arg{i}").ToList();
            var search = ExampleFinder.Find(analysis.Tree.GetRoot(), entryMethod, names);
            int? selected = example switch
            {
                < 0 => null,
                { } chosen when chosen < search.Examples.Count => chosen,
                // An example that no longer exists — the file was edited — falls back to the first.
                _ => search.Examples.Count > 0 ? 0 : null
            };
            var chosenExample = selected is { } index ? search.Examples[index] : null;

            var (arguments, resolved) = ResolveInputs(parameters, specs, size, values, chosenExample);
            var found = new StepTrace
            {
                Status = RunStatus.Completed,
                Size = size,
                Steps = Array.Empty<ExecutionStep>(),
                Statements = Array.Empty<StatementInfo>(),
                Inputs = resolved,
                Examples = search.Examples,
                ExampleNotes = search.Notes,
                SelectedExample = selected
            };

            if (resolved.Any(input => input.Error is not null))
                return found with
                {
                    Status = RunStatus.InvalidInput,
                    Errors = resolved.Where(i => i.Error is not null).Select(i => $"{i.Name}: {i.Error}").ToList()
                };

            // Captured before the run, because many solutions change their inputs in place
            // and the point of showing the arguments is to show where it started.
            var shownArguments = parameters.Select((p, i) => Variable(p.Name ?? $"arg{i}", ValueCapture.Capture(arguments[i]))).ToList();

            var trace = Runner.Invoke(solution, arguments, budget, recordSteps: maxSteps);
            var statements = solution.Statements;

            var steps = trace.RecordedSteps.Select((step, stepIndex) =>
            {
                var info = statements[step.StatementId];
                var variables = info.Variables.Select((name, i) => Variable(name, step.Values[i])).ToList();
                return new ExecutionStep(stepIndex, info.Id, info.Line, info.Column, step.Depth, info.Method, variables);
            }).ToList();

            string? returned = trace.Status == RunStatus.Completed ? Literals.Format(trace.ReturnObject) : null;

            return found with
            {
                Status = trace.Status,
                Steps = steps,
                Statements = statements,
                Truncated = trace.StepsTruncated,
                MaxSteps = maxSteps,
                Arguments = shownArguments,
                ReturnValue = returned,
                Errors = trace.Errors,
                ErrorLine = trace.ErrorLine,
                Check = Check(chosenExample, resolved, returned)
            };
        }
        finally
        {
            if (!solution.Abandoned) solution.Dispose();
        }
    }

    /// <summary>
    /// Each parameter's value, from the first source that has one: what the student typed,
    /// then the example found in the code, then a generated value at the chosen size.
    /// </summary>
    private static (object?[] Arguments, IReadOnlyList<StepInput> Inputs) ResolveInputs(
        System.Reflection.ParameterInfo[] parameters, IReadOnlyList<InputSpec> specs, int size,
        IReadOnlyList<string?>? typed, FoundExample? example)
    {
        var arguments = new object?[parameters.Length];
        var inputs = new List<StepInput>(parameters.Length);

        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            string name = parameter.Name ?? $"arg{i}";
            string type = ParameterBinder.FriendlyName(parameter.ParameterType);

            string? text = typed is not null && i < typed.Count ? typed[i] : null;
            var source = InputSource.Typed;
            if (text is null && example?.Values[i] is { } fromCode)
            {
                text = fromCode;
                source = InputSource.Example;
            }

            if (text is null)
            {
                arguments[i] = InputGenerator.Generate(specs[i], size, parameter.ParameterType);
                inputs.Add(new StepInput(name, type, Literals.Format(arguments[i]), InputSource.Generated)
                {
                    Note = example?.Notes.FirstOrDefault(n => n.Parameter == name)?.Message
                });
                continue;
            }

            try
            {
                arguments[i] = Literals.ToObject(text, parameter.ParameterType);
                inputs.Add(new StepInput(name, type, text, source));
            }
            catch (LiteralException ex)
            {
                inputs.Add(new StepInput(name, type, text, source) { Error = ex.Message });
            }
        }

        return (arguments, inputs);
    }

    /// <summary>
    /// The result against the example's "Output:". Only when every input came from that
    /// example: once a value is typed or generated, the expected output is about different
    /// inputs and comparing with it would be wrong.
    /// </summary>
    private static OutputCheck? Check(FoundExample? example, IReadOnlyList<StepInput> inputs, string? returned)
    {
        if (example?.Expected is not { } expected || returned is null) return null;
        if (inputs.Any(i => i.Source != InputSource.Example)) return null;

        try
        {
            var (want, got) = (Literals.Parse(expected), Literals.Parse(returned));
            bool matches = example.AnyOrder ? Literals.SameValueAnyOrder(want, got) : Literals.SameValue(want, got);
            return new OutputCheck(expected, returned, matches) { AnyOrder = example.AnyOrder };
        }
        catch (LiteralException)
        {
            // An Output written in prose ("true, because…") cannot be compared, so no
            // verdict is given rather than a wrong one.
            return null;
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
