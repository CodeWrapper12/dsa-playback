using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// The one place that knows how instrumented code reaches its tracer.
///
/// Every instrumented assembly gets its own <c>__DsaVizTrace</c> class with a static
/// <c>S</c> slot. The runner points the slot at a fresh TraceState for each run, back at
/// Inactive afterwards, and at Revoked if the run is abandoned. Because the slot belongs
/// to the assembly, two compiled solutions can never share tracer state.
/// </summary>
public static class TraceCalls
{
    public const string HolderType = "__DsaVizTrace";
    public const string SlotField = "S";

    /// <summary>Prefix for every generated call, e.g. <c>global::__DsaVizTrace.S.Hit(3)</c>.</summary>
    public const string Prefix = "global::" + HolderType + "." + SlotField + ".";

    /// <summary>
    /// Compiled as its own syntax tree rather than appended to the user's file: a file with
    /// a file-scoped namespace cannot have a second top-level type added after it.
    /// </summary>
    public static SyntaxTree HolderTree() => CSharpSyntaxTree.ParseText(
        $"internal static class {HolderType} {{ " +
        $"public static global::DsaViz.Runtime.TraceState {SlotField} = global::DsaViz.Runtime.TraceState.Inactive; }}",
        path: "__DsaVizTrace.cs");
}
