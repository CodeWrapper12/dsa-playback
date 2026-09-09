using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Instrumentation;

public sealed record InstrumentedSource(SyntaxTree Tree, IReadOnlyList<StatementInfo> Statements);

/// <summary>
/// The three-stage pipeline that turns a user's solution into a measurable one.
/// Order is not negotiable: annotate while positions still mean the original file,
/// normalise braces so every executable position sits inside a block, then insert.
///
/// Dropping the normalisation step does not corrupt the program — it silently removes
/// braceless bodies from the measurement and disarms the execution budget. See
/// <see cref="BraceNormalizer"/>.
/// </summary>
public static class Instrumenter
{
    public static SyntaxTree Parse(string source, string path = "solution.cs")
        => CSharpSyntaxTree.ParseText(source, path: path);

    public static InstrumentedSource Instrument(SyntaxTree originalTree)
    {
        var annotated = new LineAnnotator(originalTree).Visit(originalTree.GetRoot())!;
        var normalised = new BraceNormalizer().Visit(annotated)!;

        var rewriter = new TraceRewriter(originalTree.GetText());
        var instrumented = rewriter.Visit(normalised)!;

        return new InstrumentedSource(
            CSharpSyntaxTree.Create((CompilationUnitSyntax)instrumented),
            rewriter.Statements);
    }
}
