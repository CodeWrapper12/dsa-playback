using System.Collections.Generic;

// Removing from the front of a List shifts every remaining element: O(n) each time.
public class Solution {
    public int Drain(int[] values) {
        var list = new List<int>(values);
        int total = 0;
        while (list.Count > 0) { total += list[0]; list.RemoveAt(0); }
        return total;
    }
}
