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

        foreach (var entry in EntryPointScanner.Scan(source))
        {
            var outcome = Runner.Compile(source, entry.Qualified, instrument: false);
            Assert.True(outcome.Success, $"{entry.Qualified}: {string.Join("; ", outcome.Errors)}");
            using var solution = outcome.Solution!;

            bool engineAccepts;
            try { ParameterBinder.Describe(solution.Parameters); engineAccepts = true; }
            catch (UnsupportedSignatureException) { engineAccepts = false; }

            Assert.True(engineAccepts == entry.Measurable,
                $"{entry.Signature}: picker says {(entry.Measurable ? "measurable" : "not measurable")}, engine says the opposite");
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
    public void A_class_that_cannot_be_constructed_says_why()
    {
        var entry = Assert.Single(EntryPointScanner.Scan(
            "public class Solution { public Solution(int seed) { } public int Go(int[] values) => values.Length; }"));

        Assert.False(entry.Measurable);
        Assert.Contains("parameterless constructor", entry.Reason);
    }
}
