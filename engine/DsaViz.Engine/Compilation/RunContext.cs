using System.Reflection;
using System.Runtime.Loader;

namespace DsaViz.Engine.Compilation;

/// <summary>
/// A collectible load context for one compiled solution. Every run compiles a fresh
/// assembly; without unloading, a long-lived host (the VS Code extension keeps the
/// engine warm) would accumulate one assembly per run until it died.
/// </summary>
public sealed class RunContext : AssemblyLoadContext, IDisposable
{
    public RunContext() : base(isCollectible: true) { }

    // Returning null defers to the default context, so DsaViz.Runtime resolves to the
    // already-loaded copy. That matters: the tracer's counters must be the same
    // statics the engine reads afterwards, not a second copy loaded in here.
    protected override Assembly? Load(AssemblyName assemblyName) => null;

    public void Dispose() => Unload();
}
