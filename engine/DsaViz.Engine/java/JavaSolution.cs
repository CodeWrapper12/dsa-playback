using System.Text.RegularExpressions;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Native;
using DsaViz.Engine.Python;

namespace DsaViz.Engine.Java;

/// <summary>
/// A Java solution: the methods and design classes it offers, their parameters as types the
/// shared input code understands, its examples, and builds of it through the student's JDK,
/// repaired where javac will not let a variable be read.
/// </summary>
public sealed class JavaSolution
{
    public static bool IsJava(string file) => file.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    public const int DepthLimit = 20_000;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    private const int Repairs = 6;

    public JvFile File { get; }
    public string Entry { get; }
    public JvMethod? Method { get; }
    /// <summary>For a design problem, its class; null otherwise.</summary>
    public JvClass? DesignClass { get; }

    private JavaSolution(JvFile file, string entry)
    {
        File = file;
        Entry = entry;
        if (!entry.Contains('.') && file.Classes.FirstOrDefault(c => c.Name == entry && c.TopLevel) is { } type) DesignClass = type;
        else Method = Find(file, entry);
    }

    public static JavaSolution Read(string source, string entry) => new(JavaReader.Read(source), entry);

    public static JvMethod? Find(JvFile file, string entry)
    {
        int dot = entry.LastIndexOf('.');
        string name = dot >= 0 ? entry[(dot + 1)..] : entry;
        string? owner = dot >= 0 ? entry[..dot] : null;
        return file.Methods.FirstOrDefault(m => m.Name == name && !m.IsConstructor && (owner is null || m.Owner == owner));
    }

    // ---- what can run --------------------------------------------------------------------------

    private static readonly HashSet<string> NodeClasses = new(StringComparer.Ordinal) { "TreeNode", "ListNode" };

