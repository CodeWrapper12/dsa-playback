using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;

namespace DsaViz.Engine.Python;

/// <summary>
/// A Python file as the engine sees it: its functions, their parameters as types the
/// shared input code understands, the examples written in it, and a reading of the code.
/// </summary>
public sealed class PythonSolution
{
    /// <summary>
    /// Traced Python runs about 220,000 operations a second here, so a million stops a
    /// runaway loop within about five seconds — roughly what C#'s fifty million takes.
    /// An O(n²) solution still reaches n = 500 before it.
    /// </summary>
    public const long DefaultBudget = 1_000_000;

    /// <summary>
    /// Deep enough for every solution in the measured range — a skewed tree at n = 1,000
    /// recurses 1,001 deep — and three times Python's own default of 1,000. Not C#'s 20,000:
    /// with tracemalloc running, CPython's recursion costs grow with the square of the
    /// depth (measured: 0.4 s at 2,000 deep, 5.6 s at 8,000), so a runaway recursion must be
    /// stopped early to be stopped quickly — at 3,000 it takes one to two seconds.
    /// </summary>
    public const int DepthLimit = 3_000;

    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    public PythonHost Host { get; }
    public string Source { get; }
    public string Entry { get; }
    public PythonDescription Description { get; }

    private PythonSolution(PythonHost host, string source, string entry, PythonDescription description)
    {
        Host = host;
        Source = source;
        Entry = entry;
        Description = description;
    }

    public static PythonSolution Describe(PythonHost host, string source, string entry)
    {
        var description = host.Call<PythonDescription>(new { mode = "describe", source, entry }, DescribeTimeout);
        return new PythonSolution(host, source, entry, description);
    }

    public string? CompileError => Description.CompileError;

    public PythonEntry? EntryInfo
    {
        get
        {
            string name = Entry.Contains('.') ? Entry[(Entry.LastIndexOf('.') + 1)..] : Entry;
            string owner = Entry.Contains('.') ? Entry[..Entry.LastIndexOf('.')] : "";
            return Description.Entries.FirstOrDefault(e => e.Name == name && (owner.Length == 0 || e.DeclaringType == owner));
        }
    }

    /// <summary>The design problem's class when the entry names one rather than a function.</summary>
    public PythonClass? DesignClass => Entry.Contains('.') ? null : Description.Classes.FirstOrDefault(c => c.Name == Entry);

    private IReadOnlyList<(string Text, int Line)> CommentLines => Description.Comments.Select(c => (c.Text, c.Line)).ToList();

    /// <summary>The design examples that create <see cref="DesignClass"/>.</summary>
    public ExampleSearch DesignExamplesFound()
        => new(DsaViz.Engine.Analysis.DesignExamples.FromCommentLines(CommentLines, Entry).ToList(), Array.Empty<string>());

    /// <summary>The examples in the file for this entry: comments read by the shared reader, calls found by Python.</summary>
    public ExampleSearch Examples()
    {
        var entry = EntryInfo;
        if (entry is null) return new ExampleSearch(Array.Empty<FoundExample>(), Array.Empty<string>());
        return ExamplesFor(Description, entry);
    }

    public static ExampleSearch ExamplesFor(PythonDescription description, PythonEntry entry)
    {
        var names = entry.Parameters.Select(p => p.Name).ToList();
        var comments = ExampleFinder.FromCommentLines(description.Comments.Select(c => (c.Text, c.Line)).ToList(), names);

        var notes = new List<string>();
        var calls = new List<FoundExample>();
        foreach (var call in description.Calls.GetValueOrDefault(entry.Name) ?? new List<PythonCall>())
        {
            var values = names.Select(n => call.Values.GetValueOrDefault(n)).ToList();
            if (values.All(v => v is null))
            {
                notes.Add($"The call on line {call.Line} was not used: none of its arguments are values written in the code.");
                continue;
            }
            calls.Add(new FoundExample($"Call on line {call.Line}", call.Line, ExampleOrigin.Call, values, call.Notes));
        }
        return ExampleFinder.Combine(comments.Concat(calls), notes);
    }

