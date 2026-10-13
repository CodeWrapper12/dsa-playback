// LeetCode 3. Longest Substring Without Repeating Characters: a sliding window.
//
// Example 1:
// Input: s = "abcabcbb"
// Output: 3
class Solution {
public:
    int lengthOfLongestSubstring(string s) {
        unordered_set<char> window;
        int best = 0, left = 0;
        for (int right = 0; right < (int)s.size(); right++) {
            while (window.count(s[right])) window.erase(s[left++]);
            window.insert(s[right]);
            best = max(best, right - left + 1);
        }
        return best;
    }
};
