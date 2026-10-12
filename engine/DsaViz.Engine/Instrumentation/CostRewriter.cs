using DsaViz.Engine.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// Applies the wraps <see cref="CostCallAnalyzer"/> chose, so costed library calls are
/// charged as they actually execute.
///
/// Every wrap evaluates the expression it wraps exactly once and returns it unchanged,
/// so the rewritten call is the same call: the same receiver, the same overload, the
/// same short-circuiting. A call inside a loop condition is charged on every
/// evaluation, and a call on the far side of an && that never runs is never charged.
/// Charging at the statement level instead would get both of those wrong.
/// </summary>
public sealed class CostRewriter : CSharpSyntaxRewriter
{
    public const string AnnotationKind = "dsaviz.wrap";

    public static SyntaxAnnotation Annotation(WrapKind kind, int site, int costKind)
        => new(AnnotationKind, $"{kind}|{site}|{costKind}");

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (visited is null || !visited.HasAnnotations(AnnotationKind)) return visited;

        var annotations = visited.GetAnnotations(AnnotationKind).ToList();
        visited = visited.WithoutAnnotations(AnnotationKind);

        foreach (var annotation in annotations)
        {
            var parts = annotation.Data!.Split('|');
            var kind = Enum.Parse<WrapKind>(parts[0]);
            int site = int.Parse(parts[1]);
            int costKind = int.Parse(parts[2]);
            visited = Apply(visited, kind, site, costKind);
        }

        return visited;
    }

    private static SyntaxNode Apply(SyntaxNode node, WrapKind kind, int site, int costKind)
    {
        switch (kind)
        {
            case WrapKind.Through:
                return Call("Through", (ExpressionSyntax)node, Literal(site), Literal(costKind)).WithTriviaFrom(node);

            case WrapKind.ShiftIndex:
                return Call("ShiftIndex", (ExpressionSyntax)node, Literal(site)).WithTriviaFrom(node);

            case WrapKind.Units:
                return Call("Units", (ExpressionSyntax)node, Literal(site)).WithTriviaFrom(node);

            case WrapKind.BeforeAssignment:
                var assignment = (AssignmentExpressionSyntax)node;
                var measured = assignment.Left.WithoutTrivia();
                return assignment.WithRight(
                    Call("Before", measured, Literal(site), Literal(costKind), assignment.Right.WithoutTrivia())
                        .WithTriviaFrom(assignment.Right));

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static InvocationExpressionSyntax Call(string method, ExpressionSyntax first, params ExpressionSyntax[] rest)
        => InvocationExpression(
            ParseExpression(TraceCalls.Prefix + method),
            ArgumentList(SeparatedList(
                new[] { Argument(first.WithoutTrivia()) }.Concat(rest.Select(Argument)))));

    private static LiteralExpressionSyntax Literal(int value)
        => LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
}
