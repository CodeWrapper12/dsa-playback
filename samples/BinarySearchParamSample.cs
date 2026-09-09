// Halves the range each step: the reference O(log n). Expects sorted input, so the
// measurement should be configured with the Sorted strategy.
public static class BinarySearchParamSample
{
    public static int Search(int[] sorted, int target)
    {
        int low = 0;
        int high = sorted.Length - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (sorted[mid] == target) return mid;
            if (sorted[mid] < target) low = mid + 1;
            else high = mid - 1;
        }

        return -1;
    }
}
