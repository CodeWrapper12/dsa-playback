using DsaViz.Engine.Contracts;
using DsaViz.Engine.Java;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Java solutions, read, built with the JDK on this machine, run and measured. These need
/// JDK 17 or later; without one they fail, rather than pass while testing nothing. They share
/// the compiled languages' collection, apart from the timing tests.
/// </summary>
[Collection(CppCollection.Name)]
public class JavaTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "java", name);

    private static string Write(string source)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-java-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Solution.java");
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

    private static MeasurementResult Measure(string file, string entry, GenerationStrategy? strategy = null)
        => Job(new JobRequest { File = file, Entry = entry, Strategy = strategy }).Primary!;

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public void LeetCodes_template_is_read_without_imports()
    {
        var file = JavaReader.Read(File.ReadAllText(Sample("TwoSum.java")));
        Assert.Null(file.Problem);
        var entry = Assert.Single(JavaSolution.Entries(file));
        Assert.Equal("Solution.twoSum", entry.Qualified);
        Assert.True(entry.Measurable, entry.Reason);
        Assert.Equal(new[] { "int[]", "int" }, entry.Parameters.Select(p => p.Type));
    }

    [Fact]
    public void Private_helpers_are_not_offered()
    {
        var entries = JavaSolution.Entries(JavaReader.Read(File.ReadAllText(Sample("NumIslands.java"))));
        Assert.Equal("Solution.numIslands", Assert.Single(entries).Qualified);
    }

    [Fact]
    public void A_design_class_is_offered_once()
    {
        var entry = Assert.Single(JavaSolution.Entries(JavaReader.Read(File.ReadAllText(Sample("LRUCache.java")))));
        Assert.True(entry.Design);
        Assert.Equal("LRUCache", entry.Qualified);
        Assert.False(entry.Generatable);
    }

    [Fact]
    public void Declarations_are_told_apart_from_expressions()
    {
        var file = JavaReader.Read("""
            class Solution {
                int f(int[] a, int n) {
                    long total = 0, count = 1;
                    int[][] grid = new int[n][n];
                    Map<Integer, List<String>> byKey = new HashMap<>();
                    final var label = "x";
                    total = total + 1;
                    a[0] = 5;
                    byKey.clear();
                    int b[] = {1};
                    return 0;
                }
            }
            """);
        var declared = file.Methods.Single().Body.Children.SelectMany(s => s.Declares).Select(v => (v.Name, v.Type)).ToList();
        Assert.Equal(new[]
        {
            ("total", "long"), ("count", "long"), ("grid", "int[][]"), ("byKey", "Map<Integer, List<String>>"),
            ("label", "var"), ("b", "int[]")
        }, declared);
    }

    // ---- running ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "TwoSum.java", "Solution.twoSum" }, new object[] { "ReverseList.java", "Solution.reverseList" },
        new object[] { "MaxDepth.java", "Solution.maxDepth" }, new object[] { "LevelOrder.java", "Solution.levelOrder" },
        new object[] { "NumIslands.java", "Solution.numIslands" }, new object[] { "ValidParentheses.java", "Solution.isValid" },
        new object[] { "KthLargest.java", "Solution.findKthLargest" }, new object[] { "BinarySearch.java", "Solution.search" },
        new object[] { "GroupCount.java", "Solution.byFrequency" }, new object[] { "LongestSubstring.java", "Solution.lengthOfLongestSubstring" },
        new object[] { "Fib.java", "Solution.fib" }, new object[] { "ClimbStairs.java", "Solution.climbStairs" },
        new object[] { "UniquePaths.java", "Solution.uniquePaths" }, new object[] { "NotYetSet.java", "Solution.parity" },
        new object[] { "LRUCache.java", "LRUCache" }, new object[] { "MinStack.java", "MinStack" }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_runs_its_example_and_matches(string file, string entry)
    {
        var response = Job(new JobRequest { File = Sample(file), Entry = entry, AllExamples = true });
        Assert.True(response.Examples is not null, string.Join("; ", response.Errors));
        var report = response.Examples!;
        Assert.True(report.Status == RunStatus.Completed, string.Join("; ", report.Errors));
        Assert.NotEmpty(report.Runs);
        Assert.All(report.Runs, run => Assert.True(run.Matches == true, $"{run.Label}: expected {run.Expected}, got {run.Actual}. {run.Error}"));
    }

    [Fact]
    public void A_step_shows_the_variables_and_the_objects_they_hold()
    {
        var trace = Record(Sample("TwoSum.java"), "Solution.twoSum");

        Assert.Equal(RunStatus.Completed, trace.Status);
        var last = trace.Steps[^1];
        Assert.Equal(new[] { "nums", "target", "seen", "i" }, last.Variables.Select(v => v.Name));
        Assert.Equal("array", last.Variables[0].Kind);
        Assert.Equal("map", last.Variables[2].Kind);
        Assert.Equal(1L, last.Variables[3].Number);
        Assert.Equal("[0,1]", trace.ReturnValue);
        Assert.NotEmpty(trace.Returns);
        // Each step says what the code had allocated by then, and nothing of the recorder's.
        Assert.True(trace.Steps[0].Allocated < 200, $"the first step had allocated {trace.Steps[0].Allocated} bytes");
    }

    [Fact]
    public void A_variable_not_yet_set_says_so_until_it_is()
    {
        var trace = Record(Sample("NotYetSet.java"), "Solution.parity");
        var labels = trace.Steps.Select(s => s.Variables.FirstOrDefault(v => v.Name == "label")?.Display).ToList();
        Assert.Contains("(not set yet)", labels);
        Assert.Equal("\"odd\"", labels.Last());
    }

    [Fact]
    public void A_design_problem_runs_its_calls_and_names_the_one_running()
    {
        var trace = Record(Sample("LRUCache.java"), "LRUCache");
        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.True(trace.Check?.Matches, $"expected {trace.Check?.Expected}, got {trace.ReturnValue}");
        Assert.Equal(10, trace.Calls.Count);
        Assert.Equal("get", trace.Calls[3].Operation);
        // The object's own fields are shown: its map of items.
        Assert.Contains(trace.Steps, s => s.Variables.Any(v => v.Name == "this"));
    }

    [Fact]
    public void Calls_that_do_not_fit_a_design_class_are_explained()
    {
        var trace = Record(Sample("LRUCache.java"), "LRUCache", new[] { "[\"LRUCache\",\"put\",\"gett\"]", "[[1],[1,1],[1]]" });
        Assert.Equal(RunStatus.InvalidInput, trace.Status);
        Assert.Contains("Operation 3 is \"gett\", but LRUCache has no method by that name.", trace.Errors.Single());
    }

    [Fact]
    public void A_Deque_used_with_push_and_pop_is_a_stack_and_one_used_with_offer_and_poll_a_queue()
    {
        var stack = Record(Sample("ValidParentheses.java"), "Solution.isValid", new[] { "\"(([\"" });
        Assert.Contains(stack.Steps.SelectMany(s => s.Heap ?? Array.Empty<HeapObject>()), h => h.Kind == "stack");
        var queue = Record(Sample("LevelOrder.java"), "Solution.levelOrder");
        Assert.Contains(queue.Steps.SelectMany(s => s.Heap ?? Array.Empty<HeapObject>()), h => h.Kind == "queue");
    }

    [Fact]
    public void A_compile_error_names_the_students_own_line()
    {
        var trace = Record(Write("class Solution {\n    public int f(int n) {\n        return m + n;\n    }\n}\n"), "Solution.f");
        Assert.Equal(RunStatus.CompileError, trace.Status);
        Assert.StartsWith("line 3, column", trace.Errors[0]);
        Assert.Contains("cannot find symbol", trace.Errors[0]);
        Assert.DoesNotContain(trace.Errors, e => e.Contains("Dsaviz"));
    }

    [Fact]
    public void An_exception_names_its_type_and_line()
    {
        var trace = Record(Write("""
            class Solution {
                public int f(int[] v) {
                    int total = 0;
                    total += v[10];
                    return total;
                }
            }
            """), "Solution.f", new[] { "[1,2]" });
        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Equal("Your code threw ArrayIndexOutOfBoundsException at line 4: Index 10 out of bounds for length 2", trace.Errors.Single());
        Assert.Equal(4, trace.ErrorLine);
    }

    [Fact]
    public void An_endless_loop_is_stopped_by_the_budget()
    {
        var result = Record(Write("class Solution {\n    public int spin(int n) {\n        while (true) n++;\n    }\n}\n"), "Solution.spin", new[] { "1" });
        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
    }

    [Fact]
    public void Runaway_recursion_is_stopped_at_the_depth_limit()
    {
        var result = Record(Write("class Solution {\n    public int down(int n) {\n        return down(n - 1) + 1;\n    }\n}\n"), "Solution.down", new[] { "5" });
        Assert.Equal(RunStatus.RecursionLimitExceeded, result.Status);
        Assert.Contains("base case", result.Errors.Single());
    }

    [Fact]
    public void A_char_passed_to_a_charged_call_keeps_its_overload()
    {
        // set.remove(s.charAt(i)) must still remove a Character: widened to an int on its way
        // through the charge, it would look for an Integer and never find one.
        var result = Measure(Sample("LongestSubstring.java"), "Solution.lengthOfLongestSubstring");
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);
    }

    // ---- measuring -----------------------------------------------------------------------------

    [Theory]
    [InlineData("TwoSum.java", "Solution.twoSum", GenerationStrategy.Distinct, ComplexityClass.Linear)]
    [InlineData("DedupeWithContains.java", "Solution.countDistinct", GenerationStrategy.Distinct, ComplexityClass.Quadratic)]
    [InlineData("JoinWithPlus.java", "Solution.join", null, ComplexityClass.Quadratic)]
    [InlineData("ClimbStairs.java", "Solution.climbStairs", null, ComplexityClass.Linear)]
    [InlineData("Fib.java", "Solution.fib", null, ComplexityClass.Exponential)]
    [InlineData("GroupCount.java", "Solution.byFrequency", null, ComplexityClass.Linearithmic)]
    [InlineData("UniquePaths.java", "Solution.uniquePaths", null, ComplexityClass.Quadratic)]
    [InlineData("ReverseList.java", "Solution.reverseList", null, ComplexityClass.Linear)]
    public void Growth_is_measured_as_for_the_other_languages(string file, string entry, GenerationStrategy? strategy, ComplexityClass expected)
    {
        var result = Measure(Sample(file), entry, strategy);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(expected, result.Empirical.Best);
    }

    [Fact]
    public void Hidden_work_is_named_and_a_strings_copies_are_charged()
    {
        Assert.Contains("contains is O(n) per call",
            Measure(Sample("DedupeWithContains.java"), "Solution.countDistinct", GenerationStrategy.Distinct).Derivation!.Summary);
        Assert.Contains("String += is O(length) per call", Measure(Sample("JoinWithPlus.java"), "Solution.join").Derivation!.Summary);
    }

    [Fact]
    public void Memory_counts_only_the_solution()
    {
        // Fibonacci allocates nothing: not the JVM's class loading, not the boxing of the result.
        var fib = Measure(Sample("Fib.java"), "Solution.fib");
        Assert.All(fib.Points, p => Assert.Equal(0, p.BytesAllocated));

        // Building a string with += allocates O(n²) in all but holds only O(n) at once.
        var join = Measure(Sample("JoinWithPlus.java"), "Solution.join");
        Assert.Equal(ComplexityClass.Linear, join.Space!.LikelyPeak);
        Assert.StartsWith("Exact", join.Detail!.MemoryModel);
    }

    [Fact]
    public void Static_state_turns_the_warm_up_off()
    {
        Assert.True(JavaInstrumenter.HasStaticState(JavaReader.Read("class Solution { static int calls; public int f() { return ++calls; } }")));
        Assert.True(JavaInstrumenter.HasStaticState(JavaReader.Read("class Solution { private static Map<Integer, Integer> memo = new HashMap<>(); }")));
        Assert.False(JavaInstrumenter.HasStaticState(JavaReader.Read("class Solution { static final int MOD = 1_000_000_007; static int f(int n) { return n; } }")));
    }

    [Theory]
    [InlineData("MaxDepth.java", "Solution.maxDepth", ComplexityClass.Linear)]
    [InlineData("ClimbStairs.java", "Solution.climbStairs", ComplexityClass.Linear)]
    [InlineData("Fib.java", "Solution.fib", ComplexityClass.Exponential)]
    [InlineData("BinarySearch.java", "Solution.search", ComplexityClass.Logarithmic)]
    [InlineData("DedupeWithContains.java", "Solution.countDistinct", ComplexityClass.Quadratic)]
    [InlineData("NumIslands.java", "Solution.numIslands", ComplexityClass.Quadratic)]
    public void The_code_is_read_for_a_second_opinion(string file, string entry, ComplexityClass expected)
    {
        var solution = JavaSolution.Read(File.ReadAllText(Sample(file)), entry);
        Assert.Equal(expected, JavaEstimator.Time(solution.File, solution.Method!).Class);
    }
}
