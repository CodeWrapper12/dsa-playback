using DsaViz.Engine;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// "Visualise how it works based on my code, like when debugging or running" — the first
/// thing the original request asked for. A recorded run, one statement at a time, with
/// every variable the statement can see.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class StepThroughTests
{
    private static StepTrace Record(string sample, string entry, int size, GenerationStrategy? strategy = null, int maxSteps = 2_000)
        => Stepper.Record(SourceAnalysis.Create(Samples.Read(sample)), entry, inputs: null, size, maxSteps, strategy);

    private static string? Value(ExecutionStep step, string name) => step.Variables.FirstOrDefault(v => v.Name == name)?.Display;

    [Fact]
    public void A_binary_search_can_be_followed_step_by_step()
    {
        var trace = Record("BinarySearchParamSample.cs", "Search", 8, GenerationStrategy.Sorted);

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal("0", trace.ReturnValue);

        // The range halves exactly as the textbook says: mid 3, then 1, then 0.
        var mids = trace.Steps.Where(s => Value(s, "mid") is not null && trace.Statements[s.StatementId].Snippet.StartsWith("if (sorted[mid] =="))
            .Select(s => Value(s, "mid")).ToList();
        Assert.Equal(new[] { "3", "1", "0" }, mids);
    }

    [Fact]
    public void A_variable_appears_only_once_it_has_a_value()
    {
        // Reading a variable before it is definitely assigned is a compile error, so the
        // capture is driven by data-flow analysis rather than by what is merely in scope.
        var trace = Record("BinarySearchParamSample.cs", "Search", 8, GenerationStrategy.Sorted);

        var declaring = trace.Steps.First(s => trace.Statements[s.StatementId].Snippet.StartsWith("int mid ="));
        var next = trace.Steps[declaring.Index + 1];

        Assert.Null(Value(declaring, "mid"));
        Assert.NotNull(Value(next, "mid"));
    }

    [Fact]
    public void Values_are_snapshots_not_references()
    {
        // An array captured by reference would show its sorted end state at every step.
        var trace = Record("SortParamSample.cs", "Sort", 6, GenerationStrategy.ReverseSorted);

        string? first = StepHeap.Display(trace, trace.Steps.First(), "values");
        string? last = StepHeap.Display(trace, trace.Steps.Last(), "values");

        Assert.Equal("[6, 5, 4, 3, 2, 1]", first);
        Assert.Equal("[1, 2, 3, 4, 5, 6]", last);
    }

    [Fact]
    public void Arguments_are_shown_as_they_were_passed_in()
    {
        var trace = Record("SortParamSample.cs", "Sort", 5, GenerationStrategy.ReverseSorted);

        Assert.Equal("[5, 4, 3, 2, 1]", trace.Arguments.Single().Display);
        Assert.Equal("[1,2,3,4,5]", trace.ReturnValue);
    }

    [Fact]
    public void Arrays_and_indices_carry_what_is_needed_to_draw_pointers()
    {
        var trace = Record("SortParamSample.cs", "Sort", 5, GenerationStrategy.Random);
        var step = trace.Steps.First(s => s.Variables.Any(v => v.Name == "j"));

        var values = step.Variables.Single(v => v.Name == "values");
        var j = step.Variables.Single(v => v.Name == "j");

        Assert.Equal("array", values.Kind);
        Assert.NotNull(values.Ref);
        Assert.Equal(5, StepHeap.Object(trace, step, "values").Items!.Count);
        Assert.Equal("scalar", j.Kind);
        Assert.NotNull(j.Number);
    }

    [Fact]
    public void An_instance_methods_fields_are_captured()
    {
        // The memo table lives on the Solution object, not in a local.
        var trace = Record("leetcode/ClimbStairsMemo.cs", "ClimbStairs", 6);

        Assert.Equal(RunStatus.Completed, trace.Status);
        var memoSizes = trace.Steps.Select(s => StepHeap.Display(trace, s, "memo")).Where(v => v is not null).Distinct().ToList();
        Assert.Contains("{}", memoSizes);
        Assert.True(memoSizes.Count > 2, "the memo should fill up as the run goes");
    }

    [Fact]
    public void A_method_without_parameters_can_be_followed()
    {
        // Issue #1: nothing to pass in left the arguments empty, and the recording read the
        // first one anyway.
        var analysis = SourceAnalysis.Create("""
            public static class Arrays
            {
                public static int[] IntArray0() => new int[0];
            }
            """);
        var trace = Stepper.Record(analysis, "IntArray0");

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal("[]", trace.ReturnValue);
        Assert.Empty(trace.Arguments);
    }

    [Fact]
    public void Recursion_is_recorded_with_its_depth_and_method()
    {
        var trace = Record("leetcode/MaxDepth.cs", "MaxDepth", 7, GenerationStrategy.Skewed);

        Assert.Equal("7", trace.ReturnValue);
        Assert.Equal(8, trace.Steps.Max(s => s.Depth));      // seven nodes, then the null below the last
        Assert.All(trace.Steps, s => Assert.Equal("MaxDepth", s.Method));
        Assert.Equal("tree", trace.Arguments.Single().Kind);
    }

    [Fact]
    public void A_long_run_is_capped_and_says_so()
    {
        var trace = Record("FibonacciParamSample.cs", "Fib", 20, maxSteps: 100);

        Assert.Equal(RunStatus.Completed, trace.Status);
        Assert.Equal(100, trace.Steps.Count);
        Assert.True(trace.Truncated);
        Assert.Equal("6765", trace.ReturnValue);              // capped recording, not a capped run
    }

    [Fact]
    public void The_step_build_never_touches_the_measurement_build()
    {
        // Recording every variable allocates. Kept in a separate build, it cannot show up
        // in measured memory: the in-place sort still allocates nothing when measured.
        Record("SortParamSample.cs", "Sort", 8);
        var measured = Measurer.Measure(Samples.Read("SortParamSample.cs"), "Sort");

        Assert.Equal(0, measured.Points[^1].BytesAllocated);
    }

    [Theory]
    [MemberData(nameof(BehaviourPreservedTests.Fixtures), MemberType = typeof(BehaviourPreservedTests))]
    public void The_step_build_does_not_change_behaviour(string sample, string entry, GenerationStrategy? strategy)
    {
        // A capture call before every statement must change nothing the solution does.
        string source = Samples.Read(sample);
        var analysis = SourceAnalysis.Create(source);

        var plain = Runner.Compile(analysis, entry, instrument: false);
        var stepped = Runner.Compile(analysis, entry, instrument: true, stepping: true);
        Assert.True(stepped.Success, string.Join("; ", stepped.Errors));

        using var baseline = plain.Solution!;
        using var recorder = stepped.Solution!;
        var specs = Measurer.ApplyStrategy(ParameterBinder.Infer(baseline.Parameters), strategy);

        foreach (int size in new[] { 0, 1, 6 })
        {
            var expected = Runner.Invoke(baseline, ParameterBinder.Bind(specs, baseline.Parameters, size));
            var actual = Runner.Invoke(recorder, ParameterBinder.Bind(specs, recorder.Parameters, size),
                recording: new StepRecording(new HeapRecorder(recorder.Statements), 500));

            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.ReturnValue, actual.ReturnValue);
        }
    }
}

