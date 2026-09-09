namespace DsaViz.Engine.Tests;

public static class Samples
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "samples");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    /// <summary>
    /// Every sample that is expected to terminate. InfiniteLoopSample is excluded
    /// because a baseline run of it would never return.
    /// </summary>
    public static IEnumerable<object[]> Terminating() => new[]
    {
        new object[] { "BinarySearchSample.cs" },
        new object[] { "EarlyReturnSample.cs" },
        new object[] { "BubbleSortSample.cs" },
        new object[] { "TwoSumSample.cs" },
        new object[] { "FibonacciSample.cs" },
        new object[] { "SwitchSample.cs" },
        new object[] { "ConsoleOutputSample.cs" }
    };
}
