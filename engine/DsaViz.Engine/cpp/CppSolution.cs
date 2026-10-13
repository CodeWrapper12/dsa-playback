using System.Globalization;
using System.Text.Json;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Native;
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
    /// <summary>For a design problem, its class; null otherwise.</summary>
    public CppClass? DesignClass { get; }

    private CppSolution(CppFile file, string entry)
    {
        File = file;
        Entry = entry;
        if (!entry.Contains('.') && file.Classes.FirstOrDefault(c => c.Name == entry) is { } type) DesignClass = type;
        else Function = Find(file, entry);
    }

    /// <summary>
    /// The classes run as LeetCode design problems: one its example names, or one that cannot
    /// be created without arguments and has public member functions.
    /// </summary>
    public static IReadOnlyList<CppClass> DesignClasses(CppFile file)
    {
        var named = DesignExamples.ClassesNamed(file.Comments);
        var nodes = NodeClasses(file);
        return file.Classes
            .Where(c => !nodes.Contains(c.Name) && c.Name != "Solution" &&
                        (named.Contains(c.Name) ||
                         (!c.Constructible && file.Functions.Any(f => f.Owner == c.Name && f.IsPublic && !f.IsConstructor && !f.IsStatic))))
            .GroupBy(c => c.Name).Select(g => g.First())
            .ToList();
    }

    public ExampleSearch DesignExamplesFound()
        => new(DesignExamples.FromCommentLines(File.Comments, Entry).ToList(), Array.Empty<string>());

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
        var named = DesignExamples.ClassesNamed(file.Comments);
        var designs = DesignClasses(file);
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
        var designNames = designs.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var function in file.Functions)
        {
            if (function.IsConstructor || !function.IsPublic) continue;
            // A design class's members are run through its example's calls, not one by one.
            if (function.Owner is not null && designNames.Contains(function.Owner)) continue;
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
                reason = $"{owner} needs arguments to be created, so this member cannot be called on its own.";
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
        if (Function is null && DesignClass is null)
            return (null, null, RunStatus.CompileError, new[] { $"No function named '{Entry}' was found." });

        var program = CppInstrumenter.Build(File, Function, stepping, DesignClass);
        var compiled = host.Compile(program.Source);
        if (compiled.Executable is { } executable) return (program, executable, RunStatus.Completed, Array.Empty<string>());

        var plain = host.Compile(CppInstrumenter.Plain(File, Function, DesignClass));
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

    public IRunTarget Target(string executable, CppProgram program)
        => new NativeTarget(executable, program.Statements, program.Sites, Parameters(Function!), "cpp", DepthLimit, RunTimeout);

    /// <summary>A target for the design problem's calls, given as its two lists of LeetCode text.</summary>
    public IRunTarget DesignTarget(string executable, CppProgram program)
        => new NativeTarget(executable, program.Statements, program.Sites, Array.Empty<SolutionParameter>(), "cpp", DepthLimit, RunTimeout)
        {
            TextInputs = true
        };

    /// <summary>The design class's constructor or public method for a number of arguments, as its parameters.</summary>
    public IReadOnlyList<DsaViz.Engine.Analysis.DesignArg>? DesignSignature(string operation, int arity)
    {
        var type = DesignClass!;
        if (operation == type.Name && arity == 0 && !type.HasConstructors) return Array.Empty<DsaViz.Engine.Analysis.DesignArg>();
        var member = File.Functions.FirstOrDefault(f => f.Owner == type.Name && f.IsPublic && !f.IsStatic &&
            (operation == type.Name ? f.IsConstructor : !f.IsConstructor && f.Name == operation) && f.Parameters.Count == arity);
        return member?.Parameters.Select(p => DsaViz.Engine.Analysis.DesignArg.Of(p.Name, p.Type)).ToList();
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
