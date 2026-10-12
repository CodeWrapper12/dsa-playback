using System.Text.RegularExpressions;
using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// Inserts a tracer <c>Hit(id)</c> call before every annotated statement, where the id
/// identifies the statement rather than its line.
///
/// Counting by statement instead of by line is what lets the report distinguish one
/// statement running 3,540 times from two statements running 1,770 times each — the
/// two are indistinguishable in a line total, and on a line like
/// <c>if (a[j] &gt; a[j+1]) { swap; }</c> the difference is the entire point.
///
/// Runs after <see cref="BraceNormalizer"/>, so every executable position is inside a
/// block or switch section and a statement can be inserted safely.
/// </summary>
public sealed class TraceRewriter : CSharpSyntaxRewriter
{
    private const int MaxSnippetLength = 80;

    private readonly SourceText _source;
    private readonly bool _stepping;
    private readonly List<StatementInfo> _statements = new();

    /// <param name="stepping">
    /// Emit Step calls carrying each statement's variables instead of plain Hit calls.
    /// Only the step-through build does this; the measurement build stays exactly as cheap.
    /// </param>
    public TraceRewriter(SourceText source, bool stepping = false)
    {
        _source = source;
        _stepping = stepping;
    }

    public IReadOnlyList<StatementInfo> Statements => _statements;

    private StatementSyntax HitCall(int line, int start, int length, SyntaxNode statement)
    {
        int id = _statements.Count;
        int column = _source.Lines.GetLinePosition(start).Character + 1;

        string snippet = Regex.Replace(_source.ToString(new TextSpan(start, length)), @"\s+", " ").Trim();
        if (snippet.Length > MaxSnippetLength) snippet = snippet[..(MaxSnippetLength - 1)] + "…";

        var step = _stepping ? StepVariables.Read(statement) : null;
        _statements.Add(new StatementInfo(id, line, column, length, snippet)
        {
            Method = step?.Method ?? "",
            Variables = step?.Variables.Select(v => v.Name).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>()
        });

        if (step is null) return ParseStatement($"{TraceCalls.Prefix}Hit({id});");

        // An explicit array, never params: a single object[] variable passed to a params
        // parameter would be taken as the array itself rather than as one value.
        string values = string.Join(", ", step.Value.Variables.Select(v => v.Expression));
        return ParseStatement($"{TraceCalls.Prefix}Step({id}, new object[] {{ {values} }});");
    }

    private SyntaxList<StatementSyntax> Interleave(SyntaxList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>(statements.Count * 2);
        foreach (var statement in statements)
        {
            if (LineAnnotations.Read(statement) is { } position)
                result.Add(HitCall(position.Line, position.Start, position.Length, statement));
            result.Add(statement);
        }
        return List(result);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var rewritten = (BlockSyntax)base.VisitBlock(node)!;
        return rewritten.WithStatements(Interleave(rewritten.Statements));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        // Switch sections hold statements directly rather than in a block, so they
        // need the same treatment or every case body would go unmeasured.
        var rewritten = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        return rewritten.WithStatements(Interleave(rewritten.Statements));
    }
}
