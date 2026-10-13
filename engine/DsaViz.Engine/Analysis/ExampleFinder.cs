using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Finds input values already written in a solution file, so a student never has to type
/// an example that is sitting on the screen. Three places are read:
///
/// <list type="bullet">
/// <item>LeetCode example comments — <c>Input: nums = [2,7,11,15], target = 9</c> — with
/// their <c>Output:</c>, which becomes an expected result to check.</item>
/// <item>Calls to the method with literal arguments, in <c>Main</c> or top-level statements.</item>
/// <item>Variables given literal values before such a call, followed back from the argument.</item>
/// </list>
///
/// Every value comes out as LeetCode-format text, the same form a student types, so one
/// parser reads all of them. Anything that is not a literal is reported with the reason
/// rather than guessed at.
/// </summary>
public static class ExampleFinder
{
    private static readonly Regex ExampleHeading = new(@"^\s*Example\s*(\d+)\s*:?", RegexOptions.IgnoreCase);
    private static readonly Regex InputLine = new(@"^\s*Input\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex OutputLine = new(@"^\s*Output\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyOrder = new(@"\bin any order\b", RegexOptions.IgnoreCase);
    private static readonly Regex NamedValue = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", RegexOptions.Singleline);

    /// <summary>An example comment whose Input spans lines is read for at most this many.</summary>
    private const int MaxContinuationLines = 20;

    public static ExampleSearch Find(string source, string entry, IReadOnlyList<string> parameterNames)
        => Find(CSharpSyntaxTree.ParseText(source).GetRoot(), entry, parameterNames);

    public static ExampleSearch Find(SyntaxNode root, string entry, IReadOnlyList<string> parameterNames)
    {
        string methodName = entry.Contains('.') ? entry[(entry.LastIndexOf('.') + 1)..] : entry;
        var notes = new List<string>();

        var found = new List<FoundExample>();
        found.AddRange(FromCommentLines(CommentLines(root), parameterNames));
        found.AddRange(FromCalls(root, methodName, parameterNames, notes));
        return Combine(found, notes);
    }

    /// <summary>
    /// Examples from every source, in file order. The same values found twice — an example
    /// comment and a call that copies it — are one example; the comment is kept, because it
    /// carries the expected output.
    /// </summary>
    public static ExampleSearch Combine(IEnumerable<FoundExample> found, IReadOnlyList<string> notes)
    {
        var distinct = new List<FoundExample>();
        foreach (var example in found.OrderBy(e => e.Line))
        {
            // Only complete examples are compared: two calls that each pass something
            // unreadable look alike, but are different calls with different reasons.
            bool complete = example.Values.All(v => v is not null);
            var twin = complete ? distinct.FindIndex(d => d.Values.SequenceEqual(example.Values)) : -1;
            if (twin < 0) { distinct.Add(example); continue; }
            if (distinct[twin].Expected is null && example.Expected is not null) distinct[twin] = example;
        }

        return new ExampleSearch(distinct, notes);
    }

    // ---- LeetCode example comments ----------------------------------------------------

    /// <summary>
    /// LeetCode examples in comment lines, already stripped of their comment markers. Any
    /// language's comments are read the same way: C# comments come from Roslyn, Python's
    /// comments and docstrings from the Python runner.
    /// </summary>
    public static IEnumerable<FoundExample> FromCommentLines(
        IReadOnlyList<(string Text, int Line)> lines, IReadOnlyList<string> parameterNames)
    {
        int ordinal = 0;

        // "You may return the answer in any order" — LeetCode says it once, in the problem
        // statement, and it applies to every example's Output.
        bool anyOrder = lines.Any(l => AnyOrder.IsMatch(l.Text));
        string? heading = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var (text, line) = lines[i];
            if (ExampleHeading.Match(text) is { Success: true } h) { heading = $"Example {h.Groups[1].Value}"; continue; }
            if (InputLine.Match(text) is not { Success: true } input) continue;

            // An Input that opens more brackets than it closes continues on the next lines.
            var body = new StringBuilder(input.Groups[1].Value);
            int consumed = 0;
            while (!Balanced(body.ToString()) && i + 1 < lines.Count && consumed++ < MaxContinuationLines)
                body.Append(' ').Append(lines[++i].Text.Trim());

            // The Output line follows the Input, sometimes after a blank comment line.
            string? expected = null;
            for (int k = i + 1; k < lines.Count && k <= i + 2; k++)
            {
                if (OutputLine.Match(lines[k].Text) is { Success: true } output) { expected = output.Groups[1].Value.Trim(); break; }
                if (lines[k].Text.Length > 0) break;
            }

            ordinal++;
            var example = FromInputText(body.ToString(), parameterNames, heading ?? $"Example {ordinal}", line, expected);
            heading = null;
            if (example is not null) yield return example with { AnyOrder = anyOrder };
        }
    }

