using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using DsaViz.Engine.Rust;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Rust solutions, read, built with the rustc on this machine, run and measured. These need
/// Rust installed; without it they fail, rather than pass while testing nothing. They share
/// the C++ tests' collection: both compile with every core, and run apart from timing tests.
/// </summary>
[Collection(CppCollection.Name)]
public class RustTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "rust", name);

    private static string Write(string source)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-rust-tests");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"s{Guid.NewGuid():N}.rs");
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

    /// <summary>What each variable showed at each step: name to display, in step order.</summary>
    private static List<Dictionary<string, string>> Shown(StepTrace trace)
        => trace.Steps.Select(s => s.Variables.ToDictionary(v => v.Name, v => v.Display)).ToList();

    // ---- reading ---------------------------------------------------------------------------------

    [Fact]
    public void LeetCodes_template_is_read_without_struct_Solution()
    {
        var file = RustReader.Read(File.ReadAllText(Sample("two_sum.rs")));
        Assert.Null(file.Problem);
        var entry = Assert.Single(RustSolution.Entries(file));
        Assert.Equal("Solution.two_sum", entry.Qualified);
        Assert.True(entry.Measurable, entry.Reason);
        Assert.Equal(new[] { "Vec<i32>", "i32" }, entry.Parameters.Select(p => p.Type));
    }

    [Fact]
    public void Private_helpers_and_nested_functions_are_not_offered()
    {
        var entries = RustSolution.Entries(RustReader.Read("""
            impl Solution {
                pub fn solve(n: i32) -> i32 { fn inner(x: i32) -> i32 { x } Self::helper(inner(n)) }
                fn helper(n: i32) -> i32 { n }
            }
            """));
        Assert.Equal("Solution.solve", Assert.Single(entries).Qualified);
    }

    [Fact]
    public void A_design_problem_says_it_is_not_supported_yet()
    {
        var entries = RustSolution.Entries(RustReader.Read("""
            struct MinStack { items: Vec<i32> }
            impl MinStack {
                fn new() -> Self { MinStack { items: vec![] } }
                fn push(&mut self, val: i32) { self.items.push(val); }
            }
            """));
        var push = entries.Single(e => e.Name == "push");
        Assert.False(push.Measurable);
        Assert.Contains("Design problems in Rust are not supported yet", push.Reason);
    }

    [Fact]
    public void A_block_like_statement_ends_at_its_brace()
    {
        // while ... { } followed by the tail expression -1: two statements, as Rust parses it.
        var file = RustReader.Read("fn f(mut n: i32) -> i32 {\n    while n > 0 { n -= 1; }\n    -1\n}\n");
        var body = file.Functions.Single().Body;
        Assert.Equal(new[] { RsStatementKind.While, RsStatementKind.Expression }, body.Children.Select(c => c.Kind));
        Assert.True(body.Children[1].IsTail);
    }

    [Fact]
    public void Patterns_bind_names_and_not_variants_or_fields()
    {
        var file = RustReader.Read("""
            fn f(v: Vec<(i32, i32)>) -> i32 {
                let (mut a, b) = (1, 2);
                let Point { x, y: z } = p;
                for (i, &(k, _)) in v.iter().enumerate() { }
                if let Some(Ok(w)) = q { }
                0
            }
            """);
        var body = file.Functions.Single().Body.Children;
        Assert.Equal(new[] { "a", "b" }, body[0].Declares);
        Assert.Equal(new[] { "x", "z" }, body[1].Declares);
        Assert.Equal(new[] { "i", "k" }, body[2].BodyDeclares);
        Assert.Equal(new[] { "w" }, body[3].BodyDeclares);
    }

    // ---- running ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "two_sum.rs", "Solution.two_sum" }, new object[] { "reverse_list.rs", "Solution.reverse_list" },
        new object[] { "max_depth.rs", "Solution.max_depth" }, new object[] { "level_order.rs", "Solution.level_order" },
        new object[] { "num_islands.rs", "Solution.num_islands" }, new object[] { "valid_parentheses.rs", "Solution.is_valid" },
        new object[] { "kth_largest.rs", "Solution.find_kth_largest" }, new object[] { "binary_search.rs", "Solution.search" },
        new object[] { "group_count.rs", "Solution.by_frequency" }, new object[] { "longest_substring.rs", "Solution.length_of_longest_substring" },
        new object[] { "fib.rs", "fib" }, new object[] { "climb_stairs.rs", "Solution.climb_stairs" },
        new object[] { "unique_paths.rs", "Solution.unique_paths" }, new object[] { "ownership.rs", "Solution.ownership" }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_runs_its_example_and_matches(string file, string entry)
    {
        var report = Job(new JobRequest { File = Sample(file), Entry = entry, AllExamples = true }).Examples!;
        Assert.True(report.Status == RunStatus.Completed, string.Join("; ", report.Errors));
        Assert.NotEmpty(report.Runs);
        Assert.All(report.Runs, run => Assert.True(run.Matches == true, $"{run.Label}: expected {run.Expected}, got {run.Actual}. {run.Error}"));
    }

    [Fact]
    public void A_step_shows_the_variables_and_the_objects_they_hold()
    {
        var trace = Record(Sample("two_sum.rs"), "Solution.two_sum");

        Assert.Equal(RunStatus.Completed, trace.Status);
        var last = trace.Steps[^1];
        Assert.Equal(new[] { "nums", "target", "seen", "i", "x", "j" }, last.Variables.Select(v => v.Name));
        Assert.Equal("array", last.Variables[0].Kind);
        Assert.Equal("map", last.Variables[2].Kind);
        Assert.Equal(1L, last.Variables[3].Number);
        Assert.Equal("[0,1]", trace.ReturnValue);
        Assert.NotEmpty(trace.Returns);
    }

    [Fact]
    public void Ownership_is_shown_as_it_happens()
    {
        // let mut owned = values;           line 8
        // let largest = owned.iter_mut()…   line 9
        // *largest += 1;                    line 10
        // let total = owned.iter().sum();   line 11
        // let mut sorted = owned;           line 12
        // sorted.sort(); let label; …       lines 13–
        var trace = Record(Sample("ownership.rs"), "Solution.ownership");
        Assert.Equal(RunStatus.Completed, trace.Status);
        var at = trace.Steps.Select((s, i) => (s.Line, Shown: Shown(trace)[i])).ToDictionary(x => x.Line, x => x.Shown);

        Assert.Equal("(moved)", at[9]["values"]);
        Assert.Equal("(borrowed)", at[10]["owned"]);              // largest still points into it
        Assert.Equal("3", at[10]["largest"]);
        Assert.StartsWith("Vec<i32>", at[11]["owned"]);           // the borrow has ended
        Assert.Equal("(no longer used)", at[11]["largest"]);
        Assert.Equal("(moved)", at[13]["owned"]);
        Assert.Equal("(not set yet)", at[15]["label"]);
        Assert.Equal("\"positive\"", at[20]["label"]);
    }

    [Fact]
    public void A_variable_moved_and_set_again_is_readable_again()
    {
        var trace = Record(Sample("reverse_list.rs"), "Solution.reverse_list");
        var shown = Shown(trace);
        // curr is moved by `while let Some(mut node) = curr`, and set again on the next line.
        Assert.Contains(shown, s => s.GetValueOrDefault("curr") == "(moved)");
        Assert.Contains(shown, s => s.GetValueOrDefault("curr") == "ListNode(2)");
        Assert.All(shown.Skip(2), s => Assert.Equal("(moved)", s["head"]));
    }

    [Fact]
    public void A_Vec_used_only_with_push_and_pop_is_drawn_as_a_stack()
    {
        var trace = Record(Sample("valid_parentheses.rs"), "Solution.is_valid", new[] { "\"(([\"" });
        var stack = trace.Steps.SelectMany(s => s.Heap ?? Array.Empty<HeapObject>()).Where(h => h.Kind == "stack").ToList();
        Assert.NotEmpty(stack);
        // Top first: after pushing ( ( [, the [ is on top.
        Assert.Equal("[", stack.Last(h => h.Items is { Count: 3 }).Items![0].Text);
    }

    [Fact]
    public void A_compile_error_names_the_students_own_line()
    {
        var trace = Record(Write("impl Solution {\n    pub fn f(n: i32) -> i32 {\n        let m: i32 = \"no\";\n        m + n\n    }\n}\n"), "Solution.f");
        Assert.Equal(RunStatus.CompileError, trace.Status);
        Assert.StartsWith("line 3, column", trace.Errors[0]);
        Assert.Contains("[E0308]", trace.Errors[0]);
        Assert.DoesNotContain(trace.Errors, e => e.Contains("dsaviz"));
    }

    [Fact]
    public void A_panic_names_its_line_and_what_it_said()
    {
        var trace = Record(Write("""
            impl Solution {
                pub fn f(v: Vec<i32>) -> i32 {
                    let mut total = 0;
                    total += v[10];
                    total
                }
            }
            """), "Solution.f", new[] { "[1,2]" });
        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Equal("Your code panicked at line 4: index out of bounds: the len is 2 but the index is 10", trace.Errors.Single());
        Assert.Equal(4, trace.ErrorLine);
    }

    [Fact]
    public void An_overflow_panics_when_stepping_and_wraps_when_measuring()
    {
        string file = Write("fn grow(n: i32) -> i32 {\n    let mut x: i32 = 1;\n    for _ in 0..n { x *= 3; }\n    x\n}\n");
        var stepped = Record(file, "grow", new[] { "40" });
        Assert.Contains("attempt to multiply with overflow", stepped.Errors.Single());

        var measured = Measure(file, "grow");
        Assert.Equal(RunStatus.Completed, measured.Status);
        Assert.Equal(ComplexityClass.Linear, measured.Empirical.Best);
    }

    [Fact]
    public void An_endless_loop_is_stopped_by_the_budget()
    {
        var result = Record(Write("fn spin(mut n: i32) -> i32 {\n    loop { n = n.wrapping_add(1); }\n}\n"), "spin", new[] { "1" });
        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
    }

    [Fact]
    public void Runaway_recursion_is_stopped_at_the_depth_limit()
    {
        var result = Record(Write("fn down(n: i64) -> i64 {\n    down(n - 1) + 1\n}\n"), "down", new[] { "5" });
        Assert.Equal(RunStatus.RecursionLimitExceeded, result.Status);
        Assert.Contains("base case", result.Errors.Single());
    }

    // ---- measuring -----------------------------------------------------------------------------

    [Theory]
    [InlineData("two_sum.rs", "Solution.two_sum", GenerationStrategy.Distinct, ComplexityClass.Linear)]
    [InlineData("dedupe_with_contains.rs", "Solution.count_distinct", GenerationStrategy.Distinct, ComplexityClass.Quadratic)]
    [InlineData("climb_stairs.rs", "Solution.climb_stairs", null, ComplexityClass.Linear)]
    [InlineData("fib.rs", "fib", null, ComplexityClass.Exponential)]
    [InlineData("group_count.rs", "Solution.by_frequency", null, ComplexityClass.Linearithmic)]
    [InlineData("unique_paths.rs", "Solution.unique_paths", null, ComplexityClass.Quadratic)]
    [InlineData("reverse_list.rs", "Solution.reverse_list", null, ComplexityClass.Linear)]
    public void Growth_is_measured_as_for_the_other_languages(string file, string entry, GenerationStrategy? strategy, ComplexityClass expected)
    {
        var result = Measure(Sample(file), entry, strategy);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(expected, result.Empirical.Best);
    }

    [Fact]
    public void A_scan_hidden_in_the_library_is_named_and_a_sets_contains_is_not_charged()
    {
        var vec = Measure(Sample("dedupe_with_contains.rs"), "Solution.count_distinct", GenerationStrategy.Distinct);
        Assert.Contains("contains is O(n) per call", vec.Derivation!.Summary);

        // The same code with a HashSet: contains is O(1), and the run is linear.
        var set = Measure(Write(File.ReadAllText(Sample("dedupe_with_contains.rs"))
            .Replace("let mut seen: Vec<i32> = Vec::new();", "let mut seen = std::collections::HashSet::new();")
            .Replace("seen.push(value);", "seen.insert(value);")), "Solution.count_distinct", GenerationStrategy.Distinct);
        Assert.Equal(ComplexityClass.Linear, set.Empirical.Best);
    }

    [Fact]
    public void Memory_is_exact_and_held_space_is_read_from_the_peak()
    {
        var result = Measure(Sample("two_sum.rs"), "Solution.two_sum", GenerationStrategy.Distinct);
        Assert.Equal(ComplexityClass.Linear, result.Space!.LikelyPeak);
        Assert.StartsWith("Exact", result.Detail!.MemoryModel);
    }

    [Theory]
    [InlineData("max_depth.rs", "Solution.max_depth", ComplexityClass.Linear)]
    [InlineData("climb_stairs.rs", "Solution.climb_stairs", ComplexityClass.Linear)]
    [InlineData("fib.rs", "fib", ComplexityClass.Exponential)]
    [InlineData("binary_search.rs", "Solution.search", ComplexityClass.Logarithmic)]
    [InlineData("dedupe_with_contains.rs", "Solution.count_distinct", ComplexityClass.Quadratic)]
    public void The_code_is_read_for_a_second_opinion(string file, string entry, ComplexityClass expected)
    {
        var solution = RustSolution.Read(File.ReadAllText(Sample(file)), entry);
        Assert.Equal(expected, RustEstimator.Time(solution.File, solution.Function!).Class);
    }

    // ---- files from the LeetCode extension -------------------------------------------------------

    [Fact]
    public void A_file_in_the_LeetCode_extensions_layout_runs_its_example()
    {
        var report = Job(new JobRequest
        {
            File = Path.Combine(AppContext.BaseDirectory, "samples", "leetcode-extension", "206.reverse-linked-list.rs"),
            Entry = "Solution.reverse_list",
            AllExamples = true
        }).Examples!;
        Assert.True(Assert.Single(report.Runs).Matches);
    }

    [Fact]
    public void Examples_written_with_LeetCodes_camelCase_names_match_snake_case_parameters()
    {
        var file = Write("""
            // Input: numRows = 3
            // Output: [[1],[1,1],[1,2,1]]
            impl Solution {
                pub fn generate(num_rows: i32) -> Vec<Vec<i32>> {
                    let mut rows: Vec<Vec<i32>> = Vec::new();
                    for i in 0..num_rows as usize {
                        let mut row = vec![1; i + 1];
                        for j in 1..i { row[j] = rows[i - 1][j - 1] + rows[i - 1][j]; }
                        rows.push(row);
                    }
                    rows
                }
            }
            """);
        var report = Job(new JobRequest { File = file, Entry = "Solution.generate", AllExamples = true }).Examples!;
        Assert.True(Assert.Single(report.Runs).Matches);
    }
}
