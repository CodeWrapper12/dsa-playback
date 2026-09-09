using System.Collections.Generic;

// Expression-bodied recursive method — exercises the arrow-to-block normalisation.
public static class FibonacciSample
{
    private static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    public static object Solve()
    {
        var values = new List<int>();
        for (int i = 0; i <= 18; i++) values.Add(Fib(i));
        return values;
    }
}
