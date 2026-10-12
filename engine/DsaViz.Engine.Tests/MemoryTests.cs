using DsaViz.Engine.Contracts;
using Xunit;

namespace DsaViz.Engine.Tests;

/// <summary>
/// These pin down what the byte numbers actually mean. The counter behind them,
/// GC.GetAllocatedBytesForCurrentThread, is a monotonic cumulative total of bytes
/// allocated, so a collection landing between two statements cannot decrease it and
/// cannot move bytes from one statement to another. The figures are exact, and these
/// tests assert against known object sizes rather than merely "greater than zero" —
/// which would pass even if attribution were badly wrong.
/// </summary>
[Collection(ExactMemoryCollection.Name)]
public class MemoryTests
{
    private const string Allocations = """
        public static class Allocations
        {
            public static object Solve()
            {
                int[] small = new int[100];
                int[] large = new int[1000];
                object[] refs = new object[10];
                return small.Length + large.Length + refs.Length;
            }
        }
        """;

    [Fact]
    public void Allocation_sizes_are_exact()
    {
        var result = Runner.Run(Allocations, "Solve");
        Assert.Equal(RunStatus.Completed, result.Status);

        long BytesFor(string fragment) =>
            result.Statements.Single(s => s.Snippet.Contains(fragment)).BytesAllocated;

        // 24 bytes of object header and length, plus the payload.
        Assert.Equal(24 + 100 * sizeof(int), BytesFor("new int[100]"));
        Assert.Equal(24 + 1000 * sizeof(int), BytesFor("new int[1000]"));
        Assert.Equal(24 + 10 * 8, BytesFor("new object[10]"));
    }

    [Fact]
    public void Allocation_measurement_is_deterministic()
    {
        var runs = Enumerable.Range(0, 5)
            .Select(_ => Runner.Run(Allocations, "Solve")
                .Statements.Select(s => (s.Id, s.BytesAllocated)).ToList())
            .ToList();

        Assert.All(runs, run => Assert.Equal(runs[0], run));
    }

    [Fact]
    public void Bytes_are_counted_when_allocated_even_if_never_retained()
    {
        // Worth stating explicitly because it is the one place the numbers can
        // surprise someone: this measures allocation pressure, not peak live memory.
        // A loop that allocates and immediately discards still costs — which is
        // exactly the lesson the tool should be teaching.
        var result = Runner.Run(
            """
            public static class Garbage
            {
                public static object Solve()
                {
                    int total = 0;
                    for (int i = 0; i < 100; i++)
                    {
                        int[] temporary = new int[10];
                        total += temporary.Length;
                    }
                    return total;
                }
            }
            """, "Solve");

        Assert.Equal(RunStatus.Completed, result.Status);

        var allocating = result.Statements.Single(s => s.Snippet.Contains("new int[10]"));
        Assert.Equal(100, allocating.Hits);
        Assert.Equal(100 * (24 + 10 * sizeof(int)), allocating.BytesAllocated);
    }

    [Fact]
    public void A_statement_that_allocates_nothing_reports_zero()
    {
        var result = Runner.Run(Samples.Read("BubbleSortSample.cs"), "Solve");

        // The sort itself swaps in place; only the array creation should show bytes.
        var swap = result.Statements.Single(s => s.Snippet.StartsWith("(a[j]"));
        Assert.Equal(0, swap.BytesAllocated);
    }

    [Fact]
    public void The_memory_model_is_described_for_the_UI_to_show()
    {
        var result = Runner.Run(Allocations, "Solve");
        Assert.Contains("allocation, not retention", result.MemoryModel);
    }
}
