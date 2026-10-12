using System.Collections.Generic;

// The trap: List.Contains is a scan, so this is O(n²) while every line reads O(1).
public class Solution {
    public int CountDistinct(int[] values) {
        var seen = new List<int>();
        foreach (int value in values)
            if (!seen.Contains(value)) seen.Add(value);
        return seen.Count;
    }
}
