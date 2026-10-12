using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// For the step-through build: wraps every value a method or local function returns as
/// <c>Returned&lt;T&gt;(value)</c>, so the recursion tree can show what each call gave back.
///
/// Done in two halves, like <see cref="StepVariables"/>. The type argument has to be
/// spelled out — <c>return null;</c> gives inference nothing to work with — and only the
/// semantic model knows it, so returned expressions are marked with their type on the
/// original tree. The wrapping happens last, after arrow bodies have become ordinary
/// <c>return</c> statements, which carry the mark with them.
/// </summary>
public static class ReturnValues
{
    public const string AnnotationKind = "dsaviz.return";

    /// <summary>Returned expressions on the original tree, each with the annotation to add.</summary>
    public static IEnumerable<(SyntaxNode Node, SyntaxAnnotation Annotation)> Mark(SyntaxNode root, SemanticModel model)
    {
        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? returned = node switch
            {
                ReturnStatementSyntax { Expression: { } expression } => expression,
                ArrowExpressionClauseSyntax arrow when arrow.Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                    => arrow.Expression,
                _ => null
            };
            if (returned is null || returned is RefExpressionSyntax) continue;

            // Only returns that leave a method or local function: a lambda's return is not
            // a call the recursion tree knows about.
            var owner = node.Ancestors().FirstOrDefault(a =>
                a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax
                    or AccessorDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax
                    or ConversionOperatorDeclarationSyntax);
            if (owner is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax)) continue;
            if (IsAsyncOrIterator(owner)) continue;

            var symbol = model.GetDeclaredSymbol(owner) as IMethodSymbol;
            if (symbol is null || symbol.ReturnsVoid || symbol.ReturnsByRef || symbol.ReturnsByRefReadonly) continue;
            if (!Nameable(symbol.ReturnType)) continue;

            yield return (returned, new SyntaxAnnotation(AnnotationKind,
                symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }
    }

    private static bool IsAsyncOrIterator(SyntaxNode owner)
    {
        var (modifiers, body) = owner switch
        {
            MethodDeclarationSyntax m => (m.Modifiers, (SyntaxNode?)m.Body),
            LocalFunctionStatementSyntax f => (f.Modifiers, f.Body),
            _ => (default, null)
        };
        if (modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) return true;
        return body is not null && body
            .DescendantNodes(n => n is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax)
            .OfType<YieldStatementSyntax>().Any();
    }

    /// <summary>A type that can be written in code: not anonymous, not ref-like, not in error.</summary>
    private static bool Nameable(ITypeSymbol type) => type switch
    {
        { IsAnonymousType: true } => false,
        { IsRefLikeType: true } => false,
        { TypeKind: TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer } => false,
        INamedTypeSymbol named => named.TypeArguments.All(Nameable),
        IArrayTypeSymbol array => Nameable(array.ElementType),
        _ => true
    };

    /// <summary>Wraps the marked expressions. Runs after every other pass.</summary>
    public sealed class Rewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var rewritten = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
            if (node.Expression?.GetAnnotations(AnnotationKind).FirstOrDefault()?.Data is not { } type)
                return rewritten;

            var call = ParseExpression($"{TraceCalls.Prefix}Returned<{type}>(default!)");
            var wrapped = ((InvocationExpressionSyntax)call).WithArgumentList(
                ArgumentList(SingletonSeparatedList(Argument(rewritten.Expression!.WithoutTrivia()))));
            return rewritten.WithExpression(wrapped.WithTriviaFrom(rewritten.Expression!));
        }
    }
}
