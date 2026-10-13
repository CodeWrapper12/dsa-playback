using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Tests that assert how long something took run on their own. Beside the rest of the
/// suite on GitHub's three-core runners, a runaway recursion stopped in under a second
/// took 7.7 s to report, and an endless loop 13.5 s, while other tests had the processor.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
