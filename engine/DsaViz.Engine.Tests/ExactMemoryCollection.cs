using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Byte-exact assertions run on their own.
///
/// The engine measures in a process of its own, where the only thread allocating is the
/// one being measured. A garbage collection that thread sets off retires an allocation
/// block that is already full, so it adds nothing. xUnit, by default, runs test classes in
/// parallel inside one process, and a collection set off by another test's thread retires
/// this thread's half-used block and counts its remainder — up to about 8 KB — as
/// allocated. That was reproduced deliberately: three threads allocating continuously
/// produced 7,296 and 8,160-byte phantoms on an in-place sort. These tests assert exact
/// figures, so they run alone, in the conditions the engine actually measures in.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExactMemoryCollection
{
    public const string Name = "Exact memory";
}

[Collection(ExactMemoryCollection.Name)]
public class ExactMemoryTests
{
    [Fact]
    public void An_in_place_sort_allocates_nothing_on_every_run()
    {
        // Ten measurements, seven sizes each: seventy chances for a stray byte.
        for (int round = 0; round < 10; round++)
        {
            var result = Measurer.Measure(Samples.Read("SortParamSample.cs"), "Sort");
            Assert.All(result.Points, point => Assert.Equal(0, point.BytesAllocated));
        }
    }
}

/// <summary>The retry policy, tested without depending on when a real collection happens.</summary>
public class CleanestReadingTests
{
    private static TraceResult Reading(long bytes, int collections, RunStatus status = RunStatus.Completed) => new()
    {
        Status = status,
        Statements = Array.Empty<StatementCost>(),
        Lines = Array.Empty<LineCost>(),
        TotalOperations = 1,
        TotalBytesAllocated = bytes,
        ElapsedMs = 0,
        CollectionsDuringRun = collections
    };

    [Fact]
    public void A_clean_first_reading_is_not_repeated()
    {
        int calls = 0;
        var result = Runner.Cleanest(() => { calls++; return Reading(400, 0); });
        Assert.Equal(1, calls);
        Assert.Equal(400, result.TotalBytesAllocated);
    }

    [Fact]
    public void A_small_reading_that_saw_a_collection_is_repeated_until_clean()
    {
        var readings = new Queue<TraceResult>(new[] { Reading(8_160, 1), Reading(7_296, 1), Reading(0, 0) });
        var result = Runner.Cleanest(() => readings.Dequeue());
        Assert.Equal(0, result.TotalBytesAllocated);
    }

    [Fact]
    public void Without_a_clean_reading_the_smallest_is_kept()
    {
        // Phantoms only ever add bytes, so the smallest reading is the most faithful.
        var readings = new Queue<TraceResult>(new[]
            { Reading(8_160, 1), Reading(424, 2), Reading(7_720, 1), Reading(8_000, 1), Reading(8_100, 1) });
        var result = Runner.Cleanest(() => readings.Dequeue());
        Assert.Equal(424, result.TotalBytesAllocated);
    }

    [Fact]
    public void A_large_allocation_is_not_repeated_for_a_phantom_under_one_percent()
    {
        int calls = 0;
        Runner.Cleanest(() => { calls++; return Reading(5_000_000, 3); });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_run_that_behaves_differently_on_repeat_is_not_treated_as_noise()
    {
        var readings = new Queue<TraceResult>(new[] { Reading(8_160, 1), Reading(0, 0, RunStatus.RuntimeError) });
        var result = Runner.Cleanest(() => readings.Dequeue());
        Assert.Equal(RunStatus.Completed, result.Status);
    }
}