    /// <summary>
    /// The comment text of the file, one entry per line with its 1-based line number, the
    /// comment markers removed.
    /// </summary>
    internal static List<(string Text, int Line)> CommentLines(SyntaxNode root)
    {
        var lines = new List<(string, int)>();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;

            int firstLine = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var parts = trivia.ToFullString().Split('\n');
            for (int k = 0; k < parts.Length; k++)
            {
                string text = parts[k].TrimEnd('\r').Trim();
                text = Regex.Replace(text, @"^(///|//|/\*\*|/\*|\*/|\*)\s?", "");
                if (text.EndsWith("*/")) text = text[..^2];
                lines.Add((text, firstLine + k));
            }
        }
        return lines;
    }

    private static FoundExample? FromInputText(
        string text, IReadOnlyList<string> parameterNames, string label, int line, string? expected)
    {
        var segments = SplitTopLevel(text);
        var values = new string?[parameterNames.Count];
        var notes = new List<ExampleNote>();
        var named = segments.Select(s => NamedValue.Match(s)).ToList();

        if (named.All(m => m.Success))
        {
            int matched = 0;
            foreach (var match in named)
            {
                int index = IndexOf(parameterNames, match.Groups[1].Value);
                if (index < 0) continue;
                values[index] = match.Groups[2].Value.Trim();
                matched++;
            }

            // Names that match no parameter at all mean the comment describes some other
            // method — a second problem in the same file — not this one.
            if (matched == 0) return null;
        }
        else if (segments.Count == parameterNames.Count)
        {
            // "Input: [2,7,11,15], 9" — unnamed, so taken in order.
            for (int k = 0; k < segments.Count; k++) values[k] = segments[k].Trim();
        }
        else
        {
            return null;
        }

        for (int k = 0; k < values.Length; k++)
        {
            if (values[k] is null)
                notes.Add(new ExampleNote(parameterNames[k], $"{label} gives no value for '{parameterNames[k]}'."));
        }

        return new FoundExample(label, line, ExampleOrigin.Comment, values, notes) { Expected = expected };
    }

    /// <summary>Splits on commas that are outside brackets and quotes.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        char? quote = null;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote is { } q)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < text.Length) current.Append(text[++i]);
                else if (c == q) quote = null;
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static bool Balanced(string text)
    {
        int depth = 0;
        char? quote = null;
        foreach (char c in text)
        {
            if (quote is { } q) { if (c == q) quote = null; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
        }
        return depth <= 0;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        // LeetCode's examples keep the problem's names — numRows — where Rust writes num_rows.
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i].Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- calls with literal arguments --------------------------------------------------

    private static IEnumerable<FoundExample> FromCalls(
        SyntaxNode root, string methodName, IReadOnlyList<string> parameterNames, List<string> notes)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? called = call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null
            };
            if (called != methodName) continue;

            // A method calling itself is recursion, not an example of how to call it.
            if (call.Ancestors().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == methodName)) continue;
            if (call.ArgumentList.Arguments.Count != parameterNames.Count) continue;

            int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var values = new string?[parameterNames.Count];
            var callNotes = new List<ExampleNote>();

            for (int i = 0; i < call.ArgumentList.Arguments.Count; i++)
            {
                var argument = call.ArgumentList.Arguments[i];
                int index = argument.NameColon is { } nameColon
                    ? IndexOf(parameterNames, nameColon.Name.Identifier.Text)
                    : i;
                if (index < 0) continue;

                var (value, reason) = Resolve(argument.Expression, call);
                if (value is not null) values[index] = value;
                else callNotes.Add(new ExampleNote(parameterNames[index], $"Line {line} passes {reason}"));
            }

            if (values.All(v => v is null))
            {
                notes.Add($"The call on line {line} was not used: none of its arguments are values written in the code.");
                continue;
            }

            yield return new FoundExample($"Call on line {line}", line, ExampleOrigin.Call, values, callNotes);
        }
    }

    /// <summary>
    /// An argument as LeetCode-format text, or the reason it cannot be read. Only values
    /// written out in the code count; anything computed is left to the run.
    /// </summary>
    private static (string? Value, string? Reason) Resolve(ExpressionSyntax expression, SyntaxNode call)
    {
        if (expression is IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.Text;
            var declared = FindDeclaration(name, call);
            if (declared is null)
                return (null, $"`{name}`, which is not given a value in the code before the call.");
            if (declared.Initializer is null)
                return (null, $"`{name}`, which is declared without a value.");
            if (ChangedBetween(name, declared, call))
                return (null, $"`{name}`, which changes between where it is declared and the call.");

            var (value, _) = Resolve(declared.Initializer.Value, call);
            return value is not null
                ? (value, null)
                : (null, $"`{name}`, which is set to `{Shorten(declared.Initializer.Value)}`, not a value written out.");
        }

        var literal = Literal(expression);
        return literal is not null
            ? (literal, null)
            : (null, $"`{Shorten(expression)}`, which is not a value written out.");
    }

    private static string? Literal(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return literal.Kind() switch
                {
                    SyntaxKind.NumericLiteralExpression => Convert.ToString(literal.Token.Value, CultureInfo.InvariantCulture),
                    SyntaxKind.StringLiteralExpression => Quote((string)literal.Token.Value!),
                    SyntaxKind.CharacterLiteralExpression => Quote(((char)literal.Token.Value!).ToString()),
                    SyntaxKind.TrueLiteralExpression => "true",
                    SyntaxKind.FalseLiteralExpression => "false",
                    SyntaxKind.NullLiteralExpression => "null",
                    _ => null
                };

            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } negative
                when negative.Operand is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression }:
                return "-" + Literal(negative.Operand);

            case ParenthesizedExpressionSyntax parenthesized:
                return Literal(parenthesized.Expression);

            case CastExpressionSyntax cast:
                return Literal(cast.Expression);

            // "abc".ToCharArray() — the usual way to write a char[] by hand.
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ToCharArray" } access } call
                when call.ArgumentList.Arguments.Count == 0:
                return Literal(access.Expression);

            case ArrayCreationExpressionSyntax array:
                if (array.Initializer is not null) return List(array.Initializer.Expressions);
                // new int[0] is an empty array; new int[n] is not something to read.
                return array.Type.RankSpecifiers.Count == 1 &&
                       array.Type.RankSpecifiers[0].Sizes is [LiteralExpressionSyntax { Token.ValueText: "0" }]
                    ? "[]"
                    : null;

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return List(implicitArray.Initializer.Expressions);

            case InitializerExpressionSyntax initializer:
                return List(initializer.Expressions);

            case CollectionExpressionSyntax collection:
                return collection.Elements.All(e => e is ExpressionElementSyntax)
                    ? List(collection.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression))
                    : null;

            // new List<int> { 1, 2, 3 }, or new List<int>() with no items.
            case BaseObjectCreationExpressionSyntax creation when IsListType(creation):
                if (creation.Initializer is { } items) return List(items.Expressions);
                return creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0 ? "[]" : null;

            default:
                return null;
        }
    }

    private static bool IsListType(BaseObjectCreationExpressionSyntax creation) => creation switch
    {
        ObjectCreationExpressionSyntax { Type: GenericNameSyntax generic } => generic.Identifier.Text == "List",
        ObjectCreationExpressionSyntax { Type: QualifiedNameSyntax { Right: GenericNameSyntax generic } } => generic.Identifier.Text == "List",
        // new() { 1, 2, 3 } — the target type decides, and only a collection takes items like this.
        ImplicitObjectCreationExpressionSyntax { Initializer.RawKind: (int)SyntaxKind.CollectionInitializerExpression } => true,
        _ => false
    };

    private static string? List(IEnumerable<ExpressionSyntax> items)
    {
        var parts = new List<string>();
        foreach (var item in items)
        {
            var value = Literal(item);
            if (value is null) return null;
            parts.Add(value);
        }
        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>The nearest declaration of a local visible from the call and written before it.</summary>
    private static VariableDeclaratorSyntax? FindDeclaration(string name, SyntaxNode call)
    {
        return call.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Identifier.Text == name && d.SpanStart < call.SpanStart)
            .Where(d => Scope(d) is { } scope && scope.Span.Contains(call.Span))
            .OrderByDescending(d => d.SpanStart)
            .FirstOrDefault();
    }

    /// <summary>Where a local is visible: its block, or the whole file for top-level statements.</summary>
    private static SyntaxNode? Scope(VariableDeclaratorSyntax declarator)
        => declarator.Parent?.Parent is LocalDeclarationStatementSyntax statement
            ? statement.Parent is GlobalStatementSyntax global ? global.Parent : statement.Parent
            : null;

    /// <summary>
    /// Whether anything between the declaration and the call could change the variable:
    /// an assignment to it, an increment, or passing it by ref or out.
    /// </summary>
    private static bool ChangedBetween(string name, VariableDeclaratorSyntax declared, SyntaxNode call)
    {
        var scope = Scope(declared)!;
        return scope.DescendantNodes()
            .Where(n => n.SpanStart > declared.Span.End && n.SpanStart < call.SpanStart)
            .Any(n => n switch
            {
                AssignmentExpressionSyntax assignment => Names(assignment.Left, name),
                PostfixUnaryExpressionSyntax postfix => Names(postfix.Operand, name),
                PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                                                        prefix.IsKind(SyntaxKind.PreDecrementExpression)
                    => Names(prefix.Operand, name),
                ArgumentSyntax { RefKindKeyword.RawKind: not 0 } argument => Names(argument.Expression, name),
                _ => false
            });
    }

    /// <summary>The variable itself, or an element of it — nums[0] = 5 changes nums too.</summary>
    private static bool Names(ExpressionSyntax target, string name) => target switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == name,
        ElementAccessExpressionSyntax element => Names(element.Expression, name),
        _ => false
    };

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value)
            text.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", _ => c.ToString() });
        return text.Append('"').ToString();
    }

    private static string Shorten(SyntaxNode node)
    {
        const int MaxShown = 40;
        string text = Regex.Replace(node.ToString(), @"\s+", " ");
        return text.Length <= MaxShown ? text : text[..(MaxShown - 1)] + "…";
    }
}
