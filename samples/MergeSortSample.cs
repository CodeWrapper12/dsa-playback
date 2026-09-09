// Divide and conquer: two recursive calls per invocation, but each takes half the
// range rather than nearly all of it. Structurally it looks like the Fibonacci sample;
// the difference is how fast the input shrinks, which is why the static estimator has
// to be careful here rather than counting call sites and concluding.
public static class MergeSortSample
{
    public static int[] Sort(int[] values)
    {
        if (values.Length <= 1) return values;

        var buffer = new int[values.Length];
        SortRange(values, buffer, 0, values.Length - 1);
        return values;
    }

    private static void SortRange(int[] values, int[] buffer, int low, int high)
    {
        if (low >= high) return;

        int mid = (low + high) / 2;
        SortRange(values, buffer, low, mid);
        SortRange(values, buffer, mid + 1, high);
        Merge(values, buffer, low, mid, high);
    }

    private static void Merge(int[] values, int[] buffer, int low, int mid, int high)
    {
        for (int i = low; i <= high; i++) buffer[i] = values[i];

        int left = low;
        int right = mid + 1;

        for (int i = low; i <= high; i++)
        {
            if (left > mid) values[i] = buffer[right++];
            else if (right > high) values[i] = buffer[left++];
            else if (buffer[left] <= buffer[right]) values[i] = buffer[left++];
            else values[i] = buffer[right++];
        }
    }
}
