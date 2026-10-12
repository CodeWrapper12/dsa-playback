using System.Reflection;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Inputs;

public sealed class UnsupportedSignatureException : Exception
{
    public UnsupportedSignatureException(string message) : base(message) { }
}

/// <summary>
/// A parameter to supply: its name, and the .NET type its values are built as. A C#
/// parameter's own type; for Python, the type its hint maps onto — List[int] is built as
/// an int[] and handed over as a list. <see cref="Written"/> is how the student wrote the
/// type, for messages.
/// </summary>
public sealed record SolutionParameter(string Name, Type Type)
{
    public string? Written { get; init; }

    public static IReadOnlyList<SolutionParameter> From(ParameterInfo[] parameters)
        => parameters.Select((p, i) => new SolutionParameter(p.Name ?? $"arg{i}", p.ParameterType)).ToList();
}

/// <summary>One parameter as the engine understands it, reported so the editor can offer only valid choices.</summary>
public sealed record ParameterDescription(
    string Name, string Type, InputShape Shape, IReadOnlyList<GenerationStrategy> ApplicableStrategies);

public static class ParameterBinder
{
    private static readonly GenerationStrategy[] ArrayStrategies =
    {
        GenerationStrategy.Random, GenerationStrategy.Distinct, GenerationStrategy.Sorted,
        GenerationStrategy.ReverseSorted, GenerationStrategy.AllDuplicates, GenerationStrategy.NearlySorted
    };

    // A 26-letter alphabet cannot supply unique characters beyond 26, so Distinct is not
    // offered for character data rather than offered and quietly degraded.
    private static readonly GenerationStrategy[] CharacterStrategies =
    {
        GenerationStrategy.Random, GenerationStrategy.Sorted, GenerationStrategy.ReverseSorted,
        GenerationStrategy.AllDuplicates, GenerationStrategy.NearlySorted
    };

    private static readonly string[] EdgeListNames = { "edges", "edge", "connections", "prerequisites", "roads" };
    private static readonly string[] NodeCountNames = { "n", "numnodes", "numcourses", "nodes", "count", "numvertices" };

    public static IReadOnlyList<GenerationStrategy> StrategiesFor(InputShape shape) => shape switch
    {
        InputShape.IntArray or InputShape.IntList or InputShape.StringArray or InputShape.LinkedList => ArrayStrategies,
        InputShape.StringValue or InputShape.CharArray => CharacterStrategies,
        InputShape.Matrix => new[] { GenerationStrategy.Random, GenerationStrategy.Sorted, GenerationStrategy.ReverseSorted, GenerationStrategy.AllDuplicates },
        InputShape.CharMatrix => new[] { GenerationStrategy.Random, GenerationStrategy.AllDuplicates },
        InputShape.Tree => new[] { GenerationStrategy.Random, GenerationStrategy.Balanced, GenerationStrategy.Skewed },
        InputShape.NestedIntList or InputShape.EdgeList => new[] { GenerationStrategy.Random, GenerationStrategy.Path },
        _ => Array.Empty<GenerationStrategy>()
    };

    /// <summary>The int parameter that gives a graph's number of nodes, beside its edge list.</summary>
    public static SolutionParameter? NodeCountParameter(IReadOnlyList<SolutionParameter> parameters)
        => parameters.FirstOrDefault(p => p.Type == typeof(int) && NodeCountNames.Contains(p.Name.ToLowerInvariant()));

    public static bool IsScalar(InputShape shape)
        => shape is InputShape.IntScalar or InputShape.LongScalar or InputShape.BoolScalar;

    /// <summary>
    /// What the engine can build for a parameter. An int[][] is an edge list rather than a
    /// matrix when its name says so and a node count sits beside it — (int n, int[][] edges)
    /// is how LeetCode passes a graph, and filling it as a square matrix of random numbers
    /// would hand a graph algorithm nonsense.
    /// </summary>
    public static InputShape? ShapeFor(SolutionParameter parameter, IReadOnlyList<SolutionParameter> all)
    {
        var type = parameter.Type;

        if (type == typeof(int[][]))
        {
            bool namedAsEdges = EdgeListNames.Contains(parameter.Name.ToLowerInvariant());
            bool hasNodeCount = all.Any(p => p.Type == typeof(int) && NodeCountNames.Contains(p.Name.ToLowerInvariant()));
            return namedAsEdges && hasNodeCount ? InputShape.EdgeList : InputShape.Matrix;
        }

        return ShapeFor(type);
    }

    public static InputShape? ShapeFor(ParameterInfo parameter, ParameterInfo[] all)
        => ShapeFor(new SolutionParameter(parameter.Name ?? "", parameter.ParameterType), SolutionParameter.From(all));

    public static InputShape? ShapeFor(Type type)
    {
        if (type == typeof(int[])) return InputShape.IntArray;
        if (type == typeof(char[])) return InputShape.CharArray;
        if (type == typeof(string[])) return InputShape.StringArray;
        if (type == typeof(int[][])) return InputShape.Matrix;
        if (type == typeof(char[][])) return InputShape.CharMatrix;
        if (type == typeof(string)) return InputShape.StringValue;
        if (type == typeof(int)) return InputShape.IntScalar;
        if (type == typeof(long)) return InputShape.LongScalar;
        if (type == typeof(bool)) return InputShape.BoolScalar;

        // Any interface a List<int> satisfies: IList<int>, IEnumerable<int>, IReadOnlyList<int>...
        if (type != typeof(object) && type.IsAssignableFrom(typeof(List<int>))) return InputShape.IntList;
        if (type == typeof(List<List<int>>) ||
            (type != typeof(object) && type.IsAssignableFrom(typeof(List<IList<int>>))))
            return InputShape.NestedIntList;

        if (NodeShapes.AsTree(type) is not null) return InputShape.Tree;
        if (NodeShapes.AsList(type) is not null) return InputShape.LinkedList;
        return null;
    }

