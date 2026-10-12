using System.Collections.Generic;

// Removing from the back shifts nothing: O(1) each time. Same method, opposite cost.
public class Solution {
    public int Drain(int[] values) {
        var list = new List<int>(values);
        int total = 0;
        while (list.Count > 0) { total += list[list.Count - 1]; list.RemoveAt(list.Count - 1); }
        return total;
    }
}
