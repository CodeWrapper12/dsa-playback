using System.Collections.Generic;

// Substring(i, 3) copies 3 characters, not the whole string, so this stays O(n).
public class Solution {
    public int DistinctTriples(string s) {
        var seen = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) seen.Add(s.Substring(i, 3));
        return seen.Count;
    }
}
