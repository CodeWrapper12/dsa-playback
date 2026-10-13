using System.Globalization;
using System.Text.Json;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Python;

namespace DsaViz.Engine.Cpp;

/// <summary>
/// A C++ solution: the functions it offers, their parameters as types the shared input code
/// understands, its examples, and runs of it — measured or recorded — through the student's
/// own compiler.
/// </summary>
public sealed class CppSolution
{
    public static readonly string[] Extensions = { ".cpp", ".cc", ".cxx", ".c++" };

    public static bool IsCpp(string file) => Extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public const int DepthLimit = 20_000;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    public CppFile File { get; }
    public CppFunction? Function { get; }
    public string Entry { get; }

    private CppSolution(CppFile file, string entry)
    {
        File = file;
        Entry = entry;
        Function = Find(file, entry);
    }

    public static CppSolution Read(string source, string entry) => new(CppReader.Read(source), entry);

    /// <summary>"Solution.twoSum" or "twoSum" — the member of that class, or a free function.</summary>
    public static CppFunction? Find(CppFile file, string entry)
    {
        int dot = entry.LastIndexOf('.');
        string name = dot >= 0 ? entry[(dot + 1)..] : entry;
        string? owner = dot >= 0 ? entry[..dot] : null;
        return file.Functions.FirstOrDefault(f => f.Name == name && !f.IsConstructor && (owner is null ? f.Owner is null : f.Owner == owner))
               ?? file.Functions.FirstOrDefault(f => f.Name == name && !f.IsConstructor);
    }

    // ---- what can run --------------------------------------------------------------------------

    /// <summary>The functions the editor can offer, with the reason for any that cannot run.</summary>
    public static IReadOnlyList<EntryPoint> Entries(CppFile file)
    {
        var nodes = NodeClasses(file);
        var classes = file.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var entries = new List<EntryPoint>();
        foreach (var function in file.Functions)
        {
            if (function.IsConstructor || !function.IsPublic) continue;
            if (function.Owner is null && function.Name == "main") continue;
            if (function.Owner is not null && nodes.Contains(function.Owner)) continue;

            var search = Examples(file, function);
            var all = Parameters(function);
            var parameters = function.Parameters.Select((p, index) =>
            {
                var type = CppTypes.Map(p.Type);
                bool given = search.Examples.Any(e => e.Values[index] is not null);
                bool generatable = type is not null && ParameterBinder.ShapeFor(all[index], all) is not null;
                return (Parameter: new EntryParameter(p.Name, p.Type, type is not null && (generatable || given)), Generatable: generatable);
            }).ToList();

            string? reason = null;
            if (function.Owner is { } owner && !function.IsStatic && classes.TryGetValue(owner, out var type) && !type.Constructible)
                reason = $"{owner} needs arguments to be created. Design problems in C++ are not supported yet.";
            else if (parameters.FirstOrDefault(p => !p.Parameter.Supported) is { Parameter: not null } unsupported)
                reason = CppTypes.Map(unsupported.Parameter.Type) is null
                    ? $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which the visualizer can neither generate nor read from an example."
                    : $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which cannot be generated. " +
                      "Give it a value in an example comment, and it is used.";

            entries.Add(new EntryPoint(function.Owner ?? "", function.Name, parameters.Select(p => p.Parameter).ToList())
            {
                Reason = reason,
                Line = function.Line,
                Generatable = parameters.All(p => p.Generatable)
            });
        }

        return entries.OrderByDescending(e => e.Measurable).ThenBy(e => e.Line).ToList();
    }

    /// <summary>Classes shaped like LeetCode's TreeNode or ListNode, whose members are plumbing.</summary>
    private static HashSet<string> NodeClasses(CppFile file)
        => file.Classes
            .Where(c => c.Fields.Any(f => f.Name == "val") &&
                        (c.Fields.Any(f => f.Name == "next") || c.Fields.Any(f => f.Name == "left") && c.Fields.Any(f => f.Name == "right")))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<SolutionParameter> Parameters(CppFunction function)
        => function.Parameters.Select(p => new SolutionParameter(p.Name, CppTypes.Map(p.Type) ?? typeof(object)) { Written = p.Type }).ToList();

