// Nested loops over the input: the reference O(n^2).
public static class SortParamSample
{
    public static int[] Sort(int[] values)
    {
        for (int i = 0; i < values.Length - 1; i++)
        {
            for (int j = 0; j < values.Length - 1 - i; j++)
            {
                if (values[j] > values[j + 1]) (values[j], values[j + 1]) = (values[j + 1], values[j]);
            }
        }
        return values;
    }
}