    /// <summary>
    /// The parameters as types the shared input code understands. A parameter without a
    /// hint takes its type from an example's value, when there is one.
    /// </summary>
    public IReadOnlyList<SolutionParameter> Parameters(ExampleSearch examples)
        => ParametersOf(EntryInfo ?? throw new InvalidOperationException($"No function '{Entry}' was found."), examples);

    public static IReadOnlyList<SolutionParameter> ParametersOf(PythonEntry entry, ExampleSearch examples)
    {
        return entry.Parameters.Select((p, i) =>
        {
            Type? type = p.Type.Length > 0 ? PythonTypes.Map(p.Type, p.Name) : null;
            if (type is null && p.Type.Length == 0)
            {
                type = examples.Examples.Select(e => e.Values[i]).Where(v => v is not null)
                    .Select(v => PythonTypes.FromExample(v!)).FirstOrDefault(t => t is not null);
            }
            return new SolutionParameter(p.Name, type ?? typeof(object))
            {
                Written = p.Type.Length > 0 ? p.Type : type is null ? "no type hint" : "(from the example)"
            };
        }).ToList();
    }

    /// <summary>The functions the editor can offer, with the reason for any that cannot run.</summary>
    public static IReadOnlyList<EntryPoint> Entries(PythonDescription description)
    {
        // A design problem's class is offered once, run through its example's calls, and
        // its methods are not offered on their own.
        var named = DesignExamples.ClassesNamed(description.Comments.Select(c => (c.Text, c.Line)).ToList());
        var designs = description.Classes
            .Where(c => named.Contains(c.Name) || (!c.Constructible && c.Methods.Count > 0))
            .ToList();
        var designNames = designs.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var designEntries = designs.Select(c => new EntryPoint("", c.Name, new[]
        {
            new EntryParameter(DesignExamples.Parameters[0], "list of operations", named.Contains(c.Name)),
            new EntryParameter(DesignExamples.Parameters[1], "list of arguments", named.Contains(c.Name))
        })
        {
            Design = true,
            // Measured per call by repeating the example's calls, so it needs one.
            Generatable = named.Contains(c.Name),
            Line = c.Line,
            Reason = named.Contains(c.Name) ? null : EntryPointScanner.DesignNeedsExample(c.Name)
        });

        return description.Entries.Where(entry => !designNames.Contains(entry.DeclaringType)).Select(entry =>
        {
            var examples = ExamplesFor(description, entry);
            var parameters = ParametersOf(entry, examples);
            string? reason = entry.Reason is { } own && !own.StartsWith("Parameter '") ? own : null;

            var generatable = parameters.Select(p => ParameterBinder.ShapeFor(p, parameters) is not null).ToList();
            var listed = entry.Parameters.Select((p, i) =>
            {
                bool given = examples.Examples.Any(e => e.Values[i] is not null);
                return new EntryParameter(p.Name, p.Type.Length > 0 ? p.Type : "(no hint)", given || generatable[i]);
            }).ToList();

            if (reason is null && listed.FirstOrDefault(p => !p.Supported) is { } unsupported)
            {
                reason = unsupported.Type == "(no hint)"
                    ? $"Parameter '{unsupported.Name}' has no type hint and no example gives it a value. " +
                      $"Add a hint the way LeetCode does, like '{unsupported.Name}: List[int]'."
                    : $"Parameter '{unsupported.Name}' is '{unsupported.Type}', which cannot be generated. " +
                      "Give it a value in an example comment or a call, and it is used.";
            }

            return new EntryPoint(entry.DeclaringType, entry.Name, listed)
            {
                Reason = reason,
                Line = entry.Line,
                Generatable = generatable.All(g => g)
            };
        })
        .Concat(designEntries)
        .OrderByDescending(e => e.Measurable)
        .ThenBy(e => e.Line)
        .ToList();
    }

    /// <summary>A run target for measuring this function at a ladder of sizes.</summary>
    public IRunTarget Target(IReadOnlyList<SolutionParameter> parameters) => new PythonTarget(this, parameters);