    public static ExampleSearch Examples(CppFile file, CppFunction function)
        => ExampleFinder.Combine(
            ExampleFinder.FromCommentLines(file.Comments, function.Parameters.Select(p => p.Name).ToList()),
            Array.Empty<string>());

    public IReadOnlyList<MethodParameters> Methods
        => File.Functions.Select(f => new MethodParameters(f.IsConstructor ? f.Owner ?? f.Name : f.Name, f.Parameters.Select(p => p.Name).ToList()))
            .Concat(Lambdas(File).Select(l => new MethodParameters(l.Name!, l.Parameters.Select(p => p.Name).ToList())))
            .GroupBy(m => m.Name).Select(g => g.First()).ToList();

    private static IEnumerable<CppLambda> Lambdas(CppFile file)
    {
        IEnumerable<CppLambda> Of(CppStatement s) => s.Lambdas.Where(l => l.Name is not null)
            .Concat(s.Lambdas.SelectMany(l => Of(l.Body)))
            .Concat(s.Children.SelectMany(Of))
            .Concat(s.Else is null ? Enumerable.Empty<CppLambda>() : Of(s.Else));
        return file.Functions.SelectMany(f => Of(f.Body));
    }

    /// <summary>The text each argument is passed as: LeetCode's format, never capped.</summary>
    public static IReadOnlyList<string> Inputs(object?[] values) => values.Select(v => Literals.Format(v, int.MaxValue)).ToList();

    // ---- building -------------------------------------------------------------------------------

    /// <summary>
    /// Builds the program for <see cref="Function"/>, or says why it cannot be built. The
    /// student's own errors are found by compiling their code without the instrumentation,
    /// so a message never mentions what the visualizer inserted.
    /// </summary>
    public (CppProgram? Program, string? Executable, RunStatus Status, IReadOnlyList<string> Errors) Build(CppHost host, bool stepping)
    {
        if (File.Problem is { } problem)
            return (null, null, RunStatus.CompileError, new[] { $"{problem}. The visualizer could not read this C++." });
        if (Function is null)
            return (null, null, RunStatus.CompileError, new[] { $"No function named '{Entry}' was found." });

        var program = CppInstrumenter.Build(File, Function, stepping);
        var compiled = host.Compile(program.Source);
        if (compiled.Executable is { } executable) return (program, executable, RunStatus.Completed, Array.Empty<string>());

        var plain = host.Compile(CppInstrumenter.Plain(File, Function));
        if (plain.Executable is null && !plain.InVisualizerCode)
            return (null, null, RunStatus.CompileError, plain.Errors);

        // The code compiles as written, so the failure is in what the visualizer added.
        Console.Error.WriteLine("Instrumented C++ failed to compile:\n" + string.Join("\n", compiled.Errors));
        return (null, null, RunStatus.RuntimeError, new[]
        {
            "The visualizer could not prepare this code to run — it uses C++ the visualizer does not read yet. " +
            "This is a limitation of the visualizer, not a mistake in your code."
        }.Concat(compiled.Errors.Take(3)).ToList());
    }

    // ---- measuring -------------------------------------------------------------------------------

    public IRunTarget Target(string executable, CppProgram program) => new CppTarget(executable, program, Parameters(Function!));

    private sealed class CppTarget : IRunTarget
    {
        private readonly string _executable;
        private readonly CppProgram _program;

        public CppTarget(string executable, CppProgram program, IReadOnlyList<SolutionParameter> parameters)
        {
            _executable = executable;
            _program = program;
            Parameters = parameters;
        }

        public IReadOnlyList<SolutionParameter> Parameters { get; }
        public bool Usable => true;
        public void Dispose() { }

