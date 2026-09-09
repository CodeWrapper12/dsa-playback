using System;

// Writes to stdout, so the differential test compares captured output too.
public static class ConsoleOutputSample
{
    public static object Solve()
    {
        int total = 0;
        for (int i = 1; i <= 5; i++)
        {
            total += i;
            Console.WriteLine($"step {i} total {total}");
        }
        return total;
    }
}
