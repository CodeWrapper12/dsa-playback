using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Instrumentation;

public sealed record InstrumentedSource(
    SyntaxTree Tree,
    IReadOnlyList<StatementInfo> Statements,
    IReadOnlyList<LibraryCallSite> Sites)
{
    /// <summary>Everything that has to be compiled together: the solution and its tracer slot.</summary>
    public IReadOnlyList<SyntaxTree> Trees => new[] { Tree, TraceCalls.HolderTree() };
}

/// <summary>
/// The pipeline that turns a user's solution into a measurable one. Order is load-bearing:
///
/// 1. Mark the expressions to charge, using the semantic model of the original file.
/// 2. Tag statements with their original positions, while positions still mean the file.
/// 3. Normalise braces and arrow bodies, so every executable position sits in a block.
///    Dropping this does not corrupt the program — it silently removes braceless and
///    arrow-bodied code from the measurement and disarms the budget. See BraceNormalizer.
/// 4. Wrap bodies for call depth, 5. apply the charges, 6. insert the statement hits.
/// </summary>
public static class Instrumenter
{
    public static SyntaxTree Parse(string source, string path = "solution.cs")
        => CSharpSyntaxTree.ParseText(source, path: path);

    public static InstrumentedSource Instrument(SourceAnalysis analysis)
    {
        var tree = analysis.Tree;
        var charged = analysis.CostCalls.Calls.Where(call => call.Charged).ToList();

        var wrapsByTarget = charged
            .SelectMany(call => call.Wraps.Select(wrap => (wrap, call)))
            .GroupBy(pair => pair.wrap.Target)
            .ToDictionary(group => group.Key, group => group.ToList());

        var marked = tree.GetRoot().ReplaceNodes(
            wrapsByTarget.Keys,
            (original, current) => current.WithAdditionalAnnotations(
                wrapsByTarget[original].Select(pair =>
                    CostRewriter.Annotation(pair.wrap.Kind, pair.call.Site, pair.call.Cost.Kind))));

        var annotated = new LineAnnotator(tree).Visit(marked)!;
        var normalised = new BraceNormalizer().Visit(annotated)!;
        var depthTracked = new DepthWrapper().Visit(normalised)!;
        var costed = new CostRewriter().Visit(depthTracked)!;

        var rewriter = new TraceRewriter(tree.GetText());
        var instrumented = rewriter.Visit(costed)!;

        return new InstrumentedSource(
            CSharpSyntaxTree.Create((CompilationUnitSyntax)instrumented, path: "instrumented.cs"),
            rewriter.Statements,
            charged.Select(call => call.ToSite()).ToList());
    }
}
