using System.Reflection;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Runtime;

namespace DsaViz.Engine.Measurement;

/// <summary>
/// One solution, ready to run at any size: C# compiled into this process, or Python run
/// in its own. Measuring, fitting and explaining are the same for both; only running differs.
/// </summary>
public interface IRunTarget : IDisposable
{
    IReadOnlyList<SolutionParameter> Parameters { get; }

    /// <summary>
    /// One measured run. The arguments are built afresh for every attempt, because many
    /// solutions sort or mark their input in place.
    /// </summary>
    TraceResult Run(Func<object?[]> arguments, long budget);

    /// <summary>False once the solution cannot be run again — a C# run abandoned after a timeout.</summary>
    bool Usable { get; }
}

public sealed class CSharpTarget : IRunTarget
{
    private readonly CompiledSolution _solution;

    public CSharpTarget(CompiledSolution solution)
    {
        _solution = solution;
        Parameters = SolutionParameter.From(solution.Parameters);
    }

    public IReadOnlyList<SolutionParameter> Parameters { get; }

    public TraceResult Run(Func<object?[]> arguments, long budget)
        => Runner.Cleanest(() => Runner.Invoke(_solution, arguments(), budget));

    public bool Usable => !_solution.Abandoned;

    // An abandoned solution's thread may still be running inside its assembly; unloading
    // would pull the code out from under it.
    public void Dispose()
    {
        if (!_solution.Abandoned) _solution.Dispose();
    }
}

/// <summary>
/// A C# design problem, run at a size from its two lists — the operations and their
/// arguments, as LeetCode writes them — with what each call cost.
/// </summary>
public sealed class CSharpDesignTarget : IRunTarget
{
    private readonly CompiledSolution _solution;
    private readonly Type _type;

    public CSharpDesignTarget(CompiledSolution solution, Type type)
    {
        _solution = solution;
        _type = type;
    }

    public IReadOnlyList<SolutionParameter> Parameters { get; } = Array.Empty<SolutionParameter>();

    public TraceResult Run(Func<object?[]> arguments, long budget)
    {
        var lists = arguments();
        var operations = (string[])Literals.ToObject((string)lists[0]!, typeof(string[]))!;
        return Runner.Cleanest(() =>
        {
            // Made afresh for every attempt: a constructor may keep and change an array it is given.
            var calls = Stepper.DesignArguments(_type, operations, (string)lists[1]!)!;
            var starts = new List<long>(operations.Length);
            var trace = Runner.Invoke(_solution,
                new object?[] { _type, operations, calls, new Action<int>(_ => starts.Add(Operations())) }, budget);
            return trace with { CallCosts = NativeCosts.Between(starts, trace.TotalOperations) };
        });
    }

    private long Operations() => (_solution.Slot?.GetValue(null) as TraceState)?.Operations ?? 0;

    /// <summary>A constructor or method's parameters, for the number of arguments a call gives it.</summary>
    public static IReadOnlyList<DesignArg>? Signature(Type type, string operation, int arity)
    {
        MethodBase? member = operation == type.Name
            ? type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == arity)
            : type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName && string.Equals(m.Name, operation, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(m => m.GetParameters().Length == arity);
        return member?.GetParameters().Select(p => DesignArg.Of(p.Name ?? "", ParameterBinder.FriendlyName(p.ParameterType))).ToList();
    }

    public bool Usable => !_solution.Abandoned;

    public void Dispose()
    {
        if (!_solution.Abandoned) _solution.Dispose();
    }
}

/// <summary>Turns the operation count at each call's start into what each call cost.</summary>
public static class NativeCosts
{
    public static IReadOnlyList<long> Between(IReadOnlyList<long> starts, long total)
    {
        var costs = new long[starts.Count];
        for (int i = 0; i < starts.Count; i++) costs[i] = (i + 1 < starts.Count ? starts[i + 1] : total) - starts[i];
        return costs;
    }
}
