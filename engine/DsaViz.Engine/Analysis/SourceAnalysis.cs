using DsaViz.Engine.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// The user's file, parsed and bound once, shared by everything that reads it.
///
/// Errors are taken from this compilation of the original text, never from the
/// instrumented one. Instrumentation inserts statements, which moves every line below
/// the first insertion, so an error reported from the instrumented tree points at the
/// wrong line of the user's file.
/// </summary>
public sealed class SourceAnalysis
{
    private readonly Lazy<CostCallAnalysis> _costCalls;

    private SourceAnalysis(string source, SyntaxTree tree, CSharpCompilation compilation)
    {
        Source = source;
        Tree = tree;
        Compilation = compilation;
        Model = compilation.GetSemanticModel(tree);

        Errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(Describe)
            .ToList();

        _costCalls = new Lazy<CostCallAnalysis>(() => CostCallAnalyzer.Analyze(Tree, Model));
    }

    public string Source { get; }
    public SyntaxTree Tree { get; }
    public CSharpCompilation Compilation { get; }
    public SemanticModel Model { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool HasErrors => Errors.Count > 0;

    public CostCallAnalysis CostCalls => _costCalls.Value;

    public static SourceAnalysis Create(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "solution.cs");
        return new SourceAnalysis(source, tree, InMemoryCompiler.CreateCompilation(tree, "Analysis"));
    }

    /// <summary>"line 12: CS0103 The name 'x' does not exist" — positions in the user's own file.</summary>
    private static string Describe(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"line {position.Line + 1}, column {position.Character + 1}: {diagnostic.Id} {diagnostic.GetMessage()}";
    }
}
