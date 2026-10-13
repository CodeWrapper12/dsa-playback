using DsaViz.Engine.Contracts;
using DsaViz.Engine.Cpp;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// C++ solutions, read, built with the compiler on this machine, run and measured. These
/// need clang++ or g++; without one they fail, rather than pass while testing nothing.
/// </summary>
[Collection(CppCollection.Name)]
public class CppTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "cpp", name);

    private static string Write(string source)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-cpp-tests");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"s{Guid.NewGuid():N}.cpp");
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
    public void LeetCodes_template_is_read_without_includes_or_using()
    {
        var file = CppReader.Read(File.ReadAllText(Sample("two_sum.cpp")));
        Assert.Null(file.Problem);
        var entry = Assert.Single(CppSolution.Entries(file));
        Assert.Equal("Solution.twoSum", entry.Qualified);
        Assert.True(entry.Measurable, entry.Reason);
        Assert.Equal(new[] { "vector<int>&", "int" }, entry.Parameters.Select(p => p.Type));
    }

    [Fact]
    public void Declarations_are_told_apart_from_expressions()
    {
        var file = CppReader.Read("""
            int f(vector<int>& a, int n) {
                long long total = 0, count = 1;
                vector<vector<int>> dp(n, vector<int>(n, 0));
                unordered_map<int, int> seen;
                TreeNode* node = nullptr;
                const auto& [k, v] = make_pair(1, 2);
                total = total + 1;
                a[0] = 5;
                cout << total;
                seen.clear();
                int grid[n + 1];
                return 0;
            }
            """);
        var declared = file.Functions.Single().Body.Children.SelectMany(s => s.Declares).Select(v => (v.Name, v.Type)).ToList();
        Assert.Equal(new[]
        {
            ("total", "long long"), ("count", "long long"), ("dp", "vector<vector<int>>"), ("seen", "unordered_map<int, int>"),
            ("node", "TreeNode*"), ("k", ""), ("v", ""), ("grid", CppReader.VariableLengthArray)
        }, declared);
    }

    [Fact]
    public void A_parameter_type_that_cannot_be_given_says_so()
    {
        var entry = Assert.Single(CppSolution.Entries(CppReader.Read(
            "class Solution { public: int f(unordered_map<int, int>& m) { return 0; } };")));
        Assert.False(entry.Measurable);
        Assert.Contains("can neither generate nor read from an example", entry.Reason);
    }

    // ---- running ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "two_sum.cpp", "Solution.twoSum" }, new object[] { "reverse_list.cpp", "Solution.reverseList" },
        new object[] { "max_depth.cpp", "Solution.maxDepth" }, new object[] { "level_order.cpp", "Solution.levelOrder" },
        new object[] { "num_islands.cpp", "Solution.numIslands" }, new object[] { "valid_parentheses.cpp", "Solution.isValid" },
        new object[] { "kth_largest.cpp", "Solution.findKthLargest" }, new object[] { "binary_search.cpp", "Solution.search" },
        new object[] { "group_count.cpp", "Solution.byFrequency" }, new object[] { "longest_substring.cpp", "Solution.lengthOfLongestSubstring" },
        new object[] { "fib.cpp", "fib" }, new object[] { "climb_stairs.cpp", "Solution.climbStairs" }
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
        var trace = Record(Sample("two_sum.cpp"), "Solution.twoSum");

        Assert.Equal(RunStatus.Completed, trace.Status);
        var last = trace.Steps[^1];
        Assert.Equal(new[] { "nums", "target", "seen", "i" }, last.Variables.Select(v => v.Name));
        Assert.Equal("array", last.Variables[0].Kind);
        Assert.Equal("map", last.Variables[2].Kind);
        Assert.Equal(1L, last.Variables[3].Number);
        Assert.Equal("[0,1]", trace.ReturnValue);
        Assert.NotEmpty(trace.Returns);
    }

    [Fact]
    public void Every_argument_is_evaluated_once_even_when_a_library_call_is_charged()
    {
        // erase(s[left++]) once moved left twice, which changed what the code did.
        var result = Measure(Sample("longest_substring.cpp"), "Solution.lengthOfLongestSubstring");
        Assert.Equal(ComplexityClass.Linear, result.Empirical.Best);

        string file = Write("""
            class Solution {
            public:
                int f(vector<int>& v) {
                    int i = 0;
                    bool missing = find(v.begin() + i++, v.end(), -1) == v.end();
                    v.erase(v.begin() + i++);
                    return i * 10 + missing;
                }
            };
            """);
        var trace = Record(file, "Solution.f", new[] { "[1,2,3]" });
        Assert.Equal("21", trace.ReturnValue);
        var measured = Measure(file, "Solution.f");
        Assert.Equal(RunStatus.Completed, measured.Status);
    }

    [Fact]
    public void A_compile_error_names_the_students_own_line()
    {
        var trace = Record(Write("class Solution {\npublic:\n    int f(int n) {\n        return m + n;\n    }\n};\n"), "Solution.f");
        Assert.Equal(RunStatus.CompileError, trace.Status);
        Assert.StartsWith("line 4, column", trace.Errors[0]);
        Assert.DoesNotContain(trace.Errors, e => e.Contains("dsaviz"));
    }

    [Fact]
    public void A_crash_is_explained_with_its_line()
    {
        var trace = Record(Write("""
            class Solution {
            public:
                int f(int n) {
                    ListNode* p = nullptr;
                    return p->val + n;
                }
            };
            """), "Solution.f", new[] { "1" });
        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Contains("crashed", trace.Errors.Single());
        Assert.Contains("null pointer", trace.Errors.Single());
    }

    [Fact]
    public void An_exception_names_its_type_and_line()
    {
        var trace = Record(Write("""
            class Solution {
            public:
                int f(vector<int>& v) {
                    int total = 0;
                    total += v.at(10);
                    return total;
                }
            };
            """), "Solution.f", new[] { "[1,2]" });
        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.StartsWith("Your code threw out_of_range at line 5", trace.Errors.Single());
        Assert.Equal(5, trace.ErrorLine);
    }

    [Fact]
    public void An_endless_loop_is_stopped_by_the_budget()
    {
        var result = Record(Write("int spin(int n) {\n    while (true) n++;\n    return n;\n}\n"), "spin", new[] { "1" });
        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
    }

    [Fact]
    public void Runaway_recursion_is_stopped_at_the_depth_limit()
    {
        var result = Record(Write("int down(int n) {\n    return down(n - 1) + 1;\n}\n"), "down", new[] { "5" });
        Assert.Equal(RunStatus.RecursionLimitExceeded, result.Status);
        Assert.Contains("base case", result.Errors.Single());
    }

    // ---- measuring -----------------------------------------------------------------------------

    [Theory]
    [InlineData("two_sum.cpp", "Solution.twoSum", GenerationStrategy.Distinct, ComplexityClass.Linear)]
    [InlineData("dedupe_with_find.cpp", "Solution.countDistinct", GenerationStrategy.Distinct, ComplexityClass.Quadratic)]
    [InlineData("climb_stairs.cpp", "Solution.climbStairs", null, ComplexityClass.Linear)]
    [InlineData("fib.cpp", "fib", null, ComplexityClass.Exponential)]
    [InlineData("group_count.cpp", "Solution.byFrequency", null, ComplexityClass.Linearithmic)]
    [InlineData("unique_paths.cpp", "Solution.uniquePaths", null, ComplexityClass.Quadratic)]
    public void Growth_is_measured_as_for_csharp_and_python(string file, string entry, GenerationStrategy? strategy, ComplexityClass expected)
    {
        var result = Measure(Sample(file), entry, strategy);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(expected, result.Empirical.Best);
    }

    [Fact]
    public void A_scan_hidden_in_the_library_is_named()
    {
        var result = Measure(Sample("dedupe_with_find.cpp"), "Solution.countDistinct", GenerationStrategy.Distinct);
        Assert.Contains("std::find is O(n) per call", result.Derivation!.Summary);
    }

    [Fact]
    public void Memory_is_measured_exactly_and_held_space_is_read_from_the_peak()
    {
        var result = Measure(Sample("level_order.cpp"), "Solution.levelOrder");
        Assert.Equal(ComplexityClass.Linear, result.Space!.LikelyPeak);
        Assert.StartsWith("Exact", result.Detail!.MemoryModel);
    }

    [Theory]
    [InlineData("max_depth.cpp", "Solution.maxDepth", ComplexityClass.Linear)]
    [InlineData("climb_stairs.cpp", "Solution.climbStairs", ComplexityClass.Linear)]
    [InlineData("fib.cpp", "fib", ComplexityClass.Exponential)]
    [InlineData("binary_search.cpp", "Solution.search", ComplexityClass.Logarithmic)]
    [InlineData("dedupe_with_find.cpp", "Solution.countDistinct", ComplexityClass.Quadratic)]
    public void The_code_is_read_for_a_second_opinion(string file, string entry, ComplexityClass expected)
    {
        var solution = CppSolution.Read(File.ReadAllText(Sample(file)), entry);
        Assert.Equal(expected, CppEstimator.Time(solution.File, solution.Function!).Class);
    }

    // ---- files from the LeetCode extension -------------------------------------------------------

    [Fact]
    public void A_file_in_the_LeetCode_extensions_layout_runs_its_example()
    {
        var report = Job(new JobRequest
        {
            File = Path.Combine(AppContext.BaseDirectory, "samples", "leetcode-extension", "104.maximum-depth-of-binary-tree.cpp"),
            Entry = "Solution.maxDepth",
            AllExamples = true
        }).Examples!;
        Assert.True(Assert.Single(report.Runs).Matches);
    }
}
