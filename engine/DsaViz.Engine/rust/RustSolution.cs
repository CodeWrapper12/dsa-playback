using System.Text;
using System.Text.RegularExpressions;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Native;
using DsaViz.Engine.Python;

namespace DsaViz.Engine.Rust;

/// <summary>
/// A Rust solution: the functions it offers, their parameters as types the shared input
/// code understands, its examples, and builds of it — measured or recorded — through the
/// student's own rustc, repaired where the borrow checker will not allow a variable to be read.
/// </summary>
public sealed class RustSolution
{
    public static bool IsRust(string file) => file.EndsWith(".rs", StringComparison.OrdinalIgnoreCase);

    public const int DepthLimit = 20_000;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many times a build is compiled again with more reads left out before giving up.</summary>
    private const int Repairs = 8;

    public RsFile File { get; }
    public RsFunction? Function { get; }
    public string Entry { get; }
    /// <summary>For a design problem, its struct's name; null otherwise.</summary>
    public string? DesignStruct { get; }

    private RustSolution(RsFile file, string entry)
    {
        File = file;
        Entry = entry;
        if (!entry.Contains('.') && DesignStructs(file).Contains(entry)) DesignStruct = entry;
        else Function = Find(file, entry);
    }

    /// <summary>
    /// The structs run as LeetCode design problems: one its example names, or one with an
    /// `fn new` and methods taking self, as LeetCode's Rust template writes them.
    /// </summary>
    public static IReadOnlyList<string> DesignStructs(RsFile file)
    {
        var named = DesignExamples.ClassesNamed(file.Comments);
        return file.Functions.Where(f => f.Owner is { } o && o != "Solution" && o != "?" && !f.IsNested)
            .GroupBy(f => f.Owner!)
            .Where(g => g.Key is not ("TreeNode" or "ListNode") &&
                        (named.Contains(g.Key) || (g.Any(f => f.Name == "new" && !f.HasSelf) && g.Any(f => f.HasSelf))))
            .Select(g => g.Key)
            .ToList();
    }

    public ExampleSearch DesignExamplesFound()
        => new(DesignExamples.FromCommentLines(File.Comments, Entry).ToList(), Array.Empty<string>());

    public static RustSolution Read(string source, string entry) => new(RustReader.Read(source), entry);

    /// <summary>"Solution.two_sum" or "two_sum".</summary>
    public static RsFunction? Find(RsFile file, string entry)
    {
        int dot = entry.LastIndexOf('.');
        string name = dot >= 0 ? entry[(dot + 1)..] : entry;
        string? owner = dot >= 0 ? entry[..dot] : null;
        return file.Functions.FirstOrDefault(f => f.Name == name && !f.IsNested && f.Owner == owner)
               ?? file.Functions.FirstOrDefault(f => f.Name == name && !f.IsNested);
    }

    // ---- what can run --------------------------------------------------------------------------

