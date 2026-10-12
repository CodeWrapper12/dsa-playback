// A fresh n-sized array on every pass: O(n) work hidden in one statement, O(n²) overall.
public class Solution {
    public int Scratch(int[] values) {
        int total = 0;
        for (int i = 0; i < values.Length; i++) {
            int[] buffer = new int[values.Length];
            total += buffer.Length;
        }
        return total;
    }
}
