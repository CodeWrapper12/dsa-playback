using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Phase 9: a design problem's complexity, per call. Each size n is n calls — the example's
/// calls repeated with new values — and each operation's average cost per call is fitted.
/// The compiled languages build with the toolchains on this machine, so these run with the
/// other compiled languages' tests.
/// </summary>
[Collection(CppCollection.Name)]
public class DesignComplexityTests
{
    private static string Sample(string folder, string name) => Path.Combine(AppContext.BaseDirectory, "samples", folder, name);

    private static readonly ExampleSearch LruExample = new(new[]
    {
        new FoundExample("Example 1", 1, ExampleOrigin.Comment, new string?[]
        {
            """["LRUCache","put","put","get","put","get"]""", "[[2],[1,1],[2,2],[1],[3,3],[2]]"
        }, Array.Empty<ExampleNote>())
    }, Array.Empty<string>());

    private static IReadOnlyList<DesignArg>? LruSignature(string operation, int arity) => (operation, arity) switch
    {
        ("LRUCache", 1) => new[] { DesignArg.Of("capacity", "int") },
        ("put", 2) => new[] { DesignArg.Of("key", "int"), DesignArg.Of("value", "int") },
        ("get", 1) => new[] { DesignArg.Of("key", "int") },
        _ => null
    };

    [Fact]
    public void Each_size_repeats_the_examples_calls_with_capacity_n_and_keys_below_n()
    {
        var workload = DesignWorkload.From("LRUCache", LruExample, LruSignature);
        var (operations, arguments) = workload.At(100);

        var ops = ((LeetArray)Literals.Parse(operations)).Items.Select(i => ((LeetString)i).Value).ToList();
        var args = ((LeetArray)Literals.Parse(arguments)).Items.Select(i => ((LeetArray)i).Items).ToList();
        Assert.Equal(101, ops.Count);
        Assert.Equal(new[] { "LRUCache", "put", "put", "get", "put", "get", "put", "put", "get" }, ops.Take(9));
        Assert.Equal("100", ((LeetNumber)args[0][0]).Text);
        Assert.All(args.Skip(1).SelectMany(a => a), v => Assert.InRange(int.Parse(((LeetNumber)v).Text), 0, 99));

        Assert.Equal(workload.At(100), workload.At(100));   // the same calls every time a size is run
        Assert.Contains("capacity = n", workload.Describe());
    }

    [Fact]
    public void Words_characters_and_arrays_are_made_up_in_LeetCodes_format()
    {
        Assert.Equal(DesignArgKind.String, DesignArg.Of("word", "const string&").Kind);
        Assert.Equal(DesignArgKind.String, DesignArg.Of("word", "&str").Kind);
        Assert.Equal(DesignArgKind.String, DesignArg.Of("word", "str").Kind);
        Assert.Equal(DesignArgKind.IntArray, DesignArg.Of("nums", "vector<int>&").Kind);
        Assert.Equal(DesignArgKind.IntArray, DesignArg.Of("nums", "Vec<i32>").Kind);
        Assert.Equal(DesignArgKind.IntArray, DesignArg.Of("nums", "List[int]").Kind);
        Assert.Equal(DesignArgKind.Int, DesignArg.Of("key", "i64").Kind);
        Assert.Equal(DesignArgKind.Unsupported, DesignArg.Of("node", "TreeNode*").Kind);

        var example = new ExampleSearch(new[]
        {
            new FoundExample("Example 1", 1, ExampleOrigin.Comment, new string?[]
            {
                """["Trie","insert","search"]""", """[[],["apple"],["app"]]"""
            }, Array.Empty<ExampleNote>())
        }, Array.Empty<string>());
        var workload = DesignWorkload.From("Trie", example, (op, arity) => op == "Trie" ? Array.Empty<DesignArg>()
            : arity == 1 ? new[] { DesignArg.Of("word", "String") } : null);
        var args = ((LeetArray)Literals.Parse(workload.At(20).Arguments)).Items.Skip(1).Select(a => ((LeetArray)a).Items.Single()).ToList();
        Assert.All(args, word => Assert.Matches("^[a-z]{3,8}$", ((LeetString)word).Value));
    }

    [Fact]
    public void Nothing_to_repeat_or_nothing_that_can_be_made_up_is_said_plainly()
    {
        var none = Assert.Throws<DesignWorkloadException>(() =>
            DesignWorkload.From("LRUCache", new ExampleSearch(Array.Empty<FoundExample>(), Array.Empty<string>()), LruSignature));
        Assert.Equal(DesignWorkload.NoExample, none.Message);

        var node = Assert.Throws<DesignWorkloadException>(() => DesignWorkload.From("LRUCache", LruExample,
            (op, arity) => op == "put" ? new[] { DesignArg.Of("key", "TreeNode"), DesignArg.Of("value", "int") } : LruSignature(op, arity)));
        Assert.Contains("'key' is a TreeNode, which cannot be made up", node.Message);

        var unhinted = Assert.Throws<DesignWorkloadException>(() => DesignWorkload.From("LRUCache", LruExample,
            (op, arity) => op == "get" ? new[] { DesignArg.Of("key", "") } : LruSignature(op, arity)));
        Assert.Contains("'key' has no type hint", unhinted.Message);
    }

