// LeetCode 3. Longest Substring Without Repeating Characters: a sliding window.
//
// Example 1:
// Input: s = "abcabcbb"
// Output: 3
class Solution {
    public int lengthOfLongestSubstring(String s) {
        Set<Character> window = new HashSet<>();
        int best = 0, left = 0;
        for (int right = 0; right < s.length(); right++) {
            while (window.contains(s.charAt(right))) window.remove(s.charAt(left++));
            window.add(s.charAt(right));
            best = Math.max(best, right - left + 1);
        }
        return best;
    }
}
