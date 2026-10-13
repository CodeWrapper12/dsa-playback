using DsaViz.Engine.Contracts;
using DsaViz.Engine.Js;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// JavaScript and TypeScript solutions, read with the bundled TypeScript compiler, run with the
/// Node on this machine and measured. They need Node 18 or later; without it they fail, rather
/// than pass while testing nothing. They share the compiled languages' collection.
/// </summary>
[Collection(CppCollection.Name)]
public class JsTests
{
    private static string Sample(string name)
        => Path.Combine(AppContext.BaseDirectory, "samples", name.EndsWith(".ts") ? "typescript" : "javascript", name);

    private static string Write(string source, string name = "solution.js")
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-js-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name);
        File.WriteAllText(file, source);
        return file;
    }

    private static JobResponse Job(JobRequest request)
    {
        var response = JobRunner.Run(request);
        Assert.True(response.ErrorCode is null, string.Join("; ", response.Errors));
        return response;
    }

    private static StepTrace Record(string file, string entry, string?[]? values = null)
    {
        var response = Job(new JobRequest { File = file, Entry = entry, StepSize = 8, Values = values });
        Assert.True(response.Steps is not null, string.Join("; ", response.Errors));
        return response.Steps!;
    }

    private static MeasurementResult Measure(string file, string entry)
        => Job(new JobRequest { File = file, Entry = entry }).Primary!;

    private static IReadOnlyList<EntryPoint> Entries(string file)
        => JsSolution.Entries(JsSolution.Describe(JsHost.Locate(), File.ReadAllText(file), "", JsSolution.IsTypeScript(file)));

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public void LeetCodes_JavaScript_is_read_with_its_JSDoc_types()
    {
        var entry = Assert.Single(Entries(Sample("two_sum_sorted.js")));
        Assert.Equal("twoSum", entry.Qualified);
        Assert.True(entry.Measurable, entry.Reason);
        Assert.True(entry.Generatable);
        Assert.Equal(new[] { "number[]", "number" }, entry.Parameters.Select(p => p.Type));
    }

    [Fact]
    public void LeetCodes_TypeScript_is_read_with_its_annotations()
    {
        var entry = Assert.Single(Entries(Sample("max_depth.ts")));
        Assert.Equal("maxDepth", entry.Qualified);
        Assert.Equal(new[] { "TreeNode | null" }, entry.Parameters.Select(p => p.Type));
        Assert.True(entry.Generatable);
    }

    [Theory]
    [InlineData("lru_cache.js", "LRUCache")]
    [InlineData("min_stack.ts", "MinStack")]
    public void A_design_class_is_offered_once_in_either_style(string file, string name)
    {
        var entry = Assert.Single(Entries(Sample(file)));
        Assert.True(entry.Design);
        Assert.Equal(name, entry.Qualified);
        Assert.True(entry.Generatable, "its example's calls can be repeated at bigger sizes");
    }

    [Fact]
    public void A_syntax_error_is_reported_with_its_line_and_nothing_runs()
    {
        var file = Write("// Input: n = 3\n/** @param {number} n */\nvar f = function(n) {\n    return n +;\n};\n");
        var trace = Record(file, "f");
        Assert.Equal(RunStatus.CompileError, trace.Status);
        Assert.Contains("line 4", trace.Errors[0]);
    }

    // ---- running ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "two_sum_sorted.js", "twoSum", "[1,2]" },
        new object[] { "reverse_list.js", "reverseList", "[5,4,3,2,1]" },
        new object[] { "level_order.js", "levelOrder", "[[3],[9,20],[15,7]]" },
        new object[] { "climb_stairs.js", "climbStairs", "8" },
        new object[] { "dedupe_with_includes.js", "dedupe", "[3,1,2]" },
        new object[] { "num_islands.js", "numIslands", "2" },
        new object[] { "kth_largest.js", "findKthLargest", "5" },
        new object[] { "lru_cache.js", "LRUCache", "[null,null,null,1,null,-1,null,-1,3,4]" },
        new object[] { "constructs.js", "constructs", "\"1,2,3,5,8,9|cba|7|x:3|odd,odd,even|found|done\"" },
        new object[] { "two_sum_sorted.ts", "twoSum", "[1,2]" },
        new object[] { "max_depth.ts", "maxDepth", "3" },
        new object[] { "min_stack.ts", "MinStack", "[null,null,null,null,-3,null,0,-2]" },
        new object[] { "lru_cache_slow.ts", "LRUCache", "[null,null,null,1,null,-1,null,-1,3,4]" }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_runs_its_example_and_matches(string file, string entry, string result)
    {
        var trace = Record(Sample(file), entry);
        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal(result, trace.ReturnValue);
        Assert.True(trace.Check?.Matches, $"{file}: {trace.Check?.Actual} vs {trace.Check?.Expected}");
        Assert.NotEmpty(trace.Steps);
    }

    [Fact]
    public void A_step_shows_the_variables_declared_before_it_in_the_order_written()
    {
        var trace = Record(Sample("two_sum_sorted.js"), "twoSum");
        var step = trace.Steps.First(s => s.Variables.Any(v => v.Name == "total"));
        Assert.Equal(new[] { "numbers", "target", "left", "right", "total" }, step.Variables.Select(v => v.Name));
        Assert.Equal("array", step.Variables[0].Kind);
        Assert.Equal(9, step.Variables[1].Number);
    }

    [Fact]
    public void An_array_used_with_shift_is_drawn_as_a_queue_and_nodes_as_a_tree()
    {
        var trace = Record(Sample("level_order.js"), "levelOrder");
        var heap = trace.Steps.Where(s => s.Heap is not null).SelectMany(s => s.Heap!).ToList();
        Assert.Contains(heap, o => o.Kind == "queue");
        Assert.Contains(heap, o => o.Kind == "tree");
    }

    [Fact]
    public void A_design_problems_methods_show_the_objects_fields_and_each_call_is_marked()
    {
        var trace = Record(Sample("lru_cache.js"), "LRUCache");
        Assert.Contains(trace.Steps, s => s.Variables.Any(v => v.Name == "this.items" && v.Kind == "map"));
        Assert.Contains(trace.Steps, s => s.Variables.Any(v => v.Name == "this.capacity" && v.Number == 2));
        Assert.Equal(10, trace.Calls.Count);
    }

    [Fact]
    public void Recursion_records_what_each_call_returned()
    {
        var trace = Record(Sample("max_depth.ts"), "maxDepth");
        Assert.Contains(trace.Returns, r => r.Value.Text == "3");
        Assert.True(trace.Steps.Max(s => s.Depth) >= 3);
    }

    [Fact]
    public void An_error_names_its_type_and_line()
    {
        var file = Write("// Input: nums = [1,2]\n/** @param {number[]} nums */\nvar f = function(nums) {\n    const node = null;\n    return node.val;\n};\n");
        var trace = Record(file, "f");
        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Matches(@"^Your code threw TypeError at line 5: ", trace.Errors[0]);
        Assert.Equal(5, trace.ErrorLine);
    }

    [Fact]
    public void A_missing_base_case_and_an_endless_loop_are_stopped_and_explained()
    {
        var recursion = Record(Write("// Input: n = 3\n/** @param {number} n */\nvar f = function(n) {\n    return f(n - 1);\n};\n"), "f");
        Assert.Equal(RunStatus.RecursionLimitExceeded, recursion.Status);
        Assert.Contains("base case", recursion.Errors[0]);

        var loop = Measure(Write("/** @param {number} n */\nvar spin = function(n) {\n    let i = 0;\n    while (true) i++;\n};\n"), "spin");
        Assert.Contains("budget", string.Join(" ", loop.Errors.Append(loop.SizeNote ?? "")));
    }

    // ---- measuring ---------------------------------------------------------------------------------

    [Fact]
    public void Two_pointers_are_linear_and_allocate_only_their_answer()
    {
        var result = Measure(Sample("two_sum_sorted.js"), "twoSum");
        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);
        Assert.Equal(ComplexityClass.Linear, result.Static.Class);
        Assert.Equal(ComplexityClass.Constant, result.Space!.LikelyPeak);
        Assert.All(result.Points, p => Assert.Equal(32, p.BytesAllocated));
    }

    [Fact]
    public void Includes_on_an_array_is_charged_as_a_scan()
    {
        var result = Measure(Sample("dedupe_with_includes.js"), "dedupe");
        Assert.Equal(ComplexityClass.Quadratic, result.Empirical.Best);
        Assert.Equal(ComplexityClass.Quadratic, result.Static.Class);
        Assert.Contains(result.Detail!.LibraryCalls, c => c.Member == ".includes()" && c.Work > 0);
    }

    [Fact]
    public void A_memo_makes_recursion_linear_and_its_entries_are_the_memory()
    {
        var result = Measure(Sample("climb_stairs.js"), "climbStairs");
        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);
        Assert.Equal(ComplexityClass.Linear, result.Space!.LikelyPeak);
    }

    [Fact]
    public void A_list_that_keeps_growing_is_kept_data_not_garbage()
    {
        // Arrays move to bigger storage as they grow, so the largest allocation grows with n.
        var result = Measure(Sample("level_order.js"), "levelOrder");
        Assert.Equal(ComplexityClass.Linear, result.Space!.LikelyPeak);
        Assert.Equal(ComplexityClass.Quadratic, result.Empirical.Best);   // shift moves the whole queue
    }

    [Fact]
    public void A_design_problem_is_measured_per_call_and_a_scan_is_named()
    {
        var fast = Measure(Sample("lru_cache.js"), "LRUCache");
        Assert.All(fast.Design!.Operations, o => Assert.Equal(ComplexityClass.Constant, o.PerCall));

        var slow = Measure(Sample("lru_cache_slow.ts"), "LRUCache");
        var get = slow.Design!.Operations.Single(o => o.Name == "get");
        Assert.Equal(ComplexityClass.Linear, get.PerCall);
        Assert.Equal(16, get.Line);
        Assert.Contains(".indexOf() is O(n)", get.Because);
    }
}
