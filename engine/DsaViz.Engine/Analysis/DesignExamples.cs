using System.Text;
using System.Text.RegularExpressions;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using Microsoft.CodeAnalysis;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Examples of LeetCode design problems — LRU Cache, Min Stack — which LeetCode writes as
/// two lists under Input, the operations and then each one's arguments, and a list of
/// results under Output:
/// <code>
/// Input
/// ["LRUCache", "put", "get"]
/// [[2], [1, 1], [1]]
/// Output
/// [null, null, 1]
/// </code>
/// The first operation names the class. Every example is read as two values, one per
/// <see cref="Parameters"/> name, in the same LeetCode format a student types.
/// </summary>
public static class DesignExamples
{
    /// <summary>What a design run takes, in the order the boxes show them.</summary>
    public static readonly IReadOnlyList<string> Parameters = new[] { "operations", "arguments" };

    private static readonly Regex ExampleHeading = new(@"^\s*Example\s*(\d+)\s*:?", RegexOptions.IgnoreCase);
    private static readonly Regex InputLine = new(@"^\s*Input\s*:?\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex OutputLine = new(@"^\s*Output\s*:?\s*(.*)$", RegexOptions.IgnoreCase);

    /// <summary>A design example can span this many lines after its Input heading.</summary>
    private const int MaxLines = 30;

    public static ExampleSearch Find(SyntaxNode root, string className)
        => new(FromCommentLines(ExampleFinder.CommentLines(root), className).ToList(), Array.Empty<string>());

    /// <summary>The classes the design examples in these comment lines create.</summary>
    public static IReadOnlySet<string> ClassesNamed(IReadOnlyList<(string Text, int Line)> lines)
        => All(lines).Select(found => found.ClassName).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<FoundExample> FromCommentLines(IReadOnlyList<(string Text, int Line)> lines, string className)
        => All(lines).Where(found => found.ClassName == className).Select(found => found.Example);

    private static IEnumerable<(string ClassName, FoundExample Example)> All(IReadOnlyList<(string Text, int Line)> lines)
    {
        string? heading = null;
        int ordinal = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var (text, line) = lines[i];
            if (ExampleHeading.Match(text) is { Success: true } h) { heading = $"Example {h.Groups[1].Value}"; continue; }
            if (InputLine.Match(text) is not { Success: true } input) continue;

            string rest = input.Groups[1].Value.Trim();
            if (rest.Length > 0 && !rest.StartsWith('[')) continue;

            int end = i;
            var groups = Groups(lines, i, rest, 2, ref end);
            if (groups.Count != 2 || ClassOf(groups[0]) is not { } className) continue;

            string? expected = null;
            for (int k = end + 1; k < lines.Count && k <= end + 2; k++)
            {
                if (OutputLine.Match(lines[k].Text) is { Success: true } output)
                {
                    int outputEnd = k;
                    var result = Groups(lines, k, output.Groups[1].Value.Trim(), 1, ref outputEnd);
                    if (result.Count == 1) { expected = result[0]; end = outputEnd; }
                    break;
                }
                if (lines[k].Text.Length > 0) break;
            }

            ordinal++;
            yield return (className, new FoundExample(heading ?? $"Example {ordinal}", line, ExampleOrigin.Comment,
                new string?[] { groups[0], groups[1] }, Array.Empty<ExampleNote>()) { Expected = expected });
            heading = null;
            i = end;
        }
    }

    /// <summary>
    /// The first <paramref name="wanted"/> bracketed lists, starting with <paramref name="rest"/>
    /// and continuing on the lines after <paramref name="start"/>. Stops at a line that is
    /// neither empty nor part of a list, such as "Output" or "Explanation".
    /// </summary>
    private static List<string> Groups(IReadOnlyList<(string Text, int Line)> lines, int start, string rest, int wanted, ref int end)
    {
        var groups = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        char? quote = null;

        void Read(string text)
        {
            foreach (char c in text)
            {
                if (groups.Count == wanted) return;
                if (depth == 0 && c != '[') continue;
                current.Append(c);
                if (quote is { } q) { if (c == q) quote = null; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) { groups.Add(current.ToString()); current.Clear(); }
            }
            // A list continuing on the next line keeps a space where the line broke.
            if (depth > 0) current.Append(' ');
        }

        Read(rest);
        for (int k = start + 1; groups.Count < wanted && k < lines.Count && k <= start + MaxLines; k++)
        {
            string text = lines[k].Text.Trim();
            if (depth == 0 && text.Length > 0 && !text.StartsWith('[')) break;
            Read(text);
            end = k;
        }
        return groups;
    }

    /// <summary>The class the operations create: their first entry, when they are a list of text.</summary>
    private static string? ClassOf(string operations)
    {
        try
        {
            return Literals.Parse(operations) is LeetArray { Items: [LeetString first, ..] } array
                   && array.Items.All(item => item is LeetString)
                ? first.Value
                : null;
        }
        catch (LiteralException)
        {
            return null;
        }
    }
}
