using System.Text.RegularExpressions;

namespace DsaViz.Engine.Python;

/// <summary>LeetCode's TreeNode, as the shape Python tree inputs are generated and checked in.</summary>
public sealed class PyTreeNode
{
    public int val;
    public PyTreeNode? left;
    public PyTreeNode? right;
}

/// <summary>LeetCode's ListNode, as the shape Python list inputs are generated and checked in.</summary>
public sealed class PyListNode
{
    public int val;
    public PyListNode? next;
}

/// <summary>
/// Maps a Python type hint onto the .NET type its values are built as. The inputs are
/// generated, typed and checked exactly as for C#, then handed to Python as text:
/// <c>List[int]</c> is built as an int[] and arrives as a list.
/// </summary>
public static class PythonTypes
{
    // A List[List[int]] is a grid of numbers or a graph's adjacency list; only its name says which.
    private static readonly string[] AdjacencyNames = { "graph", "adj", "adjacency", "neighbors", "neighbours", "rooms", "adjlist" };

    public static Type? Map(string hint, string parameterName)
    {
        string type = Normalise(hint);
        return type switch
        {
            "int" => typeof(int),
            "float" => typeof(double),
            "bool" => typeof(bool),
            "str" => typeof(string),
            "List[int]" => typeof(int[]),
            "List[float]" => typeof(double[]),
            "List[str]" => typeof(string[]),
            "List[bool]" => typeof(bool[]),
            "List[List[int]]" => AdjacencyNames.Contains(parameterName.ToLowerInvariant())
                ? typeof(List<IList<int>>)
                : typeof(int[][]),
            "List[List[str]]" => typeof(char[][]),
            "TreeNode" => typeof(PyTreeNode),
            "ListNode" => typeof(PyListNode),
            _ => null
        };
    }

    /// <summary>
    /// <c>Optional[List[int]]</c>, <c>list[int]</c> and <c>typing.List[int]</c> are all
    /// <c>List[int]</c>; a hint in quotes is the hint.
    /// </summary>
    public static string Normalise(string hint)
    {
        string type = Regex.Replace(hint, @"\s+", "").Trim('"', '\'');
        type = type.Replace("typing.", "");
        type = Regex.Replace(type, @"\blist\[", "List[");
        while (true)
        {
            var optional = Regex.Match(type, @"^Optional\[(.*)\]$");
            if (optional.Success) { type = optional.Groups[1].Value; continue; }
            var union = Regex.Match(type, @"^(.*)\|None$");
            if (union.Success) { type = union.Groups[1].Value; continue; }
            break;
        }
        return type;
    }

    /// <summary>How a value is rebuilt on the Python side: as the file's TreeNode, its ListNode, or as is.</summary>
    public static string KindOf(Type type)
        => type == typeof(PyTreeNode) ? "tree" : type == typeof(PyListNode) ? "list" : "value";

    /// <summary>A type a value can be given for, guessed from an example's value when there is no hint.</summary>
    public static Type? FromExample(string literal)
    {
        try
        {
            return DsaViz.Engine.Inputs.Literals.Parse(literal) switch
            {
                DsaViz.Engine.Inputs.LeetNumber { IsWhole: true } => typeof(int),
                DsaViz.Engine.Inputs.LeetNumber => typeof(double),
                DsaViz.Engine.Inputs.LeetBool => typeof(bool),
                DsaViz.Engine.Inputs.LeetString => typeof(string),
                DsaViz.Engine.Inputs.LeetArray array when array.Items.All(i => i is DsaViz.Engine.Inputs.LeetNumber { IsWhole: true }) => typeof(int[]),
                DsaViz.Engine.Inputs.LeetArray array when array.Items.All(i => i is DsaViz.Engine.Inputs.LeetString) => typeof(string[]),
                DsaViz.Engine.Inputs.LeetArray array when array.Items.All(i => i is DsaViz.Engine.Inputs.LeetArray inner &&
                    inner.Items.All(x => x is DsaViz.Engine.Inputs.LeetNumber { IsWhole: true })) => typeof(int[][]),
                DsaViz.Engine.Inputs.LeetArray array when array.Items.All(i => i is DsaViz.Engine.Inputs.LeetArray inner &&
                    inner.Items.All(x => x is DsaViz.Engine.Inputs.LeetString)) => typeof(string[][]),
                _ => null
            };
        }
        catch (DsaViz.Engine.Inputs.LiteralException)
        {
            return null;
        }
    }
}
