using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Python;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Python through the same engine as C#: the same inputs, examples, recordings and
/// measurements, with only the running done by Python. These need a Python 3.9+ on the
/// path and fail without one rather than skip — a skipped test reports green while
/// testing nothing.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class PythonTests
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples", "python");

    private static string Sample(string name) => Path.Combine(Samples, name);

    private static string Write(string source)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-python-tests");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"s{Guid.NewGuid():N}.py");
        File.WriteAllText(file, source);
        return file;
    }

    private static JobResponse Job(JobRequest request)
    {
        var response = JobRunner.Run(request);
        Assert.True(response.ErrorCode is null, string.Join("; ", response.Errors));
        return response;
    }

    private static StepTrace Record(string file, string entry, string?[]? values = null, int? example = null, int size = 8)
        => Job(new JobRequest { File = file, Entry = entry, StepSize = size, Values = values, Example = example }).Steps!;

    private static MeasurementResult Measure(string file, string entry, int[]? sizes = null, GenerationStrategy? strategy = null)
        => Job(new JobRequest { File = file, Entry = entry, Sizes = sizes, Strategy = strategy }).Primary!;

    private static IReadOnlyList<EntryPoint> Entries(string source)
        => PythonSolution.Entries(PythonSolution.Describe(PythonHost.Locate(), source, "").Description);

    // ---- functions ---------------------------------------------------------------------

    [Fact]
    public void Methods_and_top_level_functions_are_listed_with_their_hints()
    {
        var entries = Entries("""
            from typing import List
            def helper(nums: List[int], k: int) -> int:
                return k
            def main():
                pass
            class Solution:
                def twoSum(self, nums: List[int], target: int) -> List[int]:
                    return []
                def _private(self, x: int) -> int:
                    return x
            """);

        Assert.Equal(new[] { "Solution.twoSum", "helper" }, entries.Select(e => e.Qualified).Order(StringComparer.Ordinal));
        Assert.All(entries, e => Assert.True(e.Measurable, e.Reason));
        Assert.Equal(new[] { "List[int]", "int" }, entries.Single(e => e.Name == "twoSum").Parameters.Select(p => p.Type));
    }

    [Fact]
    public void A_parameter_without_a_hint_needs_an_example_and_says_so()
    {
        var unhinted = Entries("def walk(nums):\n    return len(nums)\n").Single();
        Assert.False(unhinted.Measurable);
        Assert.Contains("no type hint", unhinted.Reason);

        var given = Entries("# Input: nums = [1,2,3]\ndef walk(nums):\n    return len(nums)\n").Single();
        Assert.True(given.Measurable, given.Reason);
    }

    [Fact]
    public void A_syntax_error_is_reported_at_its_line()
    {
        var file = Write("def broken(n: int) -> int:\n    return (n +\n");
        var steps = Record(file, "broken");
        Assert.Equal(RunStatus.CompileError, steps.Status);
        Assert.Matches(@"^line \d+", steps.Errors.Single());
    }

    // ---- examples and inputs ---------------------------------------------------------------

    [Fact]
    public void Comment_examples_are_used_and_checked_honouring_any_order()
    {
        var steps = Record(Sample("two_sum.py"), "Solution.twoSum");
        Assert.Equal(RunStatus.Completed, steps.Status);
        Assert.All(steps.Inputs, i => Assert.Equal(InputSource.Example, i.Source));
        Assert.True(steps.Check!.Matches);
        Assert.True(steps.Check.AnyOrder);
    }

    [Fact]
    public void Calls_under_main_and_the_variables_they_pass_are_examples()
    {
        var file = Write("""
            from typing import List
            class Solution:
                def total(self, nums: List[int], k: int) -> int:
                    return sum(nums) * k

            def build():
                return [9]

            if __name__ == "__main__":
                data = [1, 2, 3]
                print(Solution().total(data, 2))
                grows = [5]
                grows.append(6)
                print(Solution().total(grows, 3))
                print(Solution().total(build(), 4))
            """);

        var steps = Record(file, "Solution.total");
        Assert.Equal(new[] { "[1,2,3]", "2" }, steps.Examples[0].Values);
        Assert.Equal("12", steps.ReturnValue);
        Assert.Contains("changes between where it is set and the call", steps.Examples[1].Notes.Single().Message);
        Assert.Contains("`build()`, which is not a value written out", steps.Examples[2].Notes.Single().Message);
    }

    [Fact]
    public void Typed_values_are_read_the_same_way_as_for_CSharp()
    {
        var steps = Record(Sample("two_sum.py"), "Solution.twoSum", values: new[] { "[1,5,9]", "14" });
        Assert.Equal("[1,2]", steps.ReturnValue);

        var invalid = Record(Sample("two_sum.py"), "Solution.twoSum", values: new[] { "[1,'a'", null });
        Assert.Equal(RunStatus.InvalidInput, invalid.Status);
        Assert.NotNull(invalid.Inputs[0].Error);
    }

    [Fact]
    public void Generated_values_are_handed_over_whole_however_large()
    {
        // A 100×100 grid is 10,100 values — past the cap on values shown in the panel.
        var result = Measure(Sample("num_islands.py"), "Solution.numIslands", sizes: new[] { 100 });
        Assert.Empty(result.Errors);
        Assert.Single(result.Points);
    }

    // ---- the run view ------------------------------------------------------------------------

    [Fact]
    public void Every_sample_records_its_example_and_matches_its_expected_output()
    {
        var cases = new[]
        {
            ("two_sum_sorted.py", "Solution.twoSum"), ("reverse_list.py", "Solution.reverseList"),
            ("max_depth.py", "Solution.maxDepth"), ("level_order.py", "Solution.levelOrder"),
            ("climb_stairs.py", "Solution.climbStairs"), ("fib.py", "fib"), ("num_islands.py", "Solution.numIslands"),
            ("count_components.py", "Solution.countComponents"), ("kth_largest.py", "Solution.findKthLargest"),
            ("valid_parentheses.py", "Solution.isValid")
        };

        foreach (var (file, entry) in cases)
        {
            var steps = Record(Sample(file), entry);
            Assert.True(steps.Status == RunStatus.Completed, $"{file}: {string.Join("; ", steps.Errors)}");
            Assert.True(steps.Check?.Matches == true, $"{file}: expected {steps.Check?.Expected}, got {steps.ReturnValue}");
            Assert.NotEmpty(steps.Steps);
        }
    }

    [Fact]
    public void Structures_are_recorded_as_what_they_are_used_as()
    {
        string KindsOf(StepTrace trace) => string.Join(",", trace.Steps.SelectMany(s => s.Heap ?? Array.Empty<HeapObject>())
            .Select(o => o.Kind).Distinct().Order());

        Assert.Contains("stack", KindsOf(Record(Sample("valid_parentheses.py"), "Solution.isValid")));
        Assert.Contains("heap", KindsOf(Record(Sample("kth_largest.py"), "Solution.findKthLargest")));
        Assert.Contains("queue", KindsOf(Record(Sample("level_order.py"), "Solution.levelOrder")));
        Assert.Contains("tree", KindsOf(Record(Sample("max_depth.py"), "Solution.maxDepth")));
        Assert.Contains("listnode", KindsOf(Record(Sample("reverse_list.py"), "Solution.reverseList")));
        Assert.Contains("map", KindsOf(Record(Sample("two_sum.py"), "Solution.twoSum")));

        var heap = Record(Sample("kth_largest.py"), "Solution.findKthLargest").Steps
            .SelectMany(s => s.Heap ?? Array.Empty<HeapObject>()).First(o => o.Kind == "heap");
        Assert.Equal(2, heap.Arity);
    }

    [Fact]
    public void Shared_nodes_keep_one_id_and_every_call_returns_its_value()
    {
        var list = Record(Sample("reverse_list.py"), "Solution.reverseList");
        var step = list.Steps.First(s => s.Variables.Any(v => v.Name == "previous" && v.Ref is not null) &&
                                          s.Variables.Any(v => v.Name == "head" && v.Ref is not null));
        Assert.NotNull(step);

        var fib = Record(Sample("fib.py"), "fib");
        Assert.Equal(25, fib.Returns.Count);   // fib(6) makes 25 calls
        Assert.Equal("8", fib.Returns[^1].Value.Text);
        Assert.Equal(new[] { "n" }, fib.Methods.Single(m => m.Name == "fib").Parameters);
    }

    [Fact]
    public void Recording_its_own_variables_never_shows_up_as_the_codes_memory()
    {
        var file = Write("""
            def count(n: int) -> int:
                total = 0
                for i in range(n):
                    total = total + 1
                return total
            """);
        var steps = Record(file, "count", values: new[] { "50" });
        // The loop allocates nothing; capturing locals() at every step must not appear.
        var inLoop = steps.Steps.Where(s => s.Line == 4).Select(s => s.Allocated).Distinct().ToList();
        Assert.Single(inLoop);
    }

    [Fact]
    public void An_exception_in_the_students_code_names_its_line()
    {
        var file = Write("""
            from typing import List
            def last(nums: List[int]) -> int:
                i = 0
                while True:
                    i += nums[i]
            """);
        var steps = Record(file, "last", values: new[] { "[1,2,3]" });
        Assert.Equal(RunStatus.RuntimeError, steps.Status);
        Assert.Equal(5, steps.ErrorLine);
        Assert.StartsWith("Your code threw IndexError at line 5", steps.Errors.Single());
        Assert.NotEmpty(steps.Steps);
    }

    [Fact]
    public void Printing_does_not_disturb_the_answer()
    {
        var file = Write("def loud(n: int) -> int:\n    print('hello', n)\n    return n * 2\n");
        Assert.Equal("6", Record(file, "loud", values: new[] { "3" }).ReturnValue);
    }

    // ---- measuring ---------------------------------------------------------------------------

    [Fact]
    public void Hidden_list_scans_are_charged_and_make_the_class()
    {
        var result = Measure(Sample("dedupe_with_list.py"), "Solution.dedupe", strategy: GenerationStrategy.Distinct);
        Assert.Equal(ComplexityClass.Quadratic, result.Empirical.Best);
        var scan = result.Detail!.LibraryCalls.Single();
        Assert.Equal("O(n)", scan.Cost);
        Assert.Equal(1000L * 999 / 2, scan.Work);     // the n(n−1)/2 of the textbook
    }

    [Theory]
    [InlineData("two_sum_sorted.py", "Solution.twoSum", ComplexityClass.Linear)]
    [InlineData("reverse_list.py", "Solution.reverseList", ComplexityClass.Linear)]
    [InlineData("kth_largest.py", "Solution.findKthLargest", ComplexityClass.Linear)]
    [InlineData("level_order.py", "Solution.levelOrder", ComplexityClass.Linear)]
    [InlineData("count_components.py", "Solution.countComponents", ComplexityClass.Linear)]
    [InlineData("valid_parentheses.py", "Solution.isValid", ComplexityClass.Linear)]
    public void Common_solutions_measure_as_their_class(string file, string entry, ComplexityClass expected)
        => Assert.Equal(expected, Measure(Sample(file), entry).Empirical.Best);

    [Fact]
    public void Exponential_recursion_drops_to_small_sizes_and_is_named()
    {
        var result = Measure(Sample("fib.py"), "fib");
        Assert.Equal(ComplexityClass.Exponential, result.Empirical.Best);
        Assert.Equal(Measurer.SmallSizes, result.Points.Select(p => p.Size));
    }

    [Fact]
    public void Space_is_the_peak_held_measured_directly()
    {
        var kept = Measure(Sample("valid_parentheses.py"), "Solution.isValid", strategy: GenerationStrategy.AllDuplicates);
        Assert.Equal(ComplexityClass.Linear, kept.Space!.LikelyPeak);
        Assert.Contains("measured directly", kept.Space.Summary);

        var none = Measure(Sample("two_sum_sorted.py"), "Solution.twoSum");
        Assert.Equal(ComplexityClass.Constant, none.Space!.LikelyPeak);
    }

    [Fact]
    public void A_runaway_loop_and_a_missing_base_case_are_stopped_and_explained()
    {
        var loop = Measure(Write("def spin(n: int) -> int:\n    while True:\n        n += 1\n"), "spin", sizes: new[] { 10 });
        Assert.Equal(RunStatus.BudgetExceeded, loop.Status);

        var deep = Measure(Write("def down(n: int) -> int:\n    return down(n - 1) + 1\n"), "down", sizes: new[] { 10 });
        Assert.Equal(RunStatus.RecursionLimitExceeded, deep.Status);
        Assert.Contains("missing or unreachable base case", string.Join(" ", deep.Errors));
    }

    // ---- reading the code ----------------------------------------------------------------------

    [Theory]
    [InlineData("dedupe_with_list.py", "Solution.dedupe", ComplexityClass.Quadratic, "scans the list")]
    [InlineData("climb_stairs.py", "Solution.climbStairs", ComplexityClass.Linear, "caches results")]
    [InlineData("fib.py", "fib", ComplexityClass.Exponential, "no memoisation")]
    [InlineData("max_depth.py", "Solution.maxDepth", ComplexityClass.Linear, "child nodes")]
    [InlineData("num_islands.py", "Solution.numIslands", ComplexityClass.Quadratic, "processed once in total")]
    [InlineData("level_order.py", "Solution.levelOrder", ComplexityClass.Unknown, "worklist loop")]
    [InlineData("two_sum_sorted.py", "Solution.twoSum", ComplexityClass.Linear, "loop nesting depth 1")]
    public void The_code_is_read_for_time(string file, string entry, ComplexityClass expected, string reason)
    {
        var description = PythonSolution.Describe(PythonHost.Locate(), File.ReadAllText(Sample(file)), entry).Description;
        Assert.Equal(expected, description.Time!.Class);
        Assert.Contains(reason, description.Time.Reasoning);
    }

    // ---- which Python --------------------------------------------------------------------------

    [Fact]
    public void Every_supported_Python_found_runs_the_same_and_an_old_one_is_refused()
    {
        var found = new List<string>();
        foreach (var version in new[] { "python3.9", "python3.10", "python3.11", "python3.12", "python3.13" })
        {
            PythonHost host;
            try { host = PythonHost.Locate(Which(version) ?? "missing"); }
            catch (PythonUnavailableException) { continue; }
            if (Which(version) is null) continue;

            var steps = Stepper.RecordPython(host, File.ReadAllText(Sample("level_order.py")), "Solution.levelOrder");
            Assert.True(steps.Check?.Matches == true, $"{version}: {string.Join("; ", steps.Errors)}");
            found.Add(version);
        }
        // Windows has no python3.x names on the path: there, the default interpreter is the one tested.
        if (found.Count == 0)
        {
            var steps = Stepper.RecordPython(PythonHost.Locate(), File.ReadAllText(Sample("level_order.py")), "Solution.levelOrder");
            Assert.True(steps.Check?.Matches == true, string.Join("; ", steps.Errors));
        }

        if (Which("python3.8") is { } old)
        {
            var refused = Assert.Throws<PythonUnavailableException>(() =>
                PythonSolution.Describe(PythonHost.Locate(old), "def f(n: int) -> int:\n    return n\n", "f"));
            Assert.Equal("python-version", refused.Code);
        }
    }

    [Fact]
    public void No_Python_is_reported_with_a_code_the_editor_can_act_on()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetTempPath());
            var response = JobRunner.Run(new JobRequest { File = Sample("fib.py"), Entry = "fib", StepSize = 4 });
            Assert.Equal("python-missing", response.ErrorCode);
            Assert.Contains("Install Python", response.Errors.Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    private static string? Which(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}