    public static string FriendlyName(Type type)
    {
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(string)) return "string";
        if (type == typeof(char)) return "char";
        if (type == typeof(double)) return "double";
        if (type.IsArray) return FriendlyName(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            string name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
        }
        return type.Name;
    }

    private const string SupportedList =
        "int[], char[], string[], int[][], char[][], string, int, long, bool, List<int> / IList<int>, " +
        "IList<IList<int>>, and LeetCode's TreeNode and ListNode";

    public static IReadOnlyList<ParameterDescription> Describe(ParameterInfo[] parameters) => Describe(SolutionParameter.From(parameters));

    public static IReadOnlyList<ParameterDescription> Describe(IReadOnlyList<SolutionParameter> parameters)
        => parameters.Select(p =>
        {
            var shape = ShapeFor(p, parameters) ?? throw Unsupported(p);
            return new ParameterDescription(p.Name, p.Written ?? FriendlyName(p.Type), shape, StrategiesFor(shape));
        }).ToList();

    /// <summary>
    /// Picks a sensible configuration from the signature alone, so a solution can be
    /// measured without the user writing anything. Everything chosen here can be changed
    /// from the editor; inference exists to make the common case zero-effort.
    /// </summary>
    public static IReadOnlyList<InputSpec> Infer(ParameterInfo[] parameters) => Infer(SolutionParameter.From(parameters));

    public static IReadOnlyList<InputSpec> Infer(IReadOnlyList<SolutionParameter> parameters)
    {
        var shapes = parameters.Select(p => ShapeFor(p, parameters) ?? throw Unsupported(p)).ToList();
        bool hasCollection = shapes.Any(s => !IsScalar(s));
        bool hasEdgeList = shapes.Contains(InputShape.EdgeList);

        return parameters.Select((parameter, index) =>
        {
            var shape = shapes[index];
            string name = parameter.Name.ToLowerInvariant();

            if (!IsScalar(shape))
                return new InputSpec { Shape = shape, Strategy = GenerationStrategy.Random };

            if (shape == InputShape.BoolScalar)
                return new InputSpec { Shape = shape, ScalesWithSize = false, FixedValue = 0 };

            // With no collection the integers are the problem size — Fib(n), UniquePaths(m, n).
            // Beside an edge list, the node count has to grow with it or the edges would
            // name nodes that do not exist. Anything else beside a collection is a target
            // or a bound and stays put; k is usually a window or a count, where 0 is degenerate.
            bool scales = !hasCollection || (hasEdgeList && NodeCountNames.Contains(name));
            return new InputSpec
            {
                Shape = shape,
                ScalesWithSize = scales,
                FixedValue = scales ? 0 : name == "k" ? 3 : 0
            };
        }).ToList();
    }

    /// <summary>
    /// Confirms a configuration actually fits the method. Reported as a named problem
    /// rather than a binding failure at invoke time, because "cannot bind argument 2"
    /// tells the user nothing about what to change.
    /// </summary>
    public static void Validate(ParameterInfo[] parameters, IReadOnlyList<InputSpec> specs)
        => Validate(SolutionParameter.From(parameters), specs);

    public static void Validate(IReadOnlyList<SolutionParameter> parameters, IReadOnlyList<InputSpec> specs)
    {
        if (parameters.Count != specs.Count)
            throw new UnsupportedSignatureException(
                $"The method takes {parameters.Count} parameter(s) but {specs.Count} input(s) were configured.");

        for (int i = 0; i < parameters.Count; i++)
        {
            var expected = ShapeFor(parameters[i], parameters) ?? throw Unsupported(parameters[i]);
            string written = parameters[i].Written ?? FriendlyName(parameters[i].Type);

            // An int[][] may legitimately be configured either way.
            bool interchangeable = parameters[i].Type == typeof(int[][]) &&
                                   specs[i].Shape is InputShape.Matrix or InputShape.EdgeList;

            if (expected != specs[i].Shape && !interchangeable)
                throw new UnsupportedSignatureException(
                    $"Parameter '{parameters[i].Name}' is '{written}', " +
                    $"which needs shape {expected}, but {specs[i].Shape} was configured.");

            var allowed = StrategiesFor(specs[i].Shape);
            if (allowed.Count > 0 && !allowed.Contains(specs[i].Strategy))
                throw new UnsupportedSignatureException(
                    $"'{specs[i].Strategy}' does not apply to parameter '{parameters[i].Name}' " +
                    $"({written}). Choose one of: {string.Join(", ", allowed)}.");
        }
    }

    public static object?[] Bind(IReadOnlyList<InputSpec> specs, ParameterInfo[] parameters, int size)
        => Bind(specs, SolutionParameter.From(parameters), size);

    public static object?[] Bind(IReadOnlyList<InputSpec> specs, IReadOnlyList<SolutionParameter> parameters, int size)
        => specs.Select((spec, i) => InputGenerator.Generate(spec, size, parameters[i].Type)).ToArray();

    private static UnsupportedSignatureException Unsupported(SolutionParameter parameter) => new(
        $"Parameter '{parameter.Name}' has type '{parameter.Written ?? FriendlyName(parameter.Type)}', which the engine " +
        $"cannot generate. Supported parameter types are: {SupportedList}.");
}
