// Two recursive calls per invocation with no memoisation: the reference O(2^n).
// The default size ladder cannot run this, which is what the small-size retry is for.
public static class FibonacciParamSample
{
    public static long Fib(int n)
    {
        if (n < 2) return n;
        return Fib(n - 1) + Fib(n - 2);
    }
}
