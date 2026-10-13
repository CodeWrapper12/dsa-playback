using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Compilation;

/// <summary>
/// What LeetCode's C# judge provides without it being written: the usual <c>using</c>
/// lines, and <c>TreeNode</c> and <c>ListNode</c>, which its templates leave commented out.
/// A file copied from LeetCode, or made by the LeetCode extension, compiles here exactly
/// as it does there. Compiled beside the student's file, never into it, so every line
/// number they see is still their own.
/// </summary>
public static class LeetCodePrelude
{
    public const string TreeNode = "TreeNode";
    public const string ListNode = "ListNode";

    private const string Usings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Text;
        """;

    // LeetCode's own definitions, as its templates show them in a comment.
    private static readonly Dictionary<string, string> Nodes = new(StringComparer.Ordinal)
    {
        [TreeNode] = """
            public class TreeNode {
                public int val;
                public TreeNode left;
                public TreeNode right;
                public TreeNode(int val = 0, TreeNode left = null, TreeNode right = null) {
                    this.val = val;
                    this.left = left;
                    this.right = right;
                }
            }
            """,
        [ListNode] = """
            public class ListNode {
                public int val;
                public ListNode next;
                public ListNode(int val = 0, ListNode next = null) {
                    this.val = val;
                    this.next = next;
                }
            }
            """
    };

    /// <summary>The node classes a file uses without defining, which are therefore supplied.</summary>
    public static IReadOnlyList<string> SuppliedNodes(SyntaxNode root)
    {
        var declared = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(type => type.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        var used = root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(name => name.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        return Nodes.Keys.Where(name => used.Contains(name) && !declared.Contains(name)).ToList();
    }

    /// <summary>The trees to compile beside <paramref name="tree"/>.</summary>
    public static IReadOnlyList<SyntaxTree> For(SyntaxTree tree)
    {
        var options = (CSharpParseOptions)tree.Options;
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(Usings, options, path: "leetcode-usings.cs") };
        foreach (var node in SuppliedNodes(tree.GetRoot()))
            trees.Add(CSharpSyntaxTree.ParseText(Nodes[node], options, path: $"leetcode-{node}.cs"));
        return trees;
    }
}
