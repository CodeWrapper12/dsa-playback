using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DsaViz.Engine.Instrumentation;

/// <summary>Tags every statement with its position in the original source.</summary>
public sealed class LineAnnotator : CSharpSyntaxRewriter
{
    private readonly SyntaxTree _originalTree;

    public LineAnnotator(SyntaxTree originalTree) => _originalTree = originalTree;

    /// <summary>
    /// A control-flow statement's full span swallows its entire body, so highlighting
    /// or quoting it would cover dozens of lines that are separately measured. The
    /// header — <c>for (int j = 0; j &lt; n; j++)</c> — is the part that actually
    /// belongs to this statement's own cost.
    /// </summary>
    private static TextSpan HeaderSpan(StatementSyntax statement) => statement switch
    {
        IfStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        ForStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        ForEachStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        CommonForEachStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        WhileStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        SwitchStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        UsingStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        LockStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        FixedStatementSyntax n => TextSpan.FromBounds(n.SpanStart, n.CloseParenToken.Span.End),
        DoStatementSyntax n => n.DoKeyword.Span,
        TryStatementSyntax n => n.TryKeyword.Span,
        _ => statement.Span
    };

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        // Blocks are containers, not executable statements — instrumenting them would
        // double-count the statements they hold.
        if (node is not StatementSyntax statement || node is BlockSyntax)
            return base.Visit(node);

        var span = HeaderSpan(statement);
        int line = _originalTree.GetLineSpan(span).StartLinePosition.Line + 1;

        return base.Visit(node)
            ?.WithAdditionalAnnotations(LineAnnotations.For(line, span.Start, span.Length));
    }
}
