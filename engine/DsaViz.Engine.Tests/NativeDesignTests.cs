using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// LeetCode design problems in C++ and Rust: the engine writes the matching from operation
/// names to methods, since neither language can find a method by name at run time. Built
/// with the compilers on this machine, so these run with the other compiled languages' tests.
/// </summary>
[Collection(CppCollection.Name)]
public class NativeDesignTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "design", name);

    private static StepTrace Record(string file, string entry, string?[]? values = null)
    {
        var response = JobRunner.Run(new JobRequest { File = file, Entry = entry, StepSize = 8, Values = values });
        Assert.True(response.Steps is not null, string.Join("; ", response.Errors));
        return response.Steps!;
    }

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "lru_cache.cpp", "LRUCache", new[] { "capacity", "order", "where" } },
        new object[] { "min_stack.cpp", "MinStack", new[] { "items" } },
        new object[] { "lru_cache.rs", "LRUCache", new[] { "self.capacity", "self.values", "self.order" } },
        new object[] { "min_stack.rs", "MinStack", new[] { "self.items" } }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_calls_run_in_order_are_checked_and_show_the_objects_fields(string file, string entry, string[] fields)
    {
        var trace = Record(Sample(file), entry);

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal(new[] { "operations", "arguments" }, trace.Inputs.Select(i => i.Name));
        Assert.True(trace.Check?.Matches, $"expected {trace.Check?.Expected}, got {trace.ReturnValue}");
        Assert.Equal(entry, trace.Calls[0].Operation);
        Assert.Equal(Enumerable.Range(0, trace.Calls.Count), trace.Calls.Select(c => c.Index));
        // Inside a method, the object's state is listed with the other variables.
        Assert.Contains(trace.Steps, s => fields.All(f => s.Variables.Any(v => v.Name == f)));
    }

    [Theory]
    [InlineData("lru_cache.cpp")]
    [InlineData("min_stack.cpp")]
    [InlineData("lru_cache.rs")]
    [InlineData("min_stack.rs")]
    public void Run_all_examples_checks_a_design_problem(string file)
    {
        string entry = file.StartsWith("lru") ? "LRUCache" : "MinStack";
        var report = JobRunner.Run(new JobRequest { File = Sample(file), Entry = entry, AllExamples = true }).Examples!;
        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.True(Assert.Single(report.Runs).Matches);
    }

    [Theory]
    [InlineData("lru_cache.cpp")]
    [InlineData("lru_cache.rs")]
    public void The_class_is_offered_once_and_can_be_measured_per_call(string file)
    {
        var entries = JobRunnerEntries(Sample(file));
        var entry = Assert.Single(entries);
        Assert.True(entry.Design);
        Assert.True(entry.Generatable, "its example's calls can be repeated at bigger sizes");
        Assert.Equal("LRUCache", entry.Qualified);
    }

    private static IReadOnlyList<EntryPoint> JobRunnerEntries(string file)
        => file.EndsWith(".rs")
            ? Rust.RustSolution.Entries(Rust.RustReader.Read(File.ReadAllText(file)))
            : Cpp.CppSolution.Entries(Cpp.CppReader.Read(File.ReadAllText(file)));

    [Theory]
    [InlineData("lru_cache.cpp")]
    [InlineData("lru_cache.rs")]
    public void Typed_calls_replace_the_example(string file)
    {
        var trace = Record(Sample(file), "LRUCache", new[] { "[\"LRUCache\",\"put\",\"get\",\"get\"]", "[[1],[5,50],[5],[6]]" });
        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal("[null,null,50,-1]", trace.ReturnValue);
        Assert.Null(trace.Check);
    }

    [Theory]
    [InlineData("lru_cache.cpp", "[\"LRUCache\",\"put\",\"gett\"]", "[[1],[1,1],[1]]", "Operation 3 is \"gett\", but LRUCache has no")]
    [InlineData("lru_cache.rs", "[\"LRUCache\",\"put\",\"gett\"]", "[[1],[1,1],[1]]", "Operation 3 is \"gett\", but LRUCache has no")]
    [InlineData("lru_cache.cpp", "[\"Cache\",\"get\"]", "[[1],[1]]", "must be \"LRUCache\", not \"Cache\"")]
    [InlineData("lru_cache.rs", "[\"Cache\",\"get\"]", "[[1],[1]]", "must be \"LRUCache\", not \"Cache\"")]
    [InlineData("lru_cache.cpp", "[\"LRUCache\",\"put\"]", "[[1],[1]]", "Operation 2 calls put with 1 argument, but it takes 2 arguments")]
    [InlineData("lru_cache.rs", "[\"LRUCache\",\"put\"]", "[[1],[1]]", "Operation 2 calls put with 1 argument, but it takes 2 arguments")]
    [InlineData("lru_cache.cpp", "[\"LRUCache\",\"get\"]", "[[1]]", "2 operations but 1 argument lists")]
    [InlineData("lru_cache.rs", "[\"LRUCache\",\"get\"]", "[[1]]", "2 operations but 1 argument lists")]
    public void Calls_that_do_not_fit_the_class_are_explained_and_nothing_runs(string file, string operations, string arguments, string message)
    {
        var trace = Record(Sample(file), "LRUCache", new[] { operations, arguments });
        Assert.Equal(RunStatus.InvalidInput, trace.Status);
        // Checked before the first call: not even the constructor ran.
        Assert.Empty(trace.Steps);
        var said = string.Join(" ", trace.Errors.Concat(trace.Inputs.Select(i => i.Error ?? "")));
        Assert.Contains(message, said);
    }

    [Fact]
    public void A_cpp_iterator_is_named_as_one()
    {
        var trace = Record(Sample("lru_cache.cpp"), "LRUCache");
        Assert.Contains(trace.Steps, s => s.Variables.Any(v => v.Name == "found" && v.Display == "(iterator)"));
    }

    [Fact]
    public void A_rust_match_arm_with_a_block_ends_at_its_brace()
    {
        // Some(x) => { .. } followed by None => -1 with no comma between them: two arms.
        var file = Rust.RustReader.Read("fn f(v: Option<i32>) -> i32 {\n    match v {\n        Some(x) => { x }\n        None => -1,\n    }\n}\n");
        var match = file.Functions.Single().Body.Children.Single();
        Assert.Equal(2, match.Arms.Count);
    }
}
