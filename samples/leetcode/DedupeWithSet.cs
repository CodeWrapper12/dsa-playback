using System.Collections.Generic;

// The fix: HashSet.Contains is O(1), so the same loop is O(n).
public class Solution {
    public int CountDistinct(int[] values) {
        var seen = new HashSet<int>();
        foreach (int value in values)
            if (!seen.Contains(value)) seen.Add(value);
        return seen.Count;
    }
}