public class StepRecorderIsolationTests
{
    private sealed class BrokenObserver : DsaViz.Runtime.IStepObserver
    {
        public int Calls;
        public void Step(int statementId, int depth, object?[] values, long allocatedBytes)
        {
            Calls++;
            throw new InvalidOperationException("recorder bug");
        }
        public void Returned(int depth, object? value) => throw new InvalidOperationException("recorder bug");
    }

    [Fact]
    public void A_recorder_that_fails_never_breaks_the_run()
    {
        // The recorder observes; it must not be able to fail the code it observes.
        var observer = new BrokenObserver();
        var state = DsaViz.Runtime.TraceState.BeginStepping(1, 0, 1_000, 10, observer);

        state.Values(state.Step(0), new object[] { 42 });
        state.Values(state.Step(0), new object[] { 43 });
        Assert.Equal(7, state.Returned(7));
        state.End();

        Assert.IsType<InvalidOperationException>(state.ObserverFailure);
        Assert.Equal(1, observer.Calls);   // recording stops at the failure, so what was kept is consistent
    }

    [Fact]
    public void A_dictionary_renders_its_entries()
    {
        var captured = DsaViz.Engine.Inputs.ValueCapture.Capture(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        Assert.Equal("{1: 10, 2: 20}", captured.Display);
    }
}
