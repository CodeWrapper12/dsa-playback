// LeetCode 1. Two Sum, in LeetCode's own Rust template: no `struct Solution;`.
//
// Example 1:
// Input: nums = [2,7,11,15], target = 9
// Output: [0,1]
//
// Example 2:
// Input: nums = [3,2,4], target = 6
// Output: [1,2]
use std::collections::HashMap;

impl Solution {
    pub fn two_sum(nums: Vec<i32>, target: i32) -> Vec<i32> {
        let mut seen = HashMap::new();
        for (i, &x) in nums.iter().enumerate() {
            if let Some(&j) = seen.get(&(target - x)) {
                return vec![j as i32, i as i32];
            }
            seen.insert(x, i);
        }
        vec![]
    }
}
