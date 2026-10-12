using System.Collections.Generic;

// LeetCode 3. Sliding window over a string.
public class Solution {
    public int LengthOfLongestSubstring(string s) {
        var window = new HashSet<char>();
        int best = 0, left = 0;
        for (int right = 0; right < s.Length; right++) {
            while (window.Contains(s[right])) window.Remove(s[left++]);
            window.Add(s[right]);
            best = System.Math.Max(best, right - left + 1);
        }
        return best;
    }
}
