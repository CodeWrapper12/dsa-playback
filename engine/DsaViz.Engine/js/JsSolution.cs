using System.Text.RegularExpressions;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Native;
using DsaViz.Engine.Python;

namespace DsaViz.Engine.Js;

// What the reader answers with, read straight into records.

public sealed record JsParameter(string Name, string Type);
public sealed record JsFunction(string Name, int Line, IReadOnlyList<JsParameter> Parameters, string Returns);
public sealed record JsMethod(string Name, int Line, IReadOnlyList<JsParameter> Parameters, string Returns);

/// <summary>A class, or LeetCode's JavaScript prototype style: a constructor function with methods on its prototype.</summary>
public sealed record JsClass(string Name, int Line, string Style, IReadOnlyList<JsParameter>? Constructor, IReadOnlyList<JsMethod> Methods);

public sealed record JsComment(string Text, int Line);

public sealed record JsDescription
{
    public string? Problem { get; init; }
    public IReadOnlyList<JsComment> Comments { get; init; } = Array.Empty<JsComment>();
    public IReadOnlyList<JsFunction> Functions { get; init; } = Array.Empty<JsFunction>();
    public IReadOnlyList<JsClass> Classes { get; init; } = Array.Empty<JsClass>();
    public int LineCount { get; init; }
    public StaticEstimate? Time { get; init; }
    public string? Internal { get; init; }
}

public sealed record JsSite(int Site, int Line, int Column, string Member, string Cost);

public sealed record JsBuilt
{
    public string? Problem { get; init; }
    public string? Internal { get; init; }
    public string? Program { get; init; }
    public IReadOnlyList<StatementInfo> Statements { get; init; } = Array.Empty<StatementInfo>();
    public IReadOnlyList<JsSite> Sites { get; init; } = Array.Empty<JsSite>();
}

/// <summary>A program built and saved, ready to run: where its launcher is, and what it counts.</summary>
public sealed record JsProgram(string Launcher, IReadOnlyList<StatementInfo> Statements, IReadOnlyList<LibraryCallSite> Sites);

