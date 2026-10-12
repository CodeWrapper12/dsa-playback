using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// Wraps single-statement bodies in blocks, and converts expression-bodied methods,
/// local functions and constructors into block bodies.
///
/// This must run before any statement is inserted. Its purpose is measurement
/// coverage, not semantic safety: <see cref="TraceRewriter"/> only inserts inside
/// blocks and switch sections, so it structurally cannot split a braceless body away
/// from the statement controlling it. What it can do is miss that body entirely.
///
/// Without this pass, <c>for (int i = 0; i &lt; n; i++) a[i] = i;</c> has a body that
/// lives outside any block, so it receives no tracer call and its cost silently
/// disappears. Measured on the bubble-sort sample, skipping this pass left the program
/// behaving identically while the inner loop line vanished from the report and the
/// innermost line under-counted by half. A silently cheap O(n^2) is a worse outcome
/// for this tool than a crash, because nothing looks wrong.
///
/// It also underwrites the execution budget. A braceless <c>while (true) i++;</c> has
/// no instrumented statement in its body, so the tracer's Hit never runs, the budget never
/// trips, and the loop runs forever — which is exactly what happened when this pass
/// was disabled during testing.
/// </summary>
public sealed class BraceNormalizer : CSharpSyntaxRewriter
{
    private static StatementSyntax Blockify(StatementSyntax statement)
        => statement is BlockSyntax ? statement : Block(statement);

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var rewritten = (IfStatementSyntax)base.VisitIfStatement(node)!;
        rewritten = rewritten.WithStatement(Blockify(rewritten.Statement));

        // `else if` chains are left alone: the else's statement is itself an if, which
        // already got its own braces. Wrapping it would only add nesting noise.
        if (rewritten.Else is { } elseClause && elseClause.Statement is not IfStatementSyntax)
            rewritten = rewritten.WithElse(elseClause.WithStatement(Blockify(elseClause.Statement)));

        return rewritten;
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var n = (ForStatementSyntax)base.VisitForStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var n = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var n = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var n = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        var n = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
        // A nested `using (a) using (b) { }` becomes `using (a) { using (b) { } }`,
        // which is the same program.
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        var n = (LockStatementSyntax)base.VisitLockStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
    {
        var n = (FixedStatementSyntax)base.VisitFixedStatement(node)!;
        return n.WithStatement(Blockify(n.Statement));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var origin = OriginOf(node.ExpressionBody);
        var rewritten = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (rewritten.ExpressionBody is null) return rewritten;

        return rewritten
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(ToBlock(rewritten.ExpressionBody.Expression, ReturnsVoid(rewritten.ReturnType), origin));
    }

    /// <summary>
    /// Local functions get the same treatment as methods, because recursive helpers are
    /// very often written as arrow-bodied local functions — <c>int Dfs(Node n) =&gt; ...</c>.
    /// Left as an expression body, such a function contains no statement to instrument,
    /// so its recursion was invisible and a plainly O(n) solution measured as O(1).
    /// </summary>
    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var origin = OriginOf(node.ExpressionBody);
        var rewritten = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (rewritten.ExpressionBody is null) return rewritten;

        return rewritten
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(ToBlock(rewritten.ExpressionBody.Expression, ReturnsVoid(rewritten.ReturnType), origin));
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var origin = OriginOf(node.ExpressionBody);
        var rewritten = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        if (rewritten.ExpressionBody is null) return rewritten;

        return rewritten
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(ToBlock(rewritten.ExpressionBody.Expression, returnsVoid: true, origin));
    }

    /// <summary>
    /// Captured before rewriting, so the synthesised statement still points at where the
    /// user's expression actually lives.
    /// </summary>
    private static (int Line, int Start, int Length)? OriginOf(ArrowExpressionClauseSyntax? body)
    {
        if (body is null) return null;
        var span = body.Expression.Span;
        int line = body.SyntaxTree.GetLineSpan(span).StartLinePosition.Line + 1;
        return (line, span.Start, span.Length);
    }

    private static bool ReturnsVoid(TypeSyntax returnType)
        => returnType is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static BlockSyntax ToBlock(ExpressionSyntax expression, bool returnsVoid, (int Line, int Start, int Length)? origin)
    {
        StatementSyntax statement = returnsVoid ? ExpressionStatement(expression) : ReturnStatement(expression);

        if (origin is { } o)
            statement = statement.WithAdditionalAnnotations(LineAnnotations.For(o.Line, o.Start, o.Length));

        return Block(statement);
    }
}