    public static IReadOnlyList<EntryPoint> Entries(JvFile file)
    {
        var named = DesignExamples.ClassesNamed(file.Comments);
        var topLevel = file.Classes.Where(c => c.TopLevel && !NodeClasses.Contains(c.Name)).ToList();
        var designs = topLevel.Where(c => named.Contains(c.Name) || (!c.Constructible && file.Methods.Any(m => m.Owner == c.Name && !m.IsConstructor && m.IsPublic)))
            .ToList();
        var entries = new List<EntryPoint>();

        foreach (var type in designs)
        {
            bool example = named.Contains(type.Name);
            entries.Add(new EntryPoint("", type.Name, new[]
            {
                new EntryParameter(DesignExamples.Parameters[0], "list of operations", example),
                new EntryParameter(DesignExamples.Parameters[1], "list of arguments", example)
            })
            {
                Design = true,
                Generatable = false,
                Line = type.Line,
                Reason = example ? null : EntryPointScanner.DesignNeedsExample(type.Name)
            });
        }

        var designNames = designs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var method in file.Methods)
        {
            var owner = topLevel.FirstOrDefault(c => c.Name == method.Owner);
            if (owner is null || designNames.Contains(owner.Name) || method.IsConstructor || !method.IsPublic) continue;
            if (method.Name == "main" && method.IsStatic) continue;

            var search = Examples(file, method);
            var all = Parameters(method);
            var parameters = method.Parameters.Select((p, index) =>
            {
                var type = JavaTypes.Map(p.Type);
                bool given = search.Examples.Any(e => e.Values[index] is not null);
                bool generatable = type is not null && ParameterBinder.ShapeFor(all[index], all) is not null;
                return (Parameter: new EntryParameter(p.Name, p.Type, type is not null && (generatable || given)), Generatable: generatable);
            }).ToList();

            string? reason = null;
            if (!method.IsStatic && !owner.Constructible)
                reason = $"{owner.Name} needs arguments to be created, so this method cannot be called on its own.";
            else if (parameters.FirstOrDefault(p => !p.Parameter.Supported) is { Parameter: not null } unsupported)
                reason = JavaTypes.Map(unsupported.Parameter.Type) is null
                    ? $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which the visualizer can neither generate nor read from an example."
                    : $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which cannot be generated. " +
                      "Give it a value in an example comment, and it is used.";

            entries.Add(new EntryPoint(method.Owner, method.Name, parameters.Select(p => p.Parameter).ToList())
            {
                Reason = reason,
                Line = method.Line,
                Generatable = reason is null && parameters.All(p => p.Generatable)
            });
        }
        return entries.OrderByDescending(e => e.Measurable).ThenBy(e => e.Line).ToList();
    }

    public static IReadOnlyList<SolutionParameter> Parameters(JvMethod method)
        => method.Parameters.Select(p => new SolutionParameter(p.Name, JavaTypes.Map(p.Type) ?? typeof(object)) { Written = p.Type }).ToList();

    public static ExampleSearch Examples(JvFile file, JvMethod method)
        => ExampleFinder.Combine(ExampleFinder.FromCommentLines(file.Comments, method.Parameters.Select(p => p.Name).ToList()),
            Array.Empty<string>());

    public ExampleSearch DesignExamplesFound()
        => new(DesignExamples.FromCommentLines(File.Comments, Entry).ToList(), Array.Empty<string>());

    public IReadOnlyList<MethodParameters> Methods
        => File.Methods.Select(m => new MethodParameters(m.IsConstructor ? m.Owner : m.Name, m.Parameters.Select(p => p.Name).ToList()))
            .GroupBy(m => m.Name).Select(g => g.First()).ToList();

    // ---- building --------------------------------------------------------------------------------

    public (JavaPlan? Plan, string? Classes, RunStatus Status, IReadOnlyList<string> Errors) Build(JavaHost host, bool stepping)
    {
        if (File.Problem is { } problem)
            return (null, null, RunStatus.CompileError, new[] { $"{problem}. The visualizer could not read this Java." });
        if (Method is null && DesignClass is null)
            return (null, null, RunStatus.CompileError, new[] { $"No method named '{Entry}' was found." });

        var plan = JavaInstrumenter.Build(File, Method, DesignClass?.Name, stepping);
        string key = JavaHost.Hash(plan.Render(new Dictionary<int, string>()).Source);
        var left = host.Remembered(key) ?? new Dictionary<int, string>();
        bool everything = false;
        IReadOnlyList<JavaDiagnostic> last = Array.Empty<JavaDiagnostic>();

        for (int round = 0; round <= Repairs; round++)
        {
            var (source, placed) = plan.Render(left);
            var compiled = host.Compile(source, plan.FileName);
            if (compiled.Classes is { } classes)
            {
                if (left.Count > 0) host.Remember(key, left);
                return (plan, classes, RunStatus.Completed, Array.Empty<string>());
            }
            last = compiled.Errors;

            var lineStarts = LineStarts(source);
            var blamed = new Dictionary<int, string>();
            bool elsewhere = false;
            foreach (var error in compiled.Errors)
            {
                int offset = error.Line >= 1 && error.Line <= lineStarts.Count ? lineStarts[error.Line - 1] + Math.Max(0, error.Column - 1) : -1;
                int? group = offset < 0 ? null
                    : placed.Where(p => p.Value.Any(r => offset >= r.Start && offset < r.End)).Select(p => (int?)p.Key).FirstOrDefault();
                if (group is { } g && !left.ContainsKey(g))
                    blamed[g] = error.Key == "compiler.err.var.might.not.have.been.initialized" ? "not set yet" : "not readable here";
                else
                {
                    elsewhere = true;
                    Console.Error.WriteLine($"Java build error not caused by one insertion (round {round}): {error.Line}:{error.Column} {error.Key} {error.Message}");
                }
            }

            if (!elsewhere && blamed.Count > 0)
            {
                foreach (var (g, why) in blamed)
                {
                    left[g] = why;
                    if (why == "not set yet") foreach (var later in plan.Following(g)) left.TryAdd(later, why);
                }
                continue;
            }
            if (everything) break;
            everything = true;
            foreach (var g in plan.Groups.Keys) left.TryAdd(g, "not readable here");
        }

        var plain = host.Compile(JavaInstrumenter.Build(File, Method, DesignClass?.Name, stepping, instrument: false)
            .Render(new Dictionary<int, string>()).Source, plan.FileName, raw: false);
        if (plain.Classes is null)
        {
            var own = plain.Errors.Where(e => e.Line >= 1 && e.Line <= File.LineCount)
                .Select(e => $"line {e.Line}{(e.Column > 0 ? $", column {e.Column}" : "")}: {e.Message}")
                .Distinct().Take(10).ToList();
            if (own.Count > 0) return (null, null, RunStatus.CompileError, own);
        }

        Console.Error.WriteLine("Instrumented Java failed to compile:\n" + string.Join("\n", last.Select(e => $"{e.Line}:{e.Column} {e.Key} {e.Message}")));
        return (null, null, RunStatus.RuntimeError, new[]
        {
            "The visualizer could not prepare this code to run — it uses Java the visualizer does not read yet. " +
            "This is a limitation of the visualizer, not a mistake in your code."
        }.Concat(last.Take(3).Select(e => e.Message)).ToList());
    }

    private static List<int> LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++) if (source[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    public IRunTarget Target(JavaHost host, string classes, JavaPlan plan)
        => new NativeTarget(host.Java, plan.Statements, plan.Sites, Parameters(Method!), "java", DepthLimit, RunTimeout)
        {
            Launcher = host.Launcher(classes)
        };
}

/// <summary>The Java types LeetCode passes, as the .NET types the shared input code builds and formats.</summary>
public static class JavaTypes
{
    public static Type? Map(string written)
    {
        string type = Regex.Replace(written, @"\s+", "").Replace("final", "");
        switch (type)
        {
            case "int": case "Integer": case "short": case "Short": case "byte": case "Byte": return typeof(int);
            case "long": case "Long": return typeof(long);
            case "double": case "Double": case "float": case "Float": return typeof(double);
            case "boolean": case "Boolean": return typeof(bool);
            case "char": case "Character": return typeof(char);
            case "String": return typeof(string);
            case "TreeNode": return typeof(PyTreeNode);
            case "ListNode": return typeof(PyListNode);
        }
        if (type.EndsWith("[]")) return Map(type[..^2])?.MakeArrayType();
        var generic = Regex.Match(type, @"^(List|ArrayList|LinkedList|Collection|Iterable)<(.+)>$");
        if (generic.Success) return Map(generic.Groups[2].Value)?.MakeArrayType();
        return null;
    }
}

/// <summary>A reading of a Java method's time complexity from its shape, as for C++ and Rust.</summary>
public static class JavaEstimator
{
    public static readonly StaticEstimate Space = new(ComplexityClass.Unknown,
        "Reading Java code for space is not supported; the measured figures are the answer.", 0, 0, 0);

    private static readonly HashSet<string> Scans = new(StringComparer.Ordinal) { "contains", "indexOf", "lastIndexOf", "stream", "clone", "toCharArray" };

    public static StaticEstimate Time(JvFile file, JvMethod method)
    {
        var own = Own(file, method);
        // A helper the method calls — dfs(...) in the same class — is where the work often is.
        string body = Text(file, method.Body);
        var helpers = file.Methods
            .Where(m => m != method && m.Owner == method.Owner && Regex.IsMatch(body, $@"\b{Regex.Escape(m.Name)}\s*\("))
            .Select(m => Own(file, m))
            .ToList();
        var deepest = helpers.Where(h => Growth.Rank(h.Class) > Growth.Rank(own.Class)).MaxBy(h => Growth.Rank(h.Class));
        return deepest is null ? own : deepest with { Reasoning = $"In a helper it calls: {deepest.Reasoning}" };
    }

    private static string Text(JvFile file, JvStatement statement)
        => file.Text[file.Tokens[statement.First].Start..file.Tokens[statement.Last].End];

    private static StaticEstimate Own(JvFile file, JvMethod method)
    {
        var (linear, log) = Depth(file, method.Body);
        string body = Text(file, method.Body);
        var calls = Regex.Matches(body, $@"(?<![\w.]){Regex.Escape(method.Name)}\s*\(([^()]*(\([^()]*(\([^()]*\))*[^()]*\))*[^()]*)\)").ToList();
        int selfCalls = calls.Count;

        if (selfCalls >= 2 && calls.All(c => Regex.IsMatch(c.Groups[1].Value, @"\.(left|right|next)\b")))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{method.Name} calls itself on a node's children, so each node is visited once.", linear, log, selfCalls);
        // A flood fill or graph walk marks what it has visited before going further — grid[r][c] = '0',
        // visited[i] = true, seen.add(x) — so it visits each cell or node once.
        if (selfCalls >= 2 && Regex.IsMatch(body, @"\w+(\[[^\]]+\])+\s*=[^=]|\b(visited|seen)\w*\s*\.\s*add\s*\("))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{method.Name} marks what it has visited before calling itself, so each one is visited once.", linear, log, selfCalls);
        if (selfCalls >= 2 && Regex.IsMatch(body, @"\b(memo|cache|dp|seen)\w*\s*(\.(get|containsKey|put)\b|\[)"))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{method.Name} calls itself {selfCalls} times, but stores each answer and returns it when asked again, " +
                "so each value is worked out once.", linear, log, selfCalls);
        if (selfCalls >= 2)
            return new StaticEstimate(ComplexityClass.Exponential,
                $"{method.Name} calls itself {selfCalls} times in its body; without memoisation that branches at every call.", linear, log, selfCalls);
        if (selfCalls == 1)
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{method.Name} calls itself once per call{(linear > 0 ? ", with a loop in each call" : "")}.", linear, log, selfCalls);

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

    private static (int Linear, int Log) Depth(JvFile file, JvStatement statement)
    {
        var (linear, log) = statement.Children
            .Concat(statement.Else is null ? Array.Empty<JvStatement>() : new[] { statement.Else })
            .Select(c => Depth(file, c))
            .DefaultIfEmpty((0, 0))
            .MaxBy(d => d.Item1 * 10 + d.Item2);
        if (statement.Kind is JvStatementKind.Local or JvStatementKind.Expression or JvStatementKind.Return or JvStatementKind.If
            or JvStatementKind.While && ScansInLibrary(file, statement))
            linear = Math.Max(linear, 1);
        if (statement.Kind is not (JvStatementKind.For or JvStatementKind.ForEach or JvStatementKind.While or JvStatementKind.Do))
            return (linear, log);
        string text = file.Text[file.Tokens[statement.First].Start..file.Tokens[statement.HeaderEnd > 0 ? statement.HeaderEnd : statement.Last].End];
        bool halves = Regex.IsMatch(text, @"(/=\s*2\b|\*=\s*2\b|>>=\s*1\b|<<=\s*1\b|\bmid\b|\blo\s*<=?\s*hi\b|\bleft\s*<=?\s*right\b)") &&
                      statement.Kind != JvStatementKind.ForEach;
        if (!halves && statement.Kind == JvStatementKind.While)
            halves = Regex.IsMatch(Text(file, statement.Children[0]), @"\bmid\b|/=\s*2\b|>>=\s*1\b");
        return halves ? (linear, log + 1) : (linear + 1, log);
    }

    private static bool ScansInLibrary(JvFile file, JvStatement statement)
    {
        int last = statement.HeaderEnd > 0 ? statement.HeaderEnd : statement.Last;
        for (int i = statement.First; i < last; i++)
            if (file.Tokens[i].Kind == JvTokenKind.Identifier && i > 1 && file.Tokens[i - 1].Text == "." &&
                file.Tokens[i + 1].Text == "(" && Scans.Contains(file.Tokens[i].Text) && !Hashed(file, file.Tokens[i - 2].Text))
                return true;
        return false;
    }

    /// <summary>Whether a variable is declared as a set or a map, whose contains is not a scan.</summary>
    private static bool Hashed(JvFile file, string name)
        => Regex.IsMatch(file.Text, $@"\b(Set|HashSet|TreeSet|LinkedHashSet|Map|HashMap|TreeMap|LinkedHashMap)\s*<[^;=()]*>\s+{Regex.Escape(name)}\b");
}