/// <summary>
/// A JavaScript or TypeScript solution: the functions and design classes it offers, their
/// parameters as types the shared input code understands (from TypeScript's annotations, or
/// the JSDoc LeetCode puts above its JavaScript), its examples, and builds of it.
/// </summary>
public sealed class JsSolution
{
    public static bool IsJs(string file)
        => file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
           (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase));

    public static bool IsTypeScript(string file) => file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

    public const int DepthLimit = 20_000;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    public JsHost Host { get; }
    public string Source { get; }
    public string Entry { get; }
    public bool TypeScript { get; }
    public JsDescription Description { get; }
    public JsFunction? Function { get; }
    /// <summary>For a design problem, its class; null otherwise.</summary>
    public JsClass? DesignClass { get; }

    private JsSolution(JsHost host, string source, string entry, bool typescript, JsDescription description)
    {
        Host = host;
        Source = source;
        Entry = entry;
        TypeScript = typescript;
        Description = description;
        if (!entry.Contains('.') && description.Classes.FirstOrDefault(c => c.Name == entry) is { } type) DesignClass = type;
        else Function = description.Functions.FirstOrDefault(f => f.Name == entry || $".{f.Name}" == entry);
    }

    public static JsSolution Read(JsHost host, string source, string entry, bool typescript)
        => new(host, source, entry, typescript, Describe(host, source, entry, typescript));

    public static JsDescription Describe(JsHost host, string source, string entry, bool typescript)
    {
        var description = host.Read<JsDescription>(new
        {
            mode = "describe",
            typescript = JsHost.TypeScript,
            language = typescript ? "ts" : "js",
            entry,
            source
        });
        if (description.Internal is { } failure)
        {
            Console.Error.WriteLine("JavaScript reader failed: " + failure);
            return description with { Problem = "The visualizer could not read this code. This is a limitation of the visualizer, not a mistake in your code." };
        }
        return description;
    }

    // ---- what can run --------------------------------------------------------------------------

    private IReadOnlyList<(string Text, int Line)> CommentLines => Description.Comments.Select(c => (c.Text, c.Line)).ToList();

    public static IReadOnlyList<EntryPoint> Entries(JsDescription description)
    {
        var comments = description.Comments.Select(c => (c.Text, c.Line)).ToList();
        var named = DesignExamples.ClassesNamed(comments);
        var designs = description.Classes
            .Where(c => named.Contains(c.Name) || ((c.Constructor?.Count ?? 0) > 0 && c.Methods.Count > 0))
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
                // Measured per call by repeating the example's calls, so it needs one.
                Generatable = example,
                Line = type.Line,
                Reason = example ? null : EntryPointScanner.DesignNeedsExample(type.Name)
            });
        }

        foreach (var function in description.Functions)
        {
            var search = Examples(comments, function);
            var all = Parameters(function, search);
            var parameters = function.Parameters.Select((p, index) =>
            {
                // Typed by its annotation, or failing that by the value an example gives it.
                var type = all[index].Type == typeof(object) ? null : all[index].Type;
                bool given = search.Examples.Any(e => e.Values[index] is not null);
                bool generatable = type is not null && JsTypes.Map(p.Type) is not null && ParameterBinder.ShapeFor(all[index], all) is not null;
                return (Parameter: new EntryParameter(p.Name, p.Type.Length > 0 ? p.Type : "(no type)", type is not null && (generatable || given)),
                    Generatable: generatable);
            }).ToList();

            string? reason = null;
            if (parameters.FirstOrDefault(p => !p.Parameter.Supported) is { Parameter: not null } unsupported)
                reason = unsupported.Parameter.Type == "(no type)"
                    ? $"Parameter '{unsupported.Parameter.Name}' has no type and no example gives it a value. Add the JSDoc LeetCode writes, like @param {{number[]}} {unsupported.Parameter.Name}, or a TypeScript type."
                    : $"Parameter '{unsupported.Parameter.Name}' is '{unsupported.Parameter.Type}', which cannot be generated. Give it a value in an example comment, and it is used.";

            entries.Add(new EntryPoint("", function.Name, parameters.Select(p => p.Parameter).ToList())
            {
                Reason = reason,
                Line = function.Line,
                Generatable = reason is null && parameters.All(p => p.Generatable)
            });
        }
        return entries.OrderByDescending(e => e.Measurable).ThenBy(e => e.Line).ToList();
    }

    /// <summary>
    /// The parameters as the shared input code types them. JavaScript has one number type, so a
    /// number is whole unless an example gives it a fraction — myPow's x = 2.10000.
    /// </summary>
    public static IReadOnlyList<SolutionParameter> Parameters(JsFunction function, ExampleSearch? examples = null)
        => function.Parameters.Select((p, index) =>
        {
            var type = JsTypes.Map(p.Type);
            bool fractional = examples is not null && examples.Examples.Any(e =>
                index < e.Values.Count && e.Values[index] is { } literal && Regex.IsMatch(literal, @"\d\.\d"));
            if (fractional && type == typeof(int)) type = typeof(double);
            if (fractional && type == typeof(int[])) type = typeof(double[]);
            if (type is null && examples is not null && examples.Examples.Select(e => index < e.Values.Count ? e.Values[index] : null).FirstOrDefault(v => v is not null) is { } given)
                type = PythonTypes.FromExample(given);
            return new SolutionParameter(p.Name, type ?? typeof(object)) { Written = p.Type.Length > 0 ? p.Type : "any" };
        }).ToList();

    public static ExampleSearch Examples(IReadOnlyList<(string Text, int Line)> comments, JsFunction function)
        => ExampleFinder.Combine(ExampleFinder.FromCommentLines(comments, function.Parameters.Select(p => p.Name).ToList()), Array.Empty<string>());

    public ExampleSearch Examples() => Examples(CommentLines, Function!);

    public ExampleSearch DesignExamplesFound()
        => new(DesignExamples.FromCommentLines(CommentLines, Entry).ToList(), Array.Empty<string>());

    /// <summary>Each function's parameter names, for the run view's call tree; a class's constructor under its name.</summary>
    public IReadOnlyList<MethodParameters> Methods
        => Description.Functions.Select(f => new MethodParameters(f.Name, f.Parameters.Select(p => p.Name).ToList()))
            .Concat(Description.Classes.SelectMany(c => c.Methods.Select(m => new MethodParameters(m.Name, m.Parameters.Select(p => p.Name).ToList()))
                .Append(new MethodParameters(c.Name, (c.Constructor ?? Array.Empty<JsParameter>()).Select(p => p.Name).ToList()))))
            .GroupBy(m => m.Name).Select(g => g.First()).ToList();

    // ---- building --------------------------------------------------------------------------------

    public (JsProgram? Program, RunStatus Status, IReadOnlyList<string> Errors) Build(bool stepping)
    {
        if (Description.Problem is { } problem)
            return (null, RunStatus.CompileError, new[] { problem });
        if (Function is null && DesignClass is null)
            return (null, RunStatus.CompileError, new[] { $"No function named '{Entry}' was found." });

        var built = Host.Read<JsBuilt>(new
        {
            mode = "build",
            typescript = JsHost.TypeScript,
            language = TypeScript ? "ts" : "js",
            entry = Function?.Name,
            design = DesignClass?.Name,
            stepping,
            runtime = JsHost.Runtime,
            source = Source
        });
        if (built.Problem is { } syntax) return (null, RunStatus.CompileError, new[] { syntax });
        if (built.Internal is not null || built.Program is null)
        {
            Console.Error.WriteLine("JavaScript build failed: " + built.Internal);
            return (null, RunStatus.RuntimeError, new[]
            {
                "The visualizer could not prepare this code to run — it uses JavaScript the visualizer does not read yet. " +
                "This is a limitation of the visualizer, not a mistake in your code."
            });
        }

        var sites = built.Sites.Select(s => new LibraryCallSite(s.Site, s.Line, s.Column, s.Member, s.Cost)).ToList();
        return (new JsProgram(JsHost.Save(built.Program), built.Statements, sites), RunStatus.Completed, Array.Empty<string>());
    }

    private string Language => "javascript";

    public IRunTarget Target(JsProgram program, IReadOnlyList<SolutionParameter> parameters)
        => new NativeTarget(Host.Node, program.Statements, program.Sites, parameters, Language, DepthLimit, RunTimeout)
        {
            Launcher = JsHost.Launcher(program.Launcher)
        };

    /// <summary>A target for the design problem's calls, given as its two lists of LeetCode text.</summary>
    public IRunTarget DesignTarget(JsProgram program)
        => new NativeTarget(Host.Node, program.Statements, program.Sites, Array.Empty<SolutionParameter>(), Language, DepthLimit, RunTimeout)
        {
            Launcher = JsHost.Launcher(program.Launcher),
            TextInputs = true
        };

    /// <summary>The design class's constructor or method for a number of arguments, as its parameters.</summary>
    public IReadOnlyList<DesignArg>? DesignSignature(string operation, int arity)
    {
        var type = DesignClass!;
        var parameters = operation == type.Name
            ? type.Constructor ?? Array.Empty<JsParameter>()
            : type.Methods.FirstOrDefault(m => m.Name == operation)?.Parameters;
        return parameters is not null && parameters.Count == arity ? parameters.Select(p => DesignArg.Of(p.Name, p.Type)).ToList() : null;
    }

    public static readonly StaticEstimate Space = new(ComplexityClass.Unknown,
        "Reading JavaScript code for space is not supported; the measured figures are the answer.", 0, 0, 0);
}

/// <summary>The JavaScript and TypeScript types LeetCode passes, as the .NET types the shared input code builds and formats.</summary>
public static class JsTypes
{
    public static Type? Map(string written)
    {
        string type = Regex.Replace(written, @"\s+", "");
        // TreeNode | null, ?TreeNode, (TreeNode|null): a node that may be missing is a node.
        type = Regex.Replace(type, @"\|(null|undefined)|(null|undefined)\|", "").TrimStart('?', '!');
        if (type.StartsWith('(') && type.EndsWith(')')) type = type[1..^1];
        switch (type)
        {
            case "number": case "int": case "integer": return typeof(int);
            case "boolean": return typeof(bool);
            case "string": return typeof(string);
            case "character": case "char": return typeof(char);
            case "TreeNode": return typeof(PyTreeNode);
            case "ListNode": return typeof(PyListNode);
        }
        if (type.EndsWith("[]")) return Map(type[..^2])?.MakeArrayType();
        var generic = Regex.Match(type, @"^Array<(.+)>$");
        if (generic.Success) return Map(generic.Groups[1].Value)?.MakeArrayType();
        return null;
    }
}
