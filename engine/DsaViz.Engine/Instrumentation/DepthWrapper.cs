using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// Wraps every method, local function and constructor body in
/// <c>Tracer.Enter(); try { body } finally { Tracer.Exit(); }</c>.
///
/// This does three jobs. It measures how deep the recursion goes, which is the
/// stack-space half of space complexity. It stops runaway recursion before the real
/// stack overflows, because a StackOverflowException cannot be caught and kills the
/// engine outright. And it lets a returning call hand attribution back to the
/// statement that made it, so whatever the rest of that statement allocates is not
/// charged to the callee's last line.
///
/// try/finally is the one construct that guarantees Exit runs on every way out — a
/// return, a thrown exception, a goto. Iterators and async methods are skipped: their
/// bodies run in pieces across many calls, so entry and exit do not mean what they mean
/// for an ordinary call. Runs after <see cref="BraceNormalizer"/>, which guarantees every
/// body is a block.
/// </summary>
public sealed class DepthWrapper : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var rewritten = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        return rewritten.Body is { } body && CanWrap(rewritten.Modifiers, body)
            ? rewritten.WithBody(Wrap(body))
            : rewritten;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var rewritten = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        return rewritten.Body is { } body && CanWrap(rewritten.Modifiers, body)
            ? rewritten.WithBody(Wrap(body))
            : rewritten;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var rewritten = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        if (rewritten.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) return rewritten;
        return rewritten.Body is { } body ? rewritten.WithBody(Wrap(body)) : rewritten;
    }

    private static bool CanWrap(SyntaxTokenList modifiers, BlockSyntax body)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))) return false;

        // A yield belonging to a nested function does not make this one an iterator.
        bool isIterator = body
            .DescendantNodes(n => n is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax)
            .OfType<YieldStatementSyntax>()
            .Any();

        return !isIterator;
    }

    private static BlockSyntax Wrap(BlockSyntax body) => Block(
        ParseStatement(TraceCalls.Prefix + "Enter();"),
        TryStatement(
            body,
            List<CatchClauseSyntax>(),
            FinallyClause(Block(ParseStatement(TraceCalls.Prefix + "Exit();")))));
}
