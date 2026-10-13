// LeetCode 3. Longest Substring Without Repeating Characters: a sliding window.
//
// Example 1:
// Input: s = "abcabcbb"
// Output: 3
use std::collections::HashSet;
impl Solution {
    pub fn length_of_longest_substring(s: String) -> i32 {
        let chars: Vec<char> = s.chars().collect();
        let mut window = HashSet::new();
        let (mut best, mut left) = (0, 0);
        for right in 0..chars.len() {
            while window.contains(&chars[right]) {
                window.remove(&chars[left]);
                left += 1;
            }
            window.insert(chars[right]);
            best = best.max(right - left + 1);
        }
        best as i32
    }
}
