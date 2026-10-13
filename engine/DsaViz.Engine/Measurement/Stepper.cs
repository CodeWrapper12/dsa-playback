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
        var outcome = Runner.Compile(analysis, entryMethod, instrument: true, stepping: true, design: IsDesign(analysis, entryMethod));
        if (!outcome.Success) return Failed(outcome.Status, outcome.Errors, size);

        var solution = outcome.Solution!;
        try
        {
            var plan = CSharpPlan(analysis, entryMethod, solution);
            return RecordWith(plan, inputs, size, maxSteps, strategy, values, example, MethodsOf(analysis),
                CSharpRun(solution, budget, maxSteps));
        }
        finally
        {
            if (!solution.Abandoned) solution.Dispose();
        }
    }

    /// <summary>
    /// Runs every example in a C# file against one build. Each run records a single step:
    /// only its result is wanted, and recording the rest would cost time for nothing.
    /// </summary>
    public static ExampleReport RunExamples(SourceAnalysis analysis, string entryMethod, long budget = Runner.DefaultBudget)
    {
        var outcome = Runner.Compile(analysis, entryMethod, instrument: true, stepping: true, design: IsDesign(analysis, entryMethod));
        if (!outcome.Success) return new ExampleReport(outcome.Status, Array.Empty<ExampleRun>()) { Errors = outcome.Errors };

        var solution = outcome.Solution!;
        try
        {
            var plan = CSharpPlan(analysis, entryMethod, solution);
            var methods = MethodsOf(analysis);
            var run = CSharpRun(solution, budget, ResultOnlySteps);

            // A run that timed out leaves this build unusable, so later examples are
            // reported as not run rather than as failures of the student's code.
            return RunEach(plan.Search, index => solution.Abandoned
                ? null
                : RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index, methods, run));
        }
        finally
        {
            if (!solution.Abandoned) solution.Dispose();
        }
    }

    /// <summary>
    /// What a run takes and where its values come from. A design problem's two values —
    /// the operations and their arguments — are read together, since which types the
    /// arguments are depends on the methods the operations name.
    /// </summary>
    private sealed record Plan(IReadOnlyList<SolutionParameter> Parameters, ExampleSearch Search)
    {
        /// <summary>Reads a value's text; null reads it as its parameter's type.</summary>
        public Func<int, string, object?[], object?>? Read { get; init; }

        /// <summary>Why nothing can be generated, when that is so regardless of the types.</summary>
        public string? NotGenerated { get; init; }
    }

    /// <summary>The design problem's class when <paramref name="entry"/> names one, not a method.</summary>
    public static bool IsDesign(SourceAnalysis analysis, string entry)
        => !entry.Contains('.') && analysis.Tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Any(c => c.Identifier.Text == entry);

    private static readonly IReadOnlyList<SolutionParameter> DesignParameters = new[]
    {
        new SolutionParameter(DesignExamples.Parameters[0], typeof(string[])) { Written = "list of operations" },
        new SolutionParameter(DesignExamples.Parameters[1], typeof(object[][])) { Written = "list of arguments" }
    };

    private const string DesignNotGenerated =
        "A design problem's calls cannot be made up: add LeetCode's example as a comment, or type the operations and their arguments.";

    private static Plan CSharpPlan(SourceAnalysis analysis, string entry, CompiledSolution solution)
    {
        if (solution.DesignType is { } type)
        {
            return new Plan(DesignParameters, DesignExamples.Find(analysis.Tree.GetRoot(), type.Name))
            {
                NotGenerated = DesignNotGenerated,
                Read = (index, text, read) => index == 0
                    ? Literals.ToObject(text, typeof(string[]))
                    : DesignArguments(type, read[0] as string[], text)
            };
        }

        var parameters = SolutionParameter.From(solution.Parameters);
        return new Plan(parameters, ExampleFinder.Find(analysis.Tree.GetRoot(), entry, parameters.Select(p => p.Name).ToList()));
    }

    /// <summary>The argument lists as a design problem's calls need them: each one built as its method's parameter types.</summary>
    internal static object?[][]? DesignArguments(Type type, string[]? operations, string text)
    {
        var lists = ArgumentLists(text);
        // The operations could not be read, and that is already said under their own box.
        if (operations is null) return null;

        System.Reflection.MethodBase[] members;
        try
        {
            members = DsaViz.Runtime.DesignCalls.Resolve(type, operations, lists.Select(l => l.Count).ToList());
        }
        catch (DsaViz.Runtime.DesignInputException ex)
        {
            throw new LiteralException(ex.Message);
        }

        return members.Select((member, i) => member.GetParameters()
            .Select((parameter, j) => Literals.ToObject(lists[i][j], parameter.ParameterType))
            .ToArray()).ToArray();
    }

    /// <summary>A list of lists, one per call, as LeetCode writes a design problem's arguments.</summary>
    private static IReadOnlyList<IReadOnlyList<LeetValue>> ArgumentLists(string text)
    {
        const string Shape = "a list with one list of arguments per operation, like [[2],[1,1],[1]]";
        return Literals.Parse(text) is LeetArray outer
            ? outer.Items.Select(item => item is LeetArray inner ? inner.Items : throw new LiteralException($"expected {Shape}")).ToList()
            : throw new LiteralException($"expected {Shape}");
    }

    /// <summary>A run of the compiled C# solution, recording at most <paramref name="maxSteps"/> steps.</summary>
    private static Func<object?[], RecordedRun> CSharpRun(CompiledSolution solution, long budget, int maxSteps)
        => values =>
        {
            // A design run's entry point makes the calls, and takes the class to make them on.
            var recorder = new HeapRecorder(solution.Statements);
            object?[] arguments = solution.DesignType is { } type
                ? new object?[] { type, values[0], values[1], new Action<int>(recorder.CallStarting) }
                : values;
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
                trace.Status == RunStatus.Completed ? Literals.Format(trace.ReturnObject) : null)
            {
                CallStarts = recorder.CallStarts
            };
        };

    /// <summary>
    /// Steps kept per run when only the result matters. Recording stops there; the run
    /// itself goes on to the end.
    /// </summary>
    private const int ResultOnlySteps = 1;

    /// <summary>
    /// Each example's result, in file order. <paramref name="record"/> returns null for an
    /// example that could not be run at all.
    /// </summary>
    private static ExampleReport RunEach(ExampleSearch search, Func<int, StepTrace?> record)
    {
        var runs = search.Examples.Select((example, index) =>
        {
            var row = new ExampleRun(index, example.Label, example.Line, RunStatus.Completed)
            {
                Expected = example.Expected,
                AnyOrder = example.AnyOrder
            };

            if (record(index) is not { } trace)
                return row with { Status = RunStatus.TimedOut, Error = "Not run: an earlier example ran out of time." };

            bool finished = trace.Status == RunStatus.Completed;
            return row with
            {
                Status = trace.Status,
                Actual = trace.ReturnValue,
                Matches = trace.Check?.Matches,
                Error = finished ? null : string.Join(" ", trace.Errors),
                ErrorLine = trace.ErrorLine
            };
        }).ToList();

        return new ExampleReport(RunStatus.Completed, runs);
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
        if (solution.EntryInfo is null && solution.DesignClass is null)
            return Failed(RunStatus.CompileError, new[] { NoFunction(entry) }, size);

        var plan = PythonPlan(solution);
        return RecordWith(plan, inputs, size, maxSteps, strategy, values, example, solution.Description.Methods,
            PythonRun(host, solution, source, entry, plan.Parameters, budget, maxSteps));
    }

    /// <summary>Runs every example in a Python file, each recording a single step.</summary>
    public static ExampleReport RunExamplesPython(Python.PythonHost host, string source, string entry,
        long budget = Python.PythonSolution.DefaultBudget)
    {
        var solution = Python.PythonSolution.Describe(host, source, entry);
        if (solution.CompileError is { } error)
            return new ExampleReport(RunStatus.CompileError, Array.Empty<ExampleRun>()) { Errors = new[] { error } };
        if (solution.EntryInfo is null && solution.DesignClass is null)
            return new ExampleReport(RunStatus.CompileError, Array.Empty<ExampleRun>()) { Errors = new[] { NoFunction(entry) } };

        var plan = PythonPlan(solution);
        var run = PythonRun(host, solution, source, entry, plan.Parameters, budget, ResultOnlySteps);

        // Each Python run is its own process, so a timeout leaves nothing behind to skip.
        return RunEach(plan.Search, index => RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index,
            solution.Description.Methods, run));
    }

    private static string NoFunction(string entry) => $"No function named '{entry}' was found.";

    /// <summary>A C++ function's recorded run, built with the student's own compiler.</summary>
    public static StepTrace RecordCpp(
        Cpp.CppHost host,
        string source,
        string entry,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Runner.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
    {
        var solution = Cpp.CppSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Executable is null) return Failed(built.Status, built.Errors, size);

        var plan = CppPlanFor(solution);
        return RecordWith(plan, inputs, size, maxSteps, strategy, values, example, solution.Methods,
            CppRun(built.Executable, built.Program!, budget, maxSteps));
    }

    /// <summary>Runs every example in a C++ file against one build, each recording a single step.</summary>
    public static ExampleReport RunExamplesCpp(Cpp.CppHost host, string source, string entry, long budget = Runner.DefaultBudget)
    {
        var solution = Cpp.CppSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Executable is null) return new ExampleReport(built.Status, Array.Empty<ExampleRun>()) { Errors = built.Errors };

        var plan = CppPlanFor(solution);
        var run = CppRun(built.Executable, built.Program!, budget, ResultOnlySteps);
        return RunEach(plan.Search, index => RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index, solution.Methods, run));
    }

    /// <summary>A Rust function's recorded run, built with the student's own rustc.</summary>
    public static StepTrace RecordRust(
        Rust.RustHost host,
        string source,
        string entry,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Runner.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
    {
        var solution = Rust.RustSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Executable is null) return Failed(built.Status, built.Errors, size);

        var plan = solution.DesignStruct is not null
            ? DesignPlan(solution.DesignExamplesFound())
            : new Plan(Rust.RustSolution.Parameters(solution.Function!), Rust.RustSolution.Examples(solution.File, solution.Function!));
        return RecordWith(plan, inputs, size, maxSteps, strategy, values, example, solution.Methods,
            NativeRecordedRun(built.Executable, built.Plan!.Statements, solution.File.LineCount, Rust.RustSolution.DepthLimit, Rust.RustSolution.RunTimeout, budget, maxSteps));
    }

    /// <summary>Runs every example in a Rust file against one build, each recording a single step.</summary>
    public static ExampleReport RunExamplesRust(Rust.RustHost host, string source, string entry, long budget = Runner.DefaultBudget)
    {
        var solution = Rust.RustSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Executable is null) return new ExampleReport(built.Status, Array.Empty<ExampleRun>()) { Errors = built.Errors };

        var plan = solution.DesignStruct is not null
            ? DesignPlan(solution.DesignExamplesFound())
            : new Plan(Rust.RustSolution.Parameters(solution.Function!), Rust.RustSolution.Examples(solution.File, solution.Function!));
        var run = NativeRecordedRun(built.Executable, built.Plan!.Statements, solution.File.LineCount, Rust.RustSolution.DepthLimit, Rust.RustSolution.RunTimeout, budget, ResultOnlySteps);
        return RunEach(plan.Search, index => RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index, solution.Methods, run));
    }

    /// <summary>A Java method's, or design problem's, recorded run, built with the student's own JDK.</summary>
    public static StepTrace RecordJava(
        Java.JavaHost host,
        string source,
        string entry,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Runner.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
    {
        var solution = Java.JavaSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Classes is null) return Failed(built.Status, built.Errors, size);
        return RecordWith(JavaPlanFor(solution), inputs, size, maxSteps, strategy, values, example, solution.Methods,
            JavaRun(host, built.Classes, built.Plan!, solution.File.LineCount, budget, maxSteps));
    }

    /// <summary>Runs every example in a Java file against one build, each recording a single step.</summary>
    public static ExampleReport RunExamplesJava(Java.JavaHost host, string source, string entry, long budget = Runner.DefaultBudget)
    {
        var solution = Java.JavaSolution.Read(source, entry);
        var built = solution.Build(host, stepping: true);
        if (built.Classes is null) return new ExampleReport(built.Status, Array.Empty<ExampleRun>()) { Errors = built.Errors };
        var plan = JavaPlanFor(solution);
        var run = JavaRun(host, built.Classes, built.Plan!, solution.File.LineCount, budget, ResultOnlySteps);
        return RunEach(plan.Search, index => RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index, solution.Methods, run));
    }

    private static Plan JavaPlanFor(Java.JavaSolution solution)
    {
        if (solution.DesignClass is not null)
        {
            // Java checks the calls against the class itself, by reflection: the arguments go as written.
            return new Plan(DesignParameters, solution.DesignExamplesFound())
            {
                NotGenerated = DesignNotGenerated,
                Read = (index, text, read) => index == 0
                    ? Literals.ToObject(text, typeof(string[]))
                    : PlainArguments(read[0] as string[], text)
            };
        }
        return new Plan(Java.JavaSolution.Parameters(solution.Method!), Java.JavaSolution.Examples(solution.File, solution.Method!));
    }

    private static Func<object?[], RecordedRun> JavaRun(Java.JavaHost host, string classes, Java.JavaPlan plan, int sourceLines,
        long budget, int maxSteps)
        => NativeRecordedRun(host.Java, plan.Statements, sourceLines, Java.JavaSolution.DepthLimit, Java.JavaSolution.RunTimeout,
            budget, maxSteps, host.Launcher(classes));

    /// <summary>A JavaScript or TypeScript function's, or design problem's, recorded run, with the student's own Node.</summary>
    public static StepTrace RecordJs(
        Js.JsHost host,
        string source,
        string entry,
        bool typescript,
        IReadOnlyList<InputSpec>? inputs = null,
        int size = DefaultSize,
        int maxSteps = DefaultMaxSteps,
        GenerationStrategy? strategy = null,
        long budget = Runner.DefaultBudget,
        IReadOnlyList<string?>? values = null,
        int? example = null)
    {
        var solution = Js.JsSolution.Read(host, source, entry, typescript);
        var built = solution.Build(stepping: true);
        if (built.Program is null) return Failed(built.Status, built.Errors, size);
        return RecordWith(JsPlanFor(solution), inputs, size, maxSteps, strategy, values, example, solution.Methods,
            JsRun(host, built.Program, budget, maxSteps));
    }

    /// <summary>Runs every example in a JavaScript or TypeScript file against one build, each recording a single step.</summary>
    public static ExampleReport RunExamplesJs(Js.JsHost host, string source, string entry, bool typescript, long budget = Runner.DefaultBudget)
    {
        var solution = Js.JsSolution.Read(host, source, entry, typescript);
        var built = solution.Build(stepping: true);
        if (built.Program is null) return new ExampleReport(built.Status, Array.Empty<ExampleRun>()) { Errors = built.Errors };
        var plan = JsPlanFor(solution);
        var run = JsRun(host, built.Program, budget, ResultOnlySteps);
        return RunEach(plan.Search, index => RecordWith(plan, null, DefaultSize, ResultOnlySteps, null, null, index, solution.Methods, run));
    }

    private static Plan JsPlanFor(Js.JsSolution solution)
    {
        if (solution.DesignClass is not null) return DesignPlan(solution.DesignExamplesFound());
        var search = solution.Examples();
        return new Plan(Js.JsSolution.Parameters(solution.Function!, search), search);
    }

    private static Func<object?[], RecordedRun> JsRun(Js.JsHost host, Js.JsProgram program, long budget, int maxSteps)
        => NativeRecordedRun(host.Node, program.Statements, int.MaxValue, Js.JsSolution.DepthLimit, Js.JsSolution.RunTimeout,
            budget, maxSteps, Js.JsHost.Launcher(program.Launcher));

    private static Plan CppPlanFor(Cpp.CppSolution solution)
        => solution.DesignClass is not null
            ? DesignPlan(solution.DesignExamplesFound())
            : new Plan(Cpp.CppSolution.Parameters(solution.Function!), Cpp.CppSolution.Examples(solution.File, solution.Function!));

    /// <summary>
    /// A design problem in a language whose own program checks the calls against the class —
    /// Python, Java, C++, Rust: the operations as text, the arguments as written.
    /// </summary>
    private static Plan DesignPlan(ExampleSearch search)
        => new(DesignParameters, search)
        {
            NotGenerated = DesignNotGenerated,
            Read = (index, text, read) => index == 0
                ? Literals.ToObject(text, typeof(string[]))
                : PlainArguments(read[0] as string[], text)
        };

    private static Func<object?[], RecordedRun> CppRun(string executable, Cpp.CppProgram program, long budget, int maxSteps)
        => NativeRecordedRun(executable, program.Statements, int.MaxValue, Cpp.CppSolution.DepthLimit, Cpp.CppSolution.RunTimeout,
            budget, maxSteps);

    /// <summary>A recorded run of a compiled program, C++ or Rust, keeping at most <paramref name="maxSteps"/> steps.</summary>
    private static Func<object?[], RecordedRun> NativeRecordedRun(string executable, IReadOnlyList<StatementInfo> statements,
        int sourceLines, int depthLimit, TimeSpan timeout, long budget, int maxSteps, IReadOnlyList<string>? launcher = null)
        => arguments =>
        {
            IReadOnlyList<string> lines;
            int exit;
            try
            {
                lines = Native.NativeRun.Run(executable, stepping: true, Native.NativeRun.Inputs(arguments), budget,
                    depthLimit, maxSteps, timeout, out exit, launcher);
            }
            catch (Native.NativeRunFailedException ex) when (ex.TimedOut)
            {
                return new RecordedRun(RunStatus.TimedOut, new List<ExecutionStep>(), new List<ReturnEvent>(),
                    statements, false, 0, new[] { ex.Message }, null, null);
            }

            var recording = new Native.NativeRun.Recording(statements);
            recording.Read(lines);
            var end = Native.NativeRun.ReadEnding(lines, exit, statements, sourceLines);
            return new RecordedRun(end.Status, recording.Steps, recording.Returns, statements, end.Truncated,
                end.Allocated, end.Errors, end.ErrorLine, end.Result)
            {
                CallStarts = recording.CallStarts
            };
        };

    private static Plan PythonPlan(Python.PythonSolution solution)
    {
        if (solution.DesignClass is not null)
        {
            // Python takes the arguments as they are written; its runner checks them against
            // the methods, which only it can see.
            return new Plan(DesignParameters, solution.DesignExamplesFound())
            {
                NotGenerated = DesignNotGenerated,
                Read = (index, text, read) => index == 0
                    ? Literals.ToObject(text, typeof(string[]))
                    : PlainArguments(read[0] as string[], text)
            };
        }

        var search = solution.Examples();
        return new Plan(solution.Parameters(search), search);
    }

    private static object?[][]? PlainArguments(string[]? operations, string text)
    {
        var lists = ArgumentLists(text);
        if (operations is null) return null;
        if (lists.Count != operations.Length)
            throw new LiteralException(
                $"there are {operations.Length} operations but {lists.Count} argument lists. Each operation needs its own list, [] when it takes none");
        return lists.Select(list => list.Select(Literals.ToPlain).ToArray()).ToArray();
    }

    /// <summary>A run of the Python function, recording at most <paramref name="maxSteps"/> steps.</summary>
    private static Func<object?[], RecordedRun> PythonRun(Python.PythonHost host, Python.PythonSolution solution,
        string source, string entry, IReadOnlyList<SolutionParameter> parameters, long budget, int maxSteps)
        => arguments =>
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
                    heapLike = solution.Description.HeapLike,
                    design = solution.DesignClass is not null
                }, Python.PythonSolution.RunTimeout);
            }
            catch (Python.PythonRunFailedException ex) when (ex.TimedOut)
            {
                return new RecordedRun(RunStatus.TimedOut, new List<ExecutionStep>(), new List<ReturnEvent>(),
                    Array.Empty<StatementInfo>(), false, 0, new[] { ex.Message }, null, null);
            }

            return new RecordedRun(run.Status, run.Steps, run.Returns, run.Statements, run.Truncated, run.TotalAllocated,
                run.Errors, run.ErrorLine, run.ReturnValue)
            {
                CallStarts = run.Calls.Select(c => (c.Index, c.Step)).ToList()
            };
        };

    /// <summary>What one recorded run produced, whichever language ran it.</summary>
    private sealed record RecordedRun(
        RunStatus Status, IReadOnlyList<ExecutionStep> Steps, IReadOnlyList<ReturnEvent> Returns,
        IReadOnlyList<StatementInfo> Statements, bool Truncated, long TotalAllocated,
        IReadOnlyList<string> Errors, int? ErrorLine, string? ReturnValue)
    {
        /// <summary>For a design problem: each call's number and the step it started at.</summary>
        public IReadOnlyList<(int Index, int Step)> CallStarts { get; init; } = Array.Empty<(int, int)>();
    }

    /// <summary>
    /// Everything about a recorded run that does not depend on the language: which values it
    /// runs with, reading typed ones, the expected output, and the arguments as passed in.
    /// </summary>
    private static StepTrace RecordWith(
        Plan plan,
        IReadOnlyList<InputSpec>? inputs,
        int size,
        int maxSteps,
        GenerationStrategy? strategy,
        IReadOnlyList<string?>? values,
        int? example,
        IReadOnlyList<MethodParameters> methods,
        Func<object?[], RecordedRun> run)
    {
        var (parameters, search) = (plan.Parameters, plan.Search);
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
        string? generationProblem = plan.NotGenerated;
        if (specs is null && generationProblem is null)
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

        var (arguments, resolved) = ResolveInputs(parameters, specs, size, values, chosenExample, generationProblem, plan.Read);
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
            Check = Check(chosenExample, resolved, recorded.ReturnValue),
            Calls = arguments[0] is string[] operations && plan.NotGenerated == DesignNotGenerated
                ? recorded.CallStarts.Where(c => c.Index < operations.Length)
                    .Select(c => new DesignCall(c.Index, operations[c.Index], c.Step)).ToList()
                : Array.Empty<DesignCall>()
        };
    }

    /// <summary>
    /// Each parameter's value, from the first source that has one: what the student typed,
    /// then the example found in the code, then a generated value at the chosen size.
    /// </summary>
    private static (object?[] Arguments, IReadOnlyList<StepInput> Inputs) ResolveInputs(
        IReadOnlyList<SolutionParameter> parameters, IReadOnlyList<InputSpec>? specs, int size,
        IReadOnlyList<string?>? typed, FoundExample? example, string? generationProblem,
        Func<int, string, object?[], object?>? read)
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
                arguments[i] = read is null ? Literals.ToObject(text, parameter.Type) : read(i, text, arguments);
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
                Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax c => (Name: c.Identifier.Text, List: c.ParameterList),
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
