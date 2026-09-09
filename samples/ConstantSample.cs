// Does the same amount of work regardless of input size: the reference O(1).
public static class ConstantSample
{
    public static int First(int[] values)
    {
        if (values.Length == 0) return -1;
        return values[0];
    }
}
