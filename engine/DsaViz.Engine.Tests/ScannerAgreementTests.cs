using DsaViz.Engine.Analysis;
using DsaViz.Engine.Inputs;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// The editor's method picker reads syntax; the engine binds real types. If they
/// disagreed, the picker would offer a method that then fails, or hide one that works.
/// </summary>
public class ScannerAgreementTests
{
    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples", "leetcode"), "*.cs")
            .Select(path => new object[] { "leetcode/" + Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_picker_and_the_engine_agree_on_what_can_be_measured(string sample)
    {
        string source = Samples.Read(sample);

        foreach (var entry in EntryPointScanner.Scan(source).Where(e => !e.Design))
        {
            var outcome = Runner.Compile(source, entry.Qualified, instrument: false);
            Assert.True(outcome.Success, $"{entry.Qualified}: {string.Join("; ", outcome.Errors)}");
            using var solution = outcome.Solution!;

            bool engineAccepts;
            try { ParameterBinder.Describe(solution.Parameters); engineAccepts = true; }
            catch (UnsupportedSignatureException) { engineAccepts = false; }

            // A method that runs only on an example's values is offered but not measured.
            bool pickerMeasures = entry.Measurable && entry.Generatable;
            Assert.True(engineAccepts == pickerMeasures,
                $"{entry.Signature}: picker says {(pickerMeasures ? "measurable" : "not measurable")}, engine says the opposite");
        }
    }

    [Fact]
    public void Instance_methods_on_a_leetcode_solution_are_offered()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(Samples.Read("leetcode/MaxDepth.cs")));

        Assert.Equal("Solution.MaxDepth", entry.Qualified);
        Assert.True(entry.Measurable);
        Assert.Equal("TreeNode", entry.Parameters.Single().Type);
    }

    [Fact]
    public void A_class_that_cannot_be_constructed_is_a_design_problem_and_says_what_it_needs()
    {
        // Created with arguments and then called: LeetCode's design-problem shape, as in
        // Random Pick Index, whose class is even called Solution.
        var entry = Assert.Single(EntryPointScanner.Scan(
            "public class Solution { public Solution(int seed) { } public int Go(int[] values) => values.Length; }"));

        Assert.True(entry.Design);
        Assert.False(entry.Measurable);
        Assert.Contains("Add LeetCode's example as a comment", entry.Reason);
    }

    [Fact]
    public void A_class_with_only_a_private_constructor_still_says_why()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(
            "public class Solution { private Solution() { } public int Go(int[] values) => values.Length; }"));

        Assert.False(entry.Design);
        Assert.Contains("parameterless constructor", entry.Reason);
    }

    [Fact]
    public void A_type_that_cannot_be_generated_runs_when_an_example_gives_it()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(
            """
            using System.Collections.Generic;
            public class Solution
            {
                // Input: prices = [1.5,2.0], words = [["a"],["b","c"]], c = "x", k = 2
                public double Go(double[] prices, IList<IList<string>> words, char c, int k) => 0;
            }
            """));

        Assert.True(entry.Measurable, entry.Reason);
        Assert.False(entry.Generatable);
        Assert.All(entry.Parameters, p => Assert.True(p.Supported, p.Name));
    }

    [Fact]
    public void A_call_with_literal_arguments_also_gives_the_values()
    {
        var entry = EntryPointScanner.Scan(
            """
            public class Solution
            {
                public static double Half(double x) => x / 2;
                public static void Main() { Half(3.5); }
            }
            """).Single(e => e.Name == "Half");

        Assert.True(entry.Measurable, entry.Reason);
    }

    [Fact]
    public void Without_an_example_it_says_to_add_one()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(
            "public class Solution { public double Half(double x) => x / 2; }"));

        Assert.False(entry.Measurable);
        Assert.Contains("Give it a value in an example comment or a call", entry.Reason);
    }

    [Fact]
    public void A_type_no_example_can_spell_is_still_refused()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(
            """
            using System.Collections.Generic;
            public class Solution
            {
                // Input: counts = {"a": 1}
                public int Go(Dictionary<string, int> counts) => 0;
            }
            """));

        Assert.False(entry.Measurable);
        Assert.Contains("neither generate nor read from an example", entry.Reason);
    }

    [Fact]
    public void Generatable_methods_are_marked_so()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(Samples.Read("leetcode/MaxDepth.cs")));
        Assert.True(entry.Generatable);
    }
}
