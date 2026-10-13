using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Files as the LeetCode extension writes them: an @lc header, the problem in a comment,
/// no using lines, and TreeNode or ListNode left commented out. They must run as they
/// would on LeetCode, with their examples found and checked.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class LeetCodeExtensionTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "samples", "leetcode-extension", name);

    public static IEnumerable<object[]> Files() => new[]
    {
        new object[] { "1.two-sum.cs", "Solution.TwoSum" },
        new object[] { "104.maximum-depth-of-binary-tree.cs", "Solution.MaxDepth" },
        new object[] { "206.reverse-linked-list.cs", "Solution.ReverseList" },
        new object[] { "1.two-sum.py", "Solution.twoSum" },
        new object[] { "104.maximum-depth-of-binary-tree.py", "Solution.maxDepth" },
        new object[] { "206.reverse-linked-list.py", "Solution.reverseList" }
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_example_runs_and_matches(string file, string entry)
    {
        var response = JobRunner.Run(new JobRequest { File = Path(file), Entry = entry, AllExamples = true });
        var report = response.Examples;

        Assert.True(report is not null, string.Join("; ", response.Errors));
        Assert.True(report!.Status == RunStatus.Completed, string.Join("; ", report.Errors));
        Assert.NotEmpty(report.Runs);
        Assert.All(report.Runs, run => Assert.True(run.Matches == true, $"{run.Label}: expected {run.Expected}, got {run.Actual}. {run.Error}"));
    }

    [Theory]
    [InlineData("1.two-sum.cs")]
    [InlineData("104.maximum-depth-of-binary-tree.cs")]
    [InlineData("206.reverse-linked-list.cs")]
    public void The_method_is_offered(string file)
    {
        var entry = Assert.Single(EntryPointScanner.Scan(File.ReadAllText(Path(file))));
        Assert.True(entry.Measurable, entry.Reason);
        Assert.True(entry.Generatable);
    }

    [Fact]
    public void A_tree_solution_without_its_own_TreeNode_can_be_measured()
    {
        var result = Measurer.Measure(File.ReadAllText(Path("104.maximum-depth-of-binary-tree.cs")), "Solution.MaxDepth");
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);
    }

    [Fact]
    public void A_file_that_defines_its_own_TreeNode_keeps_it()
    {
        // Its own TreeNode, with properties rather than LeetCode's fields: supplying a
        // second one would make every use of the name ambiguous.
        const string source = """
            using System;
            public class TreeNode { public int val { get; set; } public TreeNode left { get; set; } public TreeNode right { get; set; } }
            public class Solution { public int Depth(TreeNode root) => root == null ? 0 : 1 + Math.Max(Depth(root.left), Depth(root.right)); }
            """;
        var analysis = SourceAnalysis.Create(source);

        Assert.False(analysis.HasErrors, string.Join("; ", analysis.Errors));
        Assert.Single(analysis.Prelude); // the using lines only
    }

    [Fact]
    public void Using_lines_written_out_as_well_are_not_an_error()
    {
        var analysis = SourceAnalysis.Create("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Solution { public int Count(int[] a) => new HashSet<int>(a).Where(x => x > 0).Count(); }
            """);
        Assert.False(analysis.HasErrors, string.Join("; ", analysis.Errors));
    }

    [Fact]
    public void An_error_in_the_file_still_names_its_own_line()
    {
        var analysis = SourceAnalysis.Create(File.ReadAllText(Path("1.two-sum.cs")).Replace("return new int[0];", "return nope;"));
        Assert.StartsWith("line 37,", Assert.Single(analysis.Errors));
    }
}
