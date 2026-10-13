using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// LeetCode design problems — a class, and an example listing the calls to make on it —
/// run in C# and Python, with every result checked against the example's Output.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class DesignTests
{
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "design", name);

    private static StepTrace Record(string file, string entry, string?[]? values = null)
    {
        var response = JobRunner.Run(new JobRequest { File = file, Entry = entry, StepSize = 8, Values = values });
        Assert.True(response.Steps is not null, string.Join("; ", response.Errors));
        return response.Steps!;
    }

    private static string Write(string source, string extension)
    {
        string folder = Path.Combine(Path.GetTempPath(), "dsaviz-design-tests");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"d{Guid.NewGuid():N}{extension}");
        File.WriteAllText(file, source);
        return file;
    }

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "LRUCache.cs", "LRUCache", new[] { "LRUCache", "Put", "Get" } },
        new object[] { "MinStack.cs", "MinStack", new[] { "Push", "GetMin", "Pop", "Top" } },
        new object[] { "lru_cache.py", "LRUCache", new[] { "__init__", "put", "get" } },
        new object[] { "min_stack.py", "MinStack", new[] { "__init__", "push", "getMin", "pop", "top" } }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_calls_run_in_order_and_the_results_are_checked(string file, string entry, string[] methods)
    {
        var trace = Record(SamplePath(file), entry);

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal(new[] { "operations", "arguments" }, trace.Inputs.Select(i => i.Name));
        Assert.All(trace.Inputs, i => Assert.Equal(InputSource.Example, i.Source));
        Assert.True(trace.Check?.Matches, $"expected {trace.Check?.Expected}, got {trace.ReturnValue}");
        // The run view shows which call is running: each step belongs to the method making it.
        Assert.Equal(methods.OrderBy(m => m), trace.Steps.Select(s => s.Method).Distinct().OrderBy(m => m));

        // Where each call began, in order, so the run view can tell one from the next.
        Assert.Equal(trace.Inputs[0].Literal.Count(c => c == ','), trace.Calls.Count - 1);
        Assert.Equal(Enumerable.Range(0, trace.Calls.Count), trace.Calls.Select(c => c.Index));
        Assert.True(trace.Calls.Zip(trace.Calls.Skip(1)).All(pair => pair.First.Step <= pair.Second.Step));
        Assert.Equal(entry, trace.Calls[0].Operation);
    }

    [Theory]
    [InlineData("LRUCache.cs")]
    [InlineData("MinStack.cs")]
    public void The_class_is_offered_once_and_can_be_measured_per_call(string file)
    {
        var entry = Assert.Single(EntryPointScanner.Scan(File.ReadAllText(SamplePath(file))));
        Assert.True(entry.Design);
        Assert.True(entry.Measurable, entry.Reason);
        Assert.True(entry.Generatable, "its example's calls can be repeated at bigger sizes");
        Assert.Equal(Path.GetFileNameWithoutExtension(file), entry.Qualified);
    }

    [Fact]
    public void Python_design_classes_are_offered_once()
    {
        var response = JobRunner.Run(new JobRequest { File = SamplePath("lru_cache.py"), Entry = "LRUCache", AllExamples = true });
        Assert.True(response.Examples!.Runs.Single().Matches);
    }

    [Theory]
    [InlineData("LRUCache.cs")]
    [InlineData("lru_cache.py")]
    public void Run_all_examples_checks_a_design_problem(string file)
    {
        var report = JobRunner.Run(new JobRequest { File = SamplePath(file), Entry = "LRUCache", AllExamples = true }).Examples!;
        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.True(Assert.Single(report.Runs).Matches);
    }

    [Theory]
    [InlineData("LRUCache.cs")]
    [InlineData("lru_cache.py")]
    public void Typed_calls_replace_the_example(string file)
    {
        var trace = Record(SamplePath(file), "LRUCache", new[] { "[\"LRUCache\",\"put\",\"get\",\"get\"]", "[[1],[5,50],[5],[6]]" });

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal("[null,null,50,-1]", trace.ReturnValue);
        Assert.Null(trace.Check);
    }

    [Theory]
    [InlineData("[\"LRUCache\",\"put\",\"gett\"]", "[[1],[1,1],[1]]", "Operation 3 is \"gett\", but LRUCache has no public method")]
    [InlineData("[\"Cache\",\"get\"]", "[[1],[1]]", "must be \"LRUCache\", not \"Cache\"")]
    [InlineData("[\"LRUCache\",\"put\"]", "[[1],[1]]", "Operation 2 calls Put with 1 argument, but it takes 2 arguments")]
    [InlineData("[\"LRUCache\",\"get\"]", "[[1]]", "2 operations but 1 argument lists")]
    public void Calls_that_do_not_fit_the_class_are_named_under_the_arguments(string operations, string arguments, string message)
    {
        var trace = Record(SamplePath("LRUCache.cs"), "LRUCache", new[] { operations, arguments });

        Assert.Equal(RunStatus.InvalidInput, trace.Status);
        Assert.Contains(message, trace.Inputs[1].Error);
        Assert.Empty(trace.Steps);
    }

    [Fact]
    public void In_Python_calls_that_do_not_fit_the_class_are_explained()
    {
        var trace = Record(SamplePath("lru_cache.py"), "LRUCache", new[] { "[\"LRUCache\",\"put\",\"gett\"]", "[[1],[1,1],[1]]" });

        Assert.Equal(RunStatus.InvalidInput, trace.Status);
        Assert.Contains("Operation 3 is \"gett\", but LRUCache has no method by that name.", trace.Errors.Single());
    }

    [Theory]
    [InlineData(".cs", """
        // Input
        // ["Counter","next","next"]
        // [[],[],[]]
        // Output
        // [null,1,2]
        public class Counter
        {
            private int count;
            public int Next()
            {
                count++;
                if (count == 2) throw new InvalidOperationException("two");
                return count;
            }
        }
        """, 12)]
    [InlineData(".py", """
        # Input
        # ["Counter","next","next"]
        # [[],[],[]]
        # Output
        # [null,1,2]
        class Counter:
            def __init__(self):
                self.count = 0

            def next(self):
                self.count += 1
                if self.count == 2:
                    raise ValueError("two")
                return self.count
        """, 13)]
    public void An_exception_in_a_call_names_the_line_it_was_thrown_on(string extension, string source, int line)
    {
        var trace = Record(Write(source, extension), "Counter");

        Assert.Equal(RunStatus.RuntimeError, trace.Status);
        Assert.Equal(line, trace.ErrorLine);
        Assert.Contains($"at line {line}", trace.Errors.Single());
    }

    [Fact]
    public void A_class_created_with_arguments_and_no_example_says_to_add_one()
    {
        var entry = Assert.Single(EntryPointScanner.Scan("""
            public class Counter { public Counter(int start) { } public int Next() => 1; }
            """));
        Assert.True(entry.Design);
        Assert.False(entry.Measurable);
        Assert.Contains("Add LeetCode's example as a comment", entry.Reason);
    }

    [Theory]
    [InlineData("LRUCache.cs")]
    [InlineData("lru_cache.py")]
    public void Complexity_is_measured_per_call(string file)
    {
        var result = JobRunner.Run(new JobRequest { File = SamplePath(file), Entry = "LRUCache" }).Primary!;
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new[] { "put", "get" }, result.Design!.Operations.Select(o => o.Name));
        Assert.All(result.Design.Operations, o => Assert.Equal(ComplexityClass.Constant, o.PerCall));
    }

    [Fact]
    public void The_example_is_read_however_LeetCode_lays_it_out()
    {
        // Input with a colon and the operations on the same line; arguments over two lines.
        var lines = """
            Example 2:
            Input: ["MinStack","push",
                    "getMin"]
            [[],[-2],
             []]
            Output: [null,null,-2]
            Explanation
            MinStack minStack = new MinStack();
            """.Split('\n').Select((text, i) => (text.Trim(), i + 1)).ToList();

        var example = Assert.Single(DesignExamples.FromCommentLines(lines, "MinStack"));
        Assert.Equal("Example 2", example.Label);
        Assert.Equal("[\"MinStack\",\"push\", \"getMin\"]", example.Values[0]);
        Assert.Equal("[[],[-2], []]", example.Values[1]);
        Assert.Equal("[null,null,-2]", example.Expected);
        Assert.Empty(DesignExamples.FromCommentLines(lines, "LRUCache"));
    }

    [Fact]
    public void An_ordinary_example_is_not_taken_for_a_design_one()
    {
        var lines = new List<(string, int)> { ("Input: nums = [2,7,11,15], target = 9", 1), ("Output: [0,1]", 2) };
        Assert.Empty(DesignExamples.ClassesNamed(lines));
    }
}
