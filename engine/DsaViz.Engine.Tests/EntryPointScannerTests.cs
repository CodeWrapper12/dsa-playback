using DsaViz.Engine.Analysis;
using Xunit;

namespace DsaViz.Engine.Tests;

public class EntryPointScannerTests
{
    [Fact]
    public void Public_static_methods_are_listed_with_their_signature()
    {
        var entries = EntryPointScanner.Scan(Samples.Read("BinarySearchParamSample.cs"));

        var search = Assert.Single(entries);
        Assert.Equal("Search", search.Name);
        Assert.Equal("BinarySearchParamSample", search.DeclaringType);
        Assert.Equal("BinarySearchParamSample.Search(int[] sorted, int target)", search.Signature);
        Assert.True(search.Measurable);
    }

    [Fact]
    public void Private_helpers_are_not_offered_as_entry_points()
    {
        // MergeSort's recursion lives in private helpers; offering them would invite
        // the user to measure something that cannot be called from outside.
        var names = EntryPointScanner.Scan(Samples.Read("MergeSortSample.cs")).Select(e => e.Name);

        Assert.Contains("Sort", names);
        Assert.DoesNotContain("SortRange", names);
        Assert.DoesNotContain("Merge", names);
    }

    [Fact]
    public void A_method_with_an_ungeneratable_parameter_is_listed_but_marked()
    {
        // Hiding it would leave the user wondering why their method is missing; the
        // useful answer is to show it with the reason attached.
        var entries = EntryPointScanner.Scan(
            """
            public static class Mixed
            {
                public class Node { public int Value; }
                public static int Walk(Node node) => 1;
                public static int Total(int[] values) => values.Length;
            }
            """);

        var walk = entries.Single(e => e.Name == "Walk");
        Assert.False(walk.Measurable);
        Assert.False(walk.Parameters[0].Supported);
        Assert.Equal("Node", walk.Parameters[0].Type);

        Assert.True(entries.Single(e => e.Name == "Total").Measurable);

        // Measurable ones come first: this is a pick list, and burying a usable method
        // under unusable ones would make it actively unhelpful.
        Assert.True(entries[0].Measurable);
    }

    [Fact]
    public void Scanning_does_not_require_the_file_to_compile()
    {
        // The editor asks for this while the user is typing, so a half-written file has
        // to yield whatever is already parseable rather than nothing at all.
        var entries = EntryPointScanner.Scan(
            "public static class Half { public static int Go(int[] v) { return v.Length; } public static int Broken(");

        Assert.Contains(entries, e => e.Name == "Go");
    }
}
