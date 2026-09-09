using System.Collections.Generic;

// The case that breaks naive instrumentation. Inserting a tracer call into
// `if (n < 0) return -1;` without adding braces first produces
// `if (n < 0) Hit(7); return -1;` — and Classify then returns -1 for every input.
public static class EarlyReturnSample
{
    private static int Classify(int n)
    {
        if (n < 0) return -1;
        if (n == 0) return 0;
        if (n % 2 == 0) return 2;
        return 1;
    }

    public static object Solve()
    {
        var results = new List<int>();
        for (int i = -3; i <= 6; i++) results.Add(Classify(i));
        return results;
    }
}