    [Fact]
    public void A_cost_per_call_is_named_by_how_it_grows_over_a_fixed_part()
    {
        int[] sizes = { 10, 25, 50, 100, 250, 500, 1000 };
        // The mix of hits and misses makes a flat cost wobble; that is still O(1).
        var flat = DesignGrowthReader.PerCall(sizes, new[] { 3.6, 2.6, 2.3, 2.6, 2.5, 2.4, 2.4 });
        Assert.Equal(ComplexityClass.Constant, flat.Class);
        Assert.Equal("≈ 2.63 operations per call", flat.Formula);

        var heap = DesignGrowthReader.PerCall(sizes, sizes.Select(n => 5 + Math.Log2(n)).ToList());
        Assert.Equal(ComplexityClass.Logarithmic, heap.Class);

        // Measured from a cache kept in order by a list: n and n log n fit almost equally,
        // and the simpler is named.
        var scan = DesignGrowthReader.PerCall(sizes, new[] { 5.4, 5.2, 5.7, 8.2, 13.5, 20.5, 41.7 });
        Assert.Equal(ComplexityClass.Linear, scan.Class);
        Assert.Equal("≈ 0.037·n per call", scan.Formula);
    }

    public static IEnumerable<object[]> Fast() => new[]
    {
        new object[] { "design", "LRUCache.cs", "LRUCache" },
        new object[] { "design", "lru_cache.py", "LRUCache" },
        new object[] { "design", "lru_cache.cpp", "LRUCache" },
        new object[] { "java", "LRUCache.java", "LRUCache" },
        new object[] { "design", "min_stack.rs", "MinStack" }
    };

    [Theory]
    [MemberData(nameof(Fast))]
    public void A_design_whose_calls_cost_the_same_at_any_size_is_O1_per_call(string folder, string file, string entry)
    {
        var result = JobRunner.Run(new JobRequest { File = Sample(folder, file), Entry = entry }).Primary!;

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.NotEmpty(result.Design!.Operations);
        Assert.All(result.Design.Operations, o =>
        {
            Assert.Equal(ComplexityClass.Constant, o.PerCall);
            Assert.Contains("O(1) per call", o.Plain);
            Assert.Null(o.Because);
        });
        Assert.Equal(Measurer.DefaultSizes, result.Design.Operations[0].Points.Select(p => p.Size));
    }

    public static IEnumerable<object[]> Slow() => new[]
    {
        new object[] { "LRUCacheSlow.cs", 23, new[] { 30 }, "List<T>.Remove is O(n)" },
        new object[] { "lru_cache_slow.py", 18, new[] { 24 }, "remove is O(n)" },
        new object[] { "lru_cache_slow.cpp", 19, new[] { 25 }, "std::find is O(n)" },
        // Finding the key and removing it there are both O(n), and which costs more depends
        // on where the key is: either line is the answer.
        new object[] { "lru_cache_slow.rs", 25, new[] { 33, 34 }, "O(n)" },
        new object[] { "LRUCacheSlow.java", 20, new[] { 26 }, "O(n)" }
    };

    [Theory]
    [MemberData(nameof(Slow))]
    public void A_cache_that_scans_a_list_is_On_per_call_and_the_scanning_line_is_named(string file, int getLine, int[] putLines, string cost)
    {
        var result = JobRunner.Run(new JobRequest { File = Sample("design", file), Entry = "LRUCache" }).Primary!;

        Assert.Equal(RunStatus.Completed, result.Status);
        var get = result.Design!.Operations.Single(o => o.Name == "get");
        var put = result.Design.Operations.Single(o => o.Name == "put");
        Assert.Equal(ComplexityClass.Linear, get.PerCall);
        Assert.Equal(ComplexityClass.Linear, put.PerCall);
        Assert.Equal(getLine, get.Line);
        Assert.Contains(put.Line!.Value, putLines);
        Assert.Contains(cost, get.Because);
        Assert.EndsWith("per call", get.Formula);
    }

    [Fact]
    public void A_scan_inside_a_helper_is_named_with_the_helper()
    {
        // The Rust sample's get and put both call touch, which finds the key with a scan.
        var result = JobRunner.Run(new JobRequest { File = Sample("design", "lru_cache.rs"), Entry = "LRUCache" }).Primary!;
        var get = result.Design!.Operations.Single(o => o.Name == "get");

        Assert.Equal(ComplexityClass.Linear, get.PerCall);
        Assert.Equal(51, get.Line);
        Assert.Contains("in touch, which it calls", get.Because);
    }

    [Fact]
    public void Without_an_example_complexity_says_to_add_one()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dsaviz-design-{Guid.NewGuid():N}.py");
        File.WriteAllText(file, File.ReadAllText(Sample("design", "min_stack.py")).Replace("Input", "Inp"));
        try
        {
            var result = JobRunner.Run(new JobRequest { File = file, Entry = "MinStack" }).Primary!;
            Assert.Null(result.Design);
            Assert.Contains(DesignWorkload.NoExample, result.Errors);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