        public TraceResult Run(Func<object?[]> arguments, long budget)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<string> lines;
            int exit;
            try
            {
                lines = CppHost.Run(_executable, stepping: false, Inputs(arguments()), budget, DepthLimit, 0, RunTimeout, out exit);
            }
            catch (CppRunFailedException ex) when (ex.TimedOut)
            {
                return Runner.Failure(RunStatus.TimedOut, new[]
                {
                    $"The run did not finish within {RunTimeout.TotalSeconds:0} seconds. The statement budget cannot stop a " +
                    "single statement that never returns — a sleep, or one enormous library call."
                });
            }
            var end = ReadEnding(lines, exit, _program.Statements);
            var statements = _program.Statements.Select(s => new StatementCost(s.Id, s.Line, s.Column, s.Length, s.Snippet,
                    end.Hits.ElementAtOrDefault(s.Id), end.Bytes.ElementAtOrDefault(s.Id))
                {
                    LibraryWork = end.Work.ElementAtOrDefault(s.Id),
                    LargestAllocation = end.Largest.ElementAtOrDefault(s.Id)
                })
                .OrderBy(s => s.Line).ThenBy(s => s.Column).ToList();

            return new TraceResult
            {
                Status = end.Status,
                Statements = statements,
                Lines = Runner.LinesOf(statements),
                TotalOperations = end.Operations,
                TotalStatements = end.StatementsRun,
                TotalBytesAllocated = end.Allocated,
                MaxRecursionDepth = end.MaxDepth,
                PeakBytes = end.Peak,
                ElapsedMs = started.Elapsed.TotalMilliseconds,
                LibraryCalls = _program.Sites.Select((site, i) => new LibraryCall(site.Site, site.Line, site.Column, site.Member, site.Cost,
                        end.Sites.ElementAtOrDefault(i).Calls, end.Sites.ElementAtOrDefault(i).Work))
                    .Where(c => c.Calls > 0).ToList(),
                Errors = end.Errors,
                ErrorLine = end.ErrorLine,
                ReturnValue = end.Result,
                Language = "cpp"
            };
        }
    }

    /// <summary>What a run's last line said: how it ended and what it counted.</summary>
    public sealed record Ending(RunStatus Status, string? Result, IReadOnlyList<string> Errors, int? ErrorLine)
    {
        public long Operations { get; init; }
        public long StatementsRun { get; init; }
        public long Allocated { get; init; }
        public long Peak { get; init; }
        public int MaxDepth { get; init; }
        public bool Truncated { get; init; }
        public IReadOnlyList<long> Hits { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Bytes { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Largest { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Work { get; init; } = Array.Empty<long>();
        public IReadOnlyList<(long Calls, long Work)> Sites { get; init; } = Array.Empty<(long, long)>();
    }

    /// <summary>
    /// Reads the program's last line. A program that ended without one crashed in a way its
    /// own handler could not report — a stack overflow, or being killed — and the exit code
    /// is what is left to explain it.
    /// </summary>
    public static Ending ReadEnding(IReadOnlyList<string> lines, int exitCode, IReadOnlyList<StatementInfo> statements)
    {
        var last = lines.LastOrDefault(l => l.StartsWith("{\"end\"", StringComparison.Ordinal));
        if (last is null)
        {
            string what = exitCode switch
            {
                -1073741571 or unchecked((int)0xC00000FD) => "Your code crashed: the stack overflowed. Very deep recursion causes this.",
                -1073741819 or unchecked((int)0xC0000005) or 139 or -11 => "Your code crashed with a segmentation fault",
                _ => $"Your code stopped unexpectedly (exit code {exitCode})"
            };
            return new Ending(RunStatus.RuntimeError, null, new[] { what + "." }, null);
        }

        using var document = JsonDocument.Parse(last);
        var root = document.RootElement;
        string status = root.GetProperty("end").GetString() ?? "";
        int statement = root.TryGetProperty("statement", out var at) ? at.GetInt32() : -1;
        int? line = statement >= 0 && statement < statements.Count ? statements[statement].Line : null;
        string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";

        var (runStatus, errors) = status switch
        {
            "Completed" => (RunStatus.Completed, Array.Empty<string>()),
            "BudgetExceeded" => (RunStatus.BudgetExceeded, new[] { error }),
            "RecursionLimitExceeded" => (RunStatus.RecursionLimitExceeded, new[] { error }),
            "Crashed" => (RunStatus.RuntimeError, new[] { Crash(error, line) }),
            "RuntimeError" => (RunStatus.RuntimeError, new[] { line is { } l ? InsertLine(error, l) : error }),
            "InputError" => (RunStatus.RuntimeError, new[] { $"The visualizer could not build the input for your function ({error}). This is a bug in the visualizer." }),
            _ => (RunStatus.RuntimeError, new[] { error })
        };
        static IReadOnlyList<long> Numbers(JsonElement root, string name)
            => root.TryGetProperty(name, out var array) ? array.EnumerateArray().Select(x => x.GetInt64()).ToList() : Array.Empty<long>();

        return new Ending(runStatus,
            root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null,
            errors, status is "Completed" ? null : line)
        {
            Operations = root.GetProperty("ops").GetInt64(),
            StatementsRun = root.GetProperty("statements").GetInt64(),
            Allocated = root.GetProperty("allocated").GetInt64(),
            Peak = root.GetProperty("peak").GetInt64(),
            MaxDepth = root.GetProperty("maxDepth").GetInt32(),
            Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean(),
            Hits = Numbers(root, "hits"),
            Bytes = Numbers(root, "bytes"),
            Largest = Numbers(root, "largest"),
            Work = Numbers(root, "work"),
            Sites = root.TryGetProperty("sites", out var sites)
                ? sites.EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList()
                : Array.Empty<(long, long)>()
        };
    }

    /// <summary>"Your code threw out_of_range: vector" → "Your code threw out_of_range at line 12: vector".</summary>
    private static string InsertLine(string error, int line)
    {
        int colon = error.IndexOf(':');
        return colon < 0 ? $"{error} at line {line}" : $"{error[..colon]} at line {line}{error[colon..]}";
    }

    private static string Crash(string what, int? line)
        => (line is { } l ? $"{what} at line {l}" : what) +
           ". Reading through a null pointer, or past the end of an array or vector, usually causes this.";

    // ---- recording ---------------------------------------------------------------------------------

    /// <summary>
    /// Turns a recorded run's output — a full snapshot per step — into steps as C# and
    /// Python send them: objects by small ids, and each object only when it changed.
    /// </summary>
    public sealed class Recording
    {
        private readonly IReadOnlyList<StatementInfo> _statements;
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, HeapObject> _sent = new();

        public List<ExecutionStep> Steps { get; } = new();
        public List<ReturnEvent> Returns { get; } = new();

        public Recording(IReadOnlyList<StatementInfo> statements) => _statements = statements;

        public void Read(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                if (line.StartsWith("{\"s\"", StringComparison.Ordinal)) Step(line);
                else if (line.StartsWith("{\"ret\"", StringComparison.Ordinal)) Returned(line);
            }
        }

        private int Id(string address)
        {
            if (!_ids.TryGetValue(address, out int id)) { id = _ids.Count + 1; _ids[address] = id; }
            return id;
        }

        private void Step(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            int statementId = root.GetProperty("s").GetInt32();
            var info = statementId < _statements.Count ? _statements[statementId] : new StatementInfo(statementId, 0, 0, 0, "");

            var objects = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var o in root.GetProperty("o").EnumerateArray()) objects[o.GetProperty("id").GetRawText()] = o;

            var variables = root.GetProperty("v").EnumerateArray().Select(v => Variable(v, objects)).ToList();
            var changed = new List<HeapObject>();
            foreach (var (address, element) in objects)
            {
                var current = Describe(Id(address), element);
                if (_sent.TryGetValue(current.Id, out var previous))
                {
                    if (HeapRecorder.Same(previous, current)) continue;
                    changed.Add(HeapRecorder.AsPatch(previous, current) ?? current);
                }
                else changed.Add(current);
                _sent[current.Id] = current;
            }

            Steps.Add(new ExecutionStep(Steps.Count, statementId, info.Line, info.Column, root.GetProperty("d").GetInt32(), info.Method, variables)
            {
                Heap = changed.Count > 0 ? changed : null,
                Allocated = root.GetProperty("a").GetInt64()
            });
        }

        private void Returned(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Returns.Add(new ReturnEvent(root.GetProperty("after").GetInt32(), root.GetProperty("ret").GetInt32(), Value(root.GetProperty("v"))));
        }

        private StepVariable Variable(JsonElement v, Dictionary<string, JsonElement> objects)
        {
            string name = v.GetProperty("n").GetString()!;
            var value = v.GetProperty("v");
            string text = value.GetProperty("x").GetString() ?? "";

            if (value.TryGetProperty("r", out var reference))
            {
                string address = reference.GetRawText();
                string kind = objects.TryGetValue(address, out var o) ? o.GetProperty("k").GetString()! : "object";
                string type = objects.TryGetValue(address, out var described) && described.TryGetProperty("t", out var t) ? t.GetString()! : kind;
                string summary = kind is "tree" or "listnode" ? text : $"{type} {text}";
                return new StepVariable(name, summary, kind, null, null) { Ref = Id(address) };
            }
            if (value.TryGetProperty("num", out var number))
                return new StepVariable(name, text, "scalar", null, number.GetInt64());
            if (text == "null") return new StepVariable(name, "null", "null", null, null);
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                string inner = text[1..^1];
                return new StepVariable(name, text, "string", inner.Length <= 40 ? inner.Select(c => c.ToString()).ToList() : null, null);
            }
            return new StepVariable(name, text, "scalar", null, null);
        }

        private HeapValue Value(JsonElement value)
        {
            string text = value.TryGetProperty("x", out var x) ? x.GetString() ?? "" : "";
            if (value.TryGetProperty("r", out var reference)) return new HeapValue(text) { Ref = Id(reference.GetRawText()) };
            if (value.TryGetProperty("num", out var number)) return new HeapValue(text) { Number = number.GetInt64() };
            return new HeapValue(text);
        }

        private HeapObject Describe(int id, JsonElement o)
        {
            var described = new HeapObject(id, o.GetProperty("k").GetString()!, o.TryGetProperty("t", out var t) ? t.GetString()! : "")
            {
                Count = o.TryGetProperty("c", out var c) ? c.GetInt32() : 0,
                Bytes = o.TryGetProperty("b", out var b) ? b.GetInt64() : 0,
                Arity = o.TryGetProperty("ar", out var ar) ? ar.GetInt32() : null
            };
            if (o.TryGetProperty("i", out var items)) described = described with { Items = items.EnumerateArray().Select(Value).ToList() };
            if (o.TryGetProperty("e", out var entries))
                described = described with { Entries = entries.EnumerateArray().Select(e => new HeapEntry(Value(e[0]), Value(e[1]))).ToList() };
            if (o.TryGetProperty("f", out var fields))
                described = described with { Fields = fields.EnumerateArray().Select(f => new HeapField(f[0].GetString()!, Value(f[1]))).ToList() };
            return described;
        }
    }
}

/// <summary>The C++ types LeetCode passes, as the .NET types the shared input code builds and formats.</summary>
public static class CppTypes
{
    public static Type? Map(string written)
    {
        string type = CppInstrumenter.ValueType(written).Replace("std::", "").Replace(" ", "");
        switch (type)
        {
            case "int": case "short": case "unsigned": case "unsignedint": case "int32_t": return typeof(int);
            case "long": case "longlong": case "longint": case "int64_t": case "size_t": case "unsignedlong": case "unsignedlonglong": return typeof(long);
            case "double": case "float": return typeof(double);
            case "bool": return typeof(bool);
            case "char": return typeof(char);
            case "string": return typeof(string);
            case "TreeNode*": return typeof(PyTreeNode);
            case "ListNode*": return typeof(PyListNode);
        }
        if (type.StartsWith("vector<", StringComparison.Ordinal) && type.EndsWith('>'))
            return Map(type[7..^1])?.MakeArrayType();
        return null;
    }
}