    public static IReadOnlyList<EntryPoint> Entries(RsFile file)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal) { "TreeNode", "ListNode" };
        var entries = new List<EntryPoint>();
        var named = DesignExamples.ClassesNamed(file.Comments);
        var designs = DesignStructs(file);
        foreach (var type in designs)
        {
            bool example = named.Contains(type);
            int line = file.Structs.FirstOrDefault(s => s.Name == type)?.Line ?? file.Functions.First(f => f.Owner == type).Line;
            entries.Add(new EntryPoint("", type, new[]
            {
                new EntryParameter(DesignExamples.Parameters[0], "list of operations", example),
                new EntryParameter(DesignExamples.Parameters[1], "list of arguments", example)
            })
            {
                Design = true,
                Generatable = false,
                Line = line,
                Reason = example ? null : EntryPointScanner.DesignNeedsExample(type)
            });
        }

        foreach (var function in file.Functions)
        {
            if (function.IsNested || (function.Owner is null && function.Name == "main")) continue;
            // A design struct's methods are run through its example's calls, not one by one.
            if (function.Owner is { } designOwner && designs.Contains(designOwner)) continue;
            if (function.Owner is { } owner && (nodes.Contains(owner) || owner == "?")) continue;
            // In impl Solution, LeetCode's entry is pub; private ones are its helpers.
            if (function.Owner == "Solution" && !function.IsPublic) continue;
            if (function.Owner is not null && function.Owner != "Solution" && !function.HasSelf && !function.IsPublic) continue;

            var search = Examples(file, function);
            var all = Parameters(function);
            var parameters = function.Parameters.Select((p, index) =>
            {
                var type = RustTypes.Map(p.Type);
                bool given = search.Examples.Any(e => e.Values[index] is not null);
                bool generatable = type is not null && ParameterBinder.ShapeFor(all[index], all) is not null;
                return (Parameter: new EntryParameter(p.Name, p.Type, type is not null && (generatable || given)), Generatable: generatable);
            }).ToList();

            string? reason = null;
            if (function.HasSelf || (function.Owner is not null && function.Owner != "Solution"))
                reason = $"{function.Owner}::{function.Name} belongs to an object that is created and then called. Give " +
                         $"{function.Owner} an fn new, and LeetCode's example of the calls, and it runs as a design problem.";
            else if (function.IsGeneric)
                reason = $"{function.Name} is generic, so there is no single type to build its inputs as.";
            else if (parameters.FirstOrDefault(p => !p.Parameter.Supported) is { Parameter: not null } unsupported)
                reason = RustTypes.Map(unsupported.Parameter.Type) is null
                    ? $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which the visualizer can neither generate nor read from an example."
                    : $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which cannot be generated. " +
                      "Give it a value in an example comment, and it is used.";

            entries.Add(new EntryPoint(function.Owner ?? "", function.Name, parameters.Select(p => p.Parameter).ToList())
            {
                Reason = reason,
                Line = function.Line,
                Generatable = reason is null && parameters.All(p => p.Generatable)
            });
        }
        return entries.OrderByDescending(e => e.Measurable).ThenBy(e => e.Line).ToList();
    }

    public static IReadOnlyList<SolutionParameter> Parameters(RsFunction function)
        => function.Parameters.Select(p => new SolutionParameter(p.Name, RustTypes.Map(p.Type) ?? typeof(object)) { Written = p.Type }).ToList();

    public static ExampleSearch Examples(RsFile file, RsFunction function)
        => ExampleFinder.Combine(ExampleFinder.FromCommentLines(file.Comments, function.Parameters.Select(p => p.Name).ToList()),
            Array.Empty<string>());

    public IReadOnlyList<MethodParameters> Methods
        => File.Functions.Select(f => new MethodParameters(f.Name, f.Parameters.Select(p => p.Name).ToList()))
            .Concat(Closures(File).Select(c => new MethodParameters(c.Name!, c.Parameters.ToList())))
            .GroupBy(m => m.Name).Select(g => g.First()).ToList();

    private static IEnumerable<RsClosure> Closures(RsFile file)
    {
        IEnumerable<RsClosure> Of(RsStatement s) => s.Closures.Where(c => c.Name is not null)
            .Concat(s.Closures.Where(c => c.Body is not null).SelectMany(c => Of(c.Body!)))
            .Concat(s.Children.SelectMany(Of))
            .Concat(s.Arms.Where(a => a.Block is not null).SelectMany(a => Of(a.Block!)))
            .Concat(s.Else is null ? Enumerable.Empty<RsClosure>() : Of(s.Else));
        return file.Functions.SelectMany(f => Of(f.Body));
    }

    // ---- building -------------------------------------------------------------------------------

    /// <summary>
    /// Builds the program for <see cref="Function"/>. Reads the compiler rejects are left out
    /// and the build compiled again; the student's own errors are found by compiling their
    /// code with nothing inserted, so a message never mentions what the visualizer added.
    /// </summary>
    public (RustPlan? Plan, string? Executable, RunStatus Status, IReadOnlyList<string> Errors) Build(RustHost host, bool stepping)
    {
        if (File.Problem is { } problem)
            return (null, null, RunStatus.CompileError, new[] { $"{problem}. The visualizer could not read this Rust." });
        if (Function is null && DesignStruct is null)
            return (null, null, RunStatus.CompileError, new[] { $"No function named '{Entry}' was found." });

        var plan = RustInstrumenter.Build(File, Function, stepping, design: DesignStruct);
        string key = RustHost.Hash(plan.Render(new Dictionary<int, string>()).Source);
        var left = host.Remembered(key) ?? new Dictionary<int, string>();
        bool everything = false;
        var last = Array.Empty<RustDiagnostic>() as IReadOnlyList<RustDiagnostic>;

        for (int round = 0; round <= Repairs; round++)
        {
            var (source, placed) = plan.Render(left);
            var compiled = host.Compile(source, overflowChecks: stepping);
            if (compiled.Executable is { } executable)
            {
                if (left.Count > 0) host.Remember(key, left);
                return (plan, executable, RunStatus.Completed, Array.Empty<string>());
            }
            last = compiled.Errors;

            // Each error inside something inserted leaves that thing out: a variable read is
            // listed with the reason instead, a return value or a charge is not recorded.
            var offsets = CharOffsets(source);
            var blamed = new Dictionary<int, string>();
            bool elsewhere = false;
            foreach (var error in compiled.Errors)
            {
                int? GroupAt(int bytes)
                {
                    int offset = offsets(bytes);
                    return placed.Where(p => p.Value.Any(r => offset >= r.Start && offset < r.End)).Select(p => (int?)p.Key).FirstOrDefault();
                }
                var group = error.ByteStart is { } b ? GroupAt(b) : null;
                // A read of a reference at a later step keeps its borrow alive, and the student's
                // own code then collides with it: the error is on their line, the cause is the read,
                // named only as where the borrow is "later used".
                var uses = error.Others.Where(o => o.Label.Contains("later used")).Select(o => GroupAt(o.ByteStart)).ToList();
                var later = uses.Where(g => g is { } x && !left.ContainsKey(x)).Select(g => g!.Value).ToList();
                // When only inserted reads use the borrow later, they are the cause: without them the other
                // read is allowed. When the student's own code goes on to use it, the borrow is really
                // alive. The compiler names only the nearest later use, so the code is asked which.
                if (later.Count > 0 && uses.All(g => g is not null) && !later.Any(plan.UsedLater))
                    foreach (var l in later) blamed[l] = "no longer used";
                else if (group is { } g && !left.ContainsKey(g)) blamed[g] = Reason(error.Code);
                else
                {
                    elsewhere = true;
                    Console.Error.WriteLine($"Rust build error not caused by one inserted read (round {round}): " +
                                            $"{error.Line}:{error.Column} {error.Code} {error.Message}");
                }
            }

            if (!elsewhere && blamed.Count > 0)
            {
                foreach (var (g, why) in blamed)
                {
                    left[g] = why;
                    // Moved, or not yet set, it stays so until the code sets it again.
                    if (why is "moved" or "not set yet" or "no longer used")
                        foreach (var later in plan.Following(g)) left.TryAdd(later, why);
                }
                continue;
            }
            if (everything) break;
            // Something not attributable to one insertion: try once with every optional one left out.
            everything = true;
            foreach (var g in plan.Groups.Keys) left.TryAdd(g, "not readable here");
        }

        var plain = host.Compile(RustInstrumenter.Plain(File, Function, DesignStruct));
        if (plain.Executable is null)
        {
            var own = plain.Errors.Where(e => e.Line >= 1 && e.Line <= File.LineCount)
                .Select(e => $"line {e.Line}, column {e.Column}: {e.Message}{(e.Code is { } c ? $" [{c}]" : "")}")
                .Distinct().Take(10).ToList();
            if (own.Count > 0) return (null, null, RunStatus.CompileError, own);
        }

        // The code compiles as written, so the failure is in what the visualizer added.
        Console.Error.WriteLine("Instrumented Rust failed to compile:\n" + string.Join("\n", last.Select(e => $"{e.Line}:{e.Column} {e.Code} {e.Message}")));
        return (null, null, RunStatus.RuntimeError, new[]
        {
            "The visualizer could not prepare this code to run — it uses Rust the visualizer does not read yet. " +
            "This is a limitation of the visualizer, not a mistake in your code."
        }.Concat(last.Take(3).Select(e => e.Message)).ToList());
    }

    /// <summary>Why a read was left out, from the compiler's error code, in the words the run view shows.</summary>
    private static string Reason(string? code) => code switch
    {
        "E0382" or "E0505" or "E0507" => "moved",
        "E0381" => "not set yet",
        "E0499" or "E0502" or "E0503" or "E0506" or "E0596" => "borrowed",
        _ => "not readable here"
    };

    /// <summary>rustc counts bytes of UTF-8; the program text is counted in characters.</summary>
    private static Func<int, int> CharOffsets(string source)
    {
        var starts = new List<int>(source.Length + 1);
        for (int i = 0; i < source.Length; i++)
        {
            int bytes = char.IsHighSurrogate(source[i]) ? 4 : char.IsLowSurrogate(source[i]) ? 0 : Encoding.UTF8.GetByteCount(source[i].ToString());
            for (int b = 0; b < bytes; b++) starts.Add(i);
        }
        starts.Add(source.Length);
        return b => b >= 0 && b < starts.Count ? starts[b] : source.Length;
    }

    public IRunTarget Target(string executable, RustPlan plan)
        => new NativeTarget(executable, plan.Statements, plan.Sites, Parameters(Function!), "rust", DepthLimit, RunTimeout);
}

