using Microsoft.CodeAnalysis;

namespace DsaViz.Engine.Instrumentation;

/// <summary>
/// Statements are tagged with their position in the ORIGINAL source before any
/// rewriting happens. Rewriting moves nodes around, so reading a position afterwards
/// would report where the code ended up rather than where the user wrote it.
/// Annotations ride along on the nodes and survive every later transform.
/// </summary>
public static class LineAnnotations
{
    public const string Kind = "dsaviz.statement";

    public static SyntaxAnnotation For(int line, int start, int length)
        => new(Kind, $"{line}|{start}|{length}");

    public static (int Line, int Start, int Length)? Read(SyntaxNode node)
    {
        foreach (var annotation in node.GetAnnotations(Kind))
        {
            var parts = annotation.Data?.Split('|');
            if (parts is { Length: 3 }
                && int.TryParse(parts[0], out int line)
                && int.TryParse(parts[1], out int start)
                && int.TryParse(parts[2], out int length))
                return (line, start, length);
        }
        return null;
    }
}