    /// <summary>One measured run of the entry with these arguments, as Python receives them.</summary>
    internal TraceResult RunMeasured(object[] args, long budget, bool design, out PythonRun? run)
    {
        run = null;
        try
        {
            run = Host.Call<PythonRun>(new
            {
                mode = "run",
                source = Source,
                entry = Entry,
                args,
                budget = Math.Min(budget, DefaultBudget),
                depthLimit = DepthLimit,
                design
            }, RunTimeout);
        }
        catch (PythonRunFailedException ex) when (ex.TimedOut)
        {
            return Runner.Failure(RunStatus.TimedOut, new[]
            {
                $"The run did not finish within {RunTimeout.TotalSeconds:0} seconds. The statement budget cannot stop a " +
                "single statement that never returns — a sleep, a blocking call, or one enormous library call."
            });
        }

        var statements = run.Statements.OrderBy(s => s.Line).ThenBy(s => s.Column).ToList();
        return new TraceResult
        {
            Status = run.Status,
            Statements = statements,
            Lines = Runner.LinesOf(statements),
            TotalOperations = run.TotalOperations,
            TotalStatements = run.TotalStatements,
            TotalBytesAllocated = run.TotalBytesAllocated,
            MaxRecursionDepth = run.MaxRecursionDepth,
            PeakBytes = run.PeakBytes,
            ElapsedMs = run.ElapsedMs,
            LibraryCalls = run.LibraryCalls,
            Errors = run.Errors,
            ErrorLine = run.ErrorLine,
            ReturnValue = run.ReturnValue,
            Output = run.Output,
            Language = "python",
            CallCosts = run.CallCosts
        };
    }

    /// <summary>A run target for measuring this design problem's calls at a ladder of sizes.</summary>
    public PythonDesignTarget DesignTarget() => new(this);

    /// <summary>A design problem run from its two lists of text, with what each call cost.</summary>
    public sealed class PythonDesignTarget : IRunTarget
    {
        private readonly PythonSolution _solution;
        private IReadOnlyList<string> _methods = Array.Empty<string>();

        internal PythonDesignTarget(PythonSolution solution) => _solution = solution;

        public IReadOnlyList<SolutionParameter> Parameters { get; } = Array.Empty<SolutionParameter>();
        public bool Usable => true;
        public void Dispose() { }

        /// <summary>The method a statement is in, as the last run reported it.</summary>
        public string? MethodOf(int statement) => statement >= 0 && statement < _methods.Count ? _methods[statement] : null;

        public TraceResult Run(Func<object?[]> arguments, long budget)
        {
            var lists = arguments();
            var trace = _solution.RunMeasured(new object[]
            {
                new { text = (string)lists[0]!, kind = "value" },
                new { text = (string)lists[1]!, kind = "value" }
            }, budget, design: true, out var run);
            if (run is not null) _methods = run.StatementMethods;
            return trace;
        }
    }

    /// <summary>The values as Python receives them: LeetCode-format text, and how to rebuild nodes.</summary>
    public static object[] Arguments(object?[] values, IReadOnlyList<SolutionParameter> parameters)
        => values.Select((v, i) => (object)new
        {
            // Whole, never capped for display: a 100×100 grid is 10,100 elements.
            text = Literals.Format(v, int.MaxValue),
            kind = PythonTypes.KindOf(parameters[i].Type)
        }).ToArray();

    private sealed class PythonTarget : IRunTarget
    {
        private readonly PythonSolution _solution;

        public PythonTarget(PythonSolution solution, IReadOnlyList<SolutionParameter> parameters)
        {
            _solution = solution;
            Parameters = parameters;
        }

        public IReadOnlyList<SolutionParameter> Parameters { get; }

        public bool Usable => true;

        public TraceResult Run(Func<object?[]> arguments, long budget)
            => _solution.RunMeasured(Arguments(arguments(), Parameters), budget, design: false, out _);

        public void Dispose() { }
    }
}