/// <summary>The Rust types LeetCode passes, as the .NET types the shared input code builds and formats.</summary>
public static class RustTypes
{
    public static Type? Map(string written)
    {
        string type = Regex.Replace(written, @"'\w+", "").Replace(" ", "").Replace("std::", "").Replace("rc::", "").Replace("cell::", "");
        while (type.StartsWith('&')) type = type.StartsWith("&mut") ? type[4..] : type[1..];
        switch (type)
        {
            case "i8": case "i16": case "i32": case "u8": case "u16": case "u32": return typeof(int);
            case "i64": case "u64": case "isize": case "usize": case "i128": return typeof(long);
            case "f64": case "f32": return typeof(double);
            case "bool": return typeof(bool);
            case "char": return typeof(char);
            case "String": case "str": return typeof(string);
            case "Option<Rc<RefCell<TreeNode>>>": return typeof(PyTreeNode);
            case "Option<Box<ListNode>>": return typeof(PyListNode);
        }
        if (type.StartsWith("Vec<", StringComparison.Ordinal) && type.EndsWith('>'))
            return Map(type[4..^1])?.MakeArrayType();
        if (type.StartsWith('[') && type.EndsWith(']') && !type.Contains(';'))
            return Map(type[1..^1])?.MakeArrayType();
        return null;
    }
}

