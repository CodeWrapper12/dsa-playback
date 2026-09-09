// One pass over the input: the reference O(n).
public static class LinearSumSample
{
    public static long Sum(int[] values)
    {
        long total = 0;
        for (int i = 0; i < values.Length; i++) total += values[i];
        return total;
    }
}
