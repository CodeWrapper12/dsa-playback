using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// For the step-through build: works out, for every statement, which variables can be
/// read just before it runs, and records them on the statement for the rewriter to pass.
///
/// "Can be read" is precise and has to be. A variable must be in scope and definitely
/// assigned at that point, or reading it is a compile error; Roslyn's data-flow analysis
/// answers exactly that question, so the generated code always compiles. Ref-like values
/// such as Span cannot be boxed and are left out. In an instance method the object's
/// fields are included too — that is where a Solution class keeps its memo table.
/// </summary>
public static class StepVariables
{
    public const string AnnotationKind = "dsaviz.vars";

    public sealed record Entry(string Name, bool IsField)
    {
        public string Expression => IsField ? "this." + Name : Name;
    }

    public static SyntaxAnnotation Annotation(string method, IEnumerable<Entry> variables)
        => new(AnnotationKind, method + "\t" + string.Join(",", variables.Select(v => (v.IsField ? "F:" : "L:") + v.Name)));

    public static (string Method, IReadOnlyList<Entry> Variables)? Read(SyntaxNode node)
    {
        var annotation = node.GetAnnotations(AnnotationKind).FirstOrDefault();
        if (annotation?.Data is not { } data) return null;
        var parts = data.Split('\t');
        var variables = parts[1].Length == 0
            ? Array.Empty<Entry>()
            : parts[1].Split(',').Select(v => new Entry(v[2..], v.StartsWith("F:"))).ToArray();
        return (parts[0], variables);
    }

    public static SyntaxNode Annotate(SyntaxNode root, SemanticModel model)
    {
        // A rewritten root would make every data-flow query throw, and a quiet fallback
        // would record steps with no variables at all. Refuse instead.
        if (root.SyntaxTree != model.SyntaxTree)
            throw new InvalidOperationException("Step variables must be read from the tree the semantic model was built for.");

        var statements = root.DescendantNodes().OfType<StatementSyntax>().Where(s => s is not BlockSyntax).ToList();
        var annotations = new Dictionary<SyntaxNode, SyntaxAnnotation>();

        foreach (var statement in statements)
        {
            var enclosing = model.GetEnclosingSymbol(statement.SpanStart) as IMethodSymbol;
            string method = enclosing switch
            {
                { MethodKind: MethodKind.AnonymousFunction } lambda => "lambda in " + (lambda.ContainingSymbol?.Name ?? "?"),
                { } symbol => symbol.Name,
                _ => "?"
            };

            annotations[statement] = Annotation(method, VariablesAt(statement, enclosing, model));
        }

        return root.ReplaceNodes(annotations.Keys, (original, current) => current.WithAdditionalAnnotations(annotations[original]));
    }

    private static IEnumerable<Entry> VariablesAt(StatementSyntax statement, IMethodSymbol? enclosing, SemanticModel model)
    {
        // Some statement kinds are not a valid data-flow region; for those Succeeded is false.
        var flow = model.AnalyzeDataFlow(statement);
        if (flow is null || !flow.Succeeded) yield break;

        var assigned = new HashSet<ISymbol>(flow.DefinitelyAssignedOnEntry, SymbolEqualityComparer.Default);

        var inScope = model.LookupSymbols(statement.SpanStart)
            .Where(s => s is ILocalSymbol or IParameterSymbol)
            .Where(s => assigned.Contains(s) && Capturable(TypeOf(s)))
            .OrderBy(s => s.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);

        foreach (var symbol in inScope)
            yield return new Entry(symbol.Name, IsField: false);

        // `this` is only reachable from a non-static method, local function or lambda.
        if (enclosing is { IsStatic: false } && enclosing.ContainingType is { } type)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                if (!field.IsStatic && !field.IsConst && !field.IsImplicitlyDeclared && Capturable(field.Type))
                    yield return new Entry(field.Name, IsField: true);
        }
    }

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => null
    };

    private static bool Capturable(ITypeSymbol? type)
        => type is not null && !type.IsRefLikeType && type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error);
}
