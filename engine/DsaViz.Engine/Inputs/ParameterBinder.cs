using System.Reflection;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Inputs;

public sealed class UnsupportedSignatureException : Exception
{
    public UnsupportedSignatureException(string message) : base(message) { }
}

public static class ParameterBinder
{
    private static readonly (Type Type, InputShape Shape)[] Supported =
    {
        (typeof(int[]), InputShape.IntArray),
        (typeof(List<int>), InputShape.IntList),
        (typeof(string), InputShape.StringValue),
        (typeof(int[][]), InputShape.Matrix),
        (typeof(int), InputShape.IntScalar),
        (typeof(bool), InputShape.BoolScalar)
    };

    private static string SupportedList() =>
        string.Join(", ", Supported.Select(entry => FriendlyName(entry.Type)));

    private static string FriendlyName(Type type) => type switch
    {
        _ when type == typeof(int[]) => "int[]",
        _ when type == typeof(List<int>) => "List<int>",
        _ when type == typeof(string) => "string",
        _ when type == typeof(int[][]) => "int[][]",
        _ when type == typeof(int) => "int",
        _ when type == typeof(bool) => "bool",
        _ => type.Name
    };

    public static InputShape? ShapeFor(Type type)
    {
        foreach (var (candidate, shape) in Supported)
            if (candidate == type) return shape;
        return null;
    }

    /// <summary>
    /// Picks a sensible configuration from the signature alone, so a solution can be
    /// measured without the user writing anything. Everything chosen here is meant to
    /// be overridden from the configuration panel — the inference exists to make the
    /// common case zero-effort, not to be clever.
    /// </summary>
    public static IReadOnlyList<InputSpec> Infer(ParameterInfo[] parameters)
    {
        var specs = new List<InputSpec>(parameters.Length);

        foreach (var parameter in parameters)
        {
            var shape = ShapeFor(parameter.ParameterType)
                ?? throw new UnsupportedSignatureException(
                    $"Parameter '{parameter.Name}' has type " +
                    $"'{FriendlyName(parameter.ParameterType)}', which the engine cannot " +
                    $"generate. Supported parameter types are: {SupportedList()}.");

            specs.Add(shape switch
            {
                // A lone int is almost always "n" itself (Fib(int n)); an int alongside
                // a collection is almost always a target or a bound, which should stay
                // put while the collection grows.
                InputShape.IntScalar when parameters.Length == 1 =>
                    new InputSpec { Shape = shape, ScalesWithSize = true },
                InputShape.IntScalar =>
                    new InputSpec { Shape = shape, ScalesWithSize = false, FixedValue = 0 },
                InputShape.BoolScalar =>
                    new InputSpec { Shape = shape, ScalesWithSize = false, FixedValue = 0 },
                _ => new InputSpec { Shape = shape, Strategy = GenerationStrategy.Random }
            });
        }

        return specs;
    }

    /// <summary>
    /// Confirms a configuration actually fits the method. Reported as a named problem
    /// rather than a binding failure at invoke time, because "cannot bind argument 2"
    /// tells the user nothing about what to change.
    /// </summary>
    public static void Validate(ParameterInfo[] parameters, IReadOnlyList<InputSpec> specs)
    {
        if (parameters.Length != specs.Count)
            throw new UnsupportedSignatureException(
                $"The method takes {parameters.Length} parameter(s) but {specs.Count} " +
                "input(s) were configured.");

        for (int i = 0; i < parameters.Length; i++)
        {
            var expected = ShapeFor(parameters[i].ParameterType);

            if (expected is null)
                throw new UnsupportedSignatureException(
                    $"Parameter '{parameters[i].Name}' has type " +
                    $"'{FriendlyName(parameters[i].ParameterType)}', which the engine " +
                    $"cannot generate. Supported parameter types are: {SupportedList()}.");

            if (expected != specs[i].Shape)
                throw new UnsupportedSignatureException(
                    $"Parameter '{parameters[i].Name}' is " +
                    $"'{FriendlyName(parameters[i].ParameterType)}', which needs shape " +
                    $"{expected}, but {specs[i].Shape} was configured.");
        }
    }

    public static object?[] Bind(IReadOnlyList<InputSpec> specs, int size)
        => specs.Select(spec => (object?)InputGenerator.Generate(spec, size)).ToArray();
}