/// <summary>
/// A reading of a Rust function's time complexity from its shape, as for C++: loop nesting,
/// halving loops, recursion — with memoisation and recursion on a node's children told apart.
/// </summary>
public static class RustEstimator
{
    public static readonly StaticEstimate Space = new(ComplexityClass.Unknown,
        "Reading Rust code for space is not supported; the measured figures are the answer.", 0, 0, 0);

    private static readonly HashSet<string> Scans = new(StringComparer.Ordinal)
        { "contains", "iter", "into_iter", "to_vec", "join", "concat" };

    public static StaticEstimate Time(RsFile file, RsFunction function)
    {
        var own = Own(file, function);
        // A helper declared inside the function — fn dfs(..) — is where the work often is.
        var helpers = file.Functions
            .Where(f => f.IsNested && f.Body.First > function.Body.First && f.Body.Last < function.Body.Last)
            .Select(f => Own(file, f))
            .ToList();
        var deepest = helpers.Where(h => Growth.Rank(h.Class) > Growth.Rank(own.Class)).MaxBy(h => Growth.Rank(h.Class));
        return deepest is null ? own : deepest with { Reasoning = $"In a helper it calls: {deepest.Reasoning}" };
    }

    private static StaticEstimate Own(RsFile file, RsFunction function)
    {
        var (linear, log) = Depth(file, function.Body);
        string body = file.Text[file.Tokens[function.Body.First].Start..file.Tokens[function.Body.Last].End];
        string name = Regex.Escape(function.Name);
        var calls = Regex.Matches(body, $@"(?<![\w.])(?:Self::|{Regex.Escape(function.Owner ?? "")}::)?{name}\s*\(([^()]*(\([^()]*(\([^()]*\))*[^()]*\))*[^()]*)\)")
            .Where(m => function.Owner is null || m.Value.Contains("::")).ToList();
        int selfCalls = calls.Count;

        if (selfCalls >= 2 && calls.All(c => Regex.IsMatch(c.Groups[1].Value, @"\b(left|right|next)\b")))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself on a node's children, so each node is visited once.", linear, log, selfCalls);
        if (selfCalls >= 2 && Regex.IsMatch(body, @"\b(memo|cache|dp|seen)\w*\s*\.(get|contains_key|insert)\b|\b(memo|cache|dp)\w*\s*\["))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself {selfCalls} times, but stores each answer and returns it when asked again, " +
                "so each value is worked out once.", linear, log, selfCalls);
        if (selfCalls >= 2)
            return new StaticEstimate(ComplexityClass.Exponential,
                $"{function.Name} calls itself {selfCalls} times in its body; without memoisation that branches at every call.",
                linear, log, selfCalls);
        if (selfCalls == 1)
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself once per call{(linear > 0 ? ", with a loop in each call" : "")}.", linear, log, selfCalls);

