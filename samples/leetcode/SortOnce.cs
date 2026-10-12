// Array.Sort is O(n log n), however few lines it takes.
public class Solution {
    public int Median(int[] values) {
        System.Array.Sort(values);
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }
}
