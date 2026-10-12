using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;

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