        var cls = (linear, log) switch
        {
            (0, 0) => ComplexityClass.Constant,
            (0, _) => ComplexityClass.Logarithmic,
            (1, 0) => ComplexityClass.Linear,
            (1, _) => ComplexityClass.Linearithmic,
            (2, _) => ComplexityClass.Quadratic,
            _ => ComplexityClass.Cubic
        };
        string reasoning = linear == 0 && log == 0
            ? "No loops or recursion."
            : $"Loops nest {linear} deep over the input{(log > 0 ? $", and {log} loop{(log == 1 ? "" : "s")} halve what is left" : "")}.";
        return new StaticEstimate(cls, reasoning, linear, log, 0);
    }

    private static (int Linear, int Log) Depth(RsFile file, RsStatement statement)
    {
        var inner = statement.Children
            .Concat(statement.Else is null ? Array.Empty<RsStatement>() : new[] { statement.Else })
            .Concat(statement.Arms.Where(a => a.Block is not null).Select(a => a.Block!))
            .Select(c => Depth(file, c))
            .DefaultIfEmpty((0, 0))
            .MaxBy(d => d.Item1 * 10 + d.Item2);
        var (linear, log) = inner;
        if (statement.Kind is RsStatementKind.Let or RsStatementKind.Expression or RsStatementKind.If or RsStatementKind.While &&
            ScansInLibrary(file, statement))
            linear = Math.Max(linear, 1);
        if (statement.Kind is not (RsStatementKind.For or RsStatementKind.While or RsStatementKind.Loop)) return (linear, log);
        string text = file.Text[file.Tokens[statement.First].Start..file.Tokens[statement.Last].End];
        bool halves = Regex.IsMatch(text, @"(/=\s*2\b|\*=\s*2\b|>>=\s*1\b|<<=\s*1\b|\bmid\b)");
        return halves ? (linear, log + 1) : (linear + 1, log);
    }

    private static bool ScansInLibrary(RsFile file, RsStatement statement)
    {
        int last = statement.HeaderEnd >= 0 ? statement.HeaderEnd : statement.Last;
        for (int i = statement.First; i < last; i++)
            if (file.Tokens[i].Kind == RsTokenKind.Identifier && i > 0 && file.Tokens[i - 1].Text == "." &&
                file.Tokens[i + 1].Text == "(" && Scans.Contains(file.Tokens[i].Text))
                return true;
        return false;
    }
}
