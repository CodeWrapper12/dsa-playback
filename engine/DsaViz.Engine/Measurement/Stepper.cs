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
    public const int DefaultMaxSteps = 10_000;

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
            var parameters = SolutionParameter.From(solution.Parameters);
            var search = ExampleFinder.Find(analysis.Tree.GetRoot(), entryMethod, parameters.Select(p => p.Name).ToList());

            return RecordWith(parameters, search, inputs, size, maxSteps, strategy, values, example, MethodsOf(analysis), arguments =>
            {
                var recorder = new HeapRecorder(solution.Statements);
                var trace = Runner.Invoke(solution, arguments, budget, recording: new StepRecording(recorder, maxSteps));

                var errors = trace.Errors.ToList();
                if (trace.RecorderFailure is not null)
                {
                    // The run is unaffected; only the recording stopped. Said plainly, with the
                    // details left for the log rather than shown as if the code had failed.
                    Console.Error.WriteLine($"Step recorder failed: {trace.RecorderFailure}");
                    errors.Add($"The recording stopped after {recorder.Steps.Count} steps because of a problem in the visualizer. " +
                               "Your code itself ran to the end.");
                }

                return new RecordedRun(trace.Status, recorder.Steps, recorder.Returns, solution.Statements, trace.StepsTruncated,
                    recorder.Steps.Count > 0 ? trace.TotalBytesAllocated : 0, errors, trace.ErrorLine,
                    trace.Status == RunStatus.Completed ? Literals.Format(trace.ReturnObject) : null);
            });
        }
        finally
        {
            if (!solution.Abandoned) solution.Dispose();
        }
    }

    /// <summary>A Python function's recorded run, through the same inputs, examples and checks as C#.</summary>
    public static StepTrace RecordPython(
        Python.PythonHost host,
        string source,
        string entry,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Python.PythonSolution.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
    {
        var solution = Python.PythonSolution.Describe(host, source, entry);
        if (solution.CompileError is { } error) return Failed(RunStatus.CompileError, new[] { error }, size);
        if (solution.EntryInfo is null) return Failed(RunStatus.CompileError, new[] { $"No function named '{entry}' was found." }, size);

        var search = solution.Examples();
        var parameters = solution.Parameters(search);

        return RecordWith(parameters, search, inputs, size, maxSteps, strategy, values, example, solution.Description.Methods, arguments =>
        {
            Python.PythonSteps run;
            try
            {
                run = host.Call<Python.PythonSteps>(new
                {
                    mode = "step",
                    source,
                    entry,
                    args = Python.PythonSolution.Arguments(arguments, parameters),
                    maxSteps,
                    budget = Math.Min(budget, Python.PythonSolution.DefaultBudget),
                    depthLimit = Python.PythonSolution.DepthLimit,
                    stackLike = solution.Description.StackLike,
                    heapLike = solution.Description.HeapLike
                }, Python.PythonSolution.RunTimeout);
            }
            catch (Python.PythonRunFailedException ex) when (ex.TimedOut)
            {
                return new RecordedRun(RunStatus.TimedOut, new List<ExecutionStep>(), new List<ReturnEvent>(),
                    Array.Empty<StatementInfo>(), false, 0, new[] { ex.Message }, null, null);
            }

            return new RecordedRun(run.Status, run.Steps, run.Returns, run.Statements, run.Truncated, run.TotalAllocated,
                run.Errors, run.ErrorLine, run.ReturnValue);
        });
    }

    /// <summary>What one recorded run produced, whichever language ran it.</summary>
    private sealed record RecordedRun(
        RunStatus Status, IReadOnlyList<ExecutionStep> Steps, IReadOnlyList<ReturnEvent> Returns,
        IReadOnlyList<StatementInfo> Statements, bool Truncated, long TotalAllocated,
        IReadOnlyList<string> Errors, int? ErrorLine, string? ReturnValue);

    /// <summary>
    /// Everything about a recorded run that does not depend on the language: which values it
    /// runs with, reading typed ones, the expected output, and the arguments as passed in.
    /// </summary>
    private static StepTrace RecordWith(
        IReadOnlyList<SolutionParameter> parameters,
        ExampleSearch search,
        IReadOnlyList<InputSpec>? inputs,
        int size,
        int maxSteps,
        GenerationStrategy? strategy,
        IReadOnlyList<string?>? values,
        int? example,
        IReadOnlyList<MethodParameters> methods,
        Func<object?[], RecordedRun> run)
    {
        int? selected = example switch
        {
            < 0 => null,
            { } chosen when chosen < search.Examples.Count => chosen,
            // An example that no longer exists — the file was edited — falls back to the first.
            _ => search.Examples.Count > 0 ? 0 : null
        };
        var chosenExample = selected is { } index ? search.Examples[index] : null;

        // Generated values need a configuration; a parameter that cannot be generated is
        // fine as long as every value it needs is typed or found in the code.
        IReadOnlyList<InputSpec>? specs = inputs;
        string? generationProblem = null;
        if (specs is null)
        {
            try
            {
                specs = Measurer.ApplyStrategy(ParameterBinder.Infer(parameters), strategy);
                ParameterBinder.Validate(parameters, specs);
            }
            catch (UnsupportedSignatureException ex)
            {
                generationProblem = ex.Message;
                specs = null;
            }
        }

        var (arguments, resolved) = ResolveInputs(parameters, specs, size, values, chosenExample, generationProblem);
        var found = new StepTrace
        {
            Status = RunStatus.Completed,
            Size = size,
            Steps = Array.Empty<ExecutionStep>(),
            Statements = Array.Empty<StatementInfo>(),
            Inputs = resolved,
            Examples = search.Examples,
            ExampleNotes = search.Notes,
            SelectedExample = selected,
            MaxSteps = maxSteps
        };

        if (resolved.Any(input => input.Error is not null))
            return found with
            {
                Status = RunStatus.InvalidInput,
                Errors = resolved.Where(i => i.Error is not null).Select(i => $"{i.Name}: {i.Error}").ToList()
            };

        // Captured before the run, because many solutions change their inputs in place
        // and the point of showing the arguments is to show where it started.
        var shownArguments = parameters.Select((p, i) => Variable(p.Name, ValueCapture.Capture(arguments[i]))).ToList();

        var recorded = run(arguments);
        return found with
        {
            Status = recorded.Status,
            Steps = recorded.Steps,
            Returns = recorded.Returns,
            TotalAllocated = recorded.TotalAllocated,
            Methods = methods,
            Graph = GraphOf(parameters),
            Statements = recorded.Statements,
            Truncated = recorded.Truncated,
            Arguments = shownArguments,
            ReturnValue = recorded.ReturnValue,
            Errors = recorded.Errors,
            ErrorLine = recorded.ErrorLine,
            Check = Check(chosenExample, resolved, recorded.ReturnValue)
        };
    }

    /// <summary>
    /// Each parameter's value, from the first source that has one: what the student typed,
    /// then the example found in the code, then a generated value at the chosen size.
    /// </summary>
    private static (object?[] Arguments, IReadOnlyList<StepInput> Inputs) ResolveInputs(
        IReadOnlyList<SolutionParameter> parameters, IReadOnlyList<InputSpec>? specs, int size,
        IReadOnlyList<string?>? typed, FoundExample? example, string? generationProblem)
    {
        var arguments = new object?[parameters.Count];
        var inputs = new List<StepInput>(parameters.Count);

        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            string name = parameter.Name;
            string type = parameter.Written ?? ParameterBinder.FriendlyName(parameter.Type);

            string? text = typed is not null && i < typed.Count ? typed[i] : null;
            var source = InputSource.Typed;
            if (text is null && example?.Values[i] is { } fromCode)
            {
                text = fromCode;
                source = InputSource.Example;
            }

            if (text is null && specs is null)
            {
                // Nothing typed, nothing in the code, and nothing that can be generated.
                inputs.Add(new StepInput(name, type, "", InputSource.Generated)
                {
                    Error = $"Type a value for '{name}': it cannot be generated. {generationProblem}"
                });
                continue;
            }

            if (text is null)
            {
                arguments[i] = InputGenerator.Generate(specs![i], size, parameter.Type);
                inputs.Add(new StepInput(name, type, Literals.Format(arguments[i]), InputSource.Generated)
                {
                    Note = example?.Notes.FirstOrDefault(n => n.Parameter == name)?.Message
                });
                continue;
            }

            try
            {
                arguments[i] = Literals.ToObject(text, parameter.Type);
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

    /// <summary>Every method and local function with its parameter names, for showing a call's arguments.</summary>
    private static IReadOnlyList<Contracts.MethodParameters> MethodsOf(SourceAnalysis analysis)
        => analysis.Tree.GetRoot().DescendantNodes()
            .Select(node => node switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax m => (Name: m.Identifier.Text, List: m.ParameterList),
                Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax f => (Name: f.Identifier.Text, List: f.ParameterList),
                _ => (Name: (string?)null, List: (Microsoft.CodeAnalysis.CSharp.Syntax.ParameterListSyntax?)null)
            })
            .Where(m => m.Name is not null)
            .GroupBy(m => m.Name!)
            .Select(g => new Contracts.MethodParameters(g.Key, g.First().List!.Parameters.Select(p => p.Identifier.Text).ToList()))
            .ToList();

    /// <summary>The input that is a graph, when the signature has one: drawn as nodes and edges.</summary>
    private static GraphInput? GraphOf(IReadOnlyList<SolutionParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var shape = ParameterBinder.ShapeFor(parameter, parameters);
            if (shape == InputShape.NestedIntList) return new GraphInput("adjacency", parameter.Name);
            if (shape == InputShape.EdgeList)
                return new GraphInput("edges", parameter.Name) { NodeCount = ParameterBinder.NodeCountParameter(parameters)?.Name };
        }
        return null;
    }

    private static StepVariable Variable(string name, CapturedValue value)
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
