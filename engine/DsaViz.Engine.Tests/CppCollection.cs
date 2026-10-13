using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// The C++ tests compile with the machine's compiler, which takes every core it is given.
/// Run beside the other tests, that made the timing checks on slow machines fail — a
/// runaway loop, cut off within a limit, seemed to take too long because the compiler had
/// the processor. They run on their own, after the rest.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CppCollection
{
    public const string Name = "C++ compiles";
}
