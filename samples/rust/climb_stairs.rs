// LeetCode 70. Climbing Stairs, memoised with a helper: O(n).
//
// Example 1:
// Input: n = 3
// Output: 3
use std::collections::HashMap;
impl Solution {
    pub fn climb_stairs(n: i32) -> i32 {
        fn ways(n: i32, memo: &mut HashMap<i32, i32>) -> i32 {
            if n <= 2 {
                return n;
            }
            if let Some(&known) = memo.get(&n) {
                return known;
            }
            let total = ways(n - 1, memo) + ways(n - 2, memo);
            memo.insert(n, total);
            total
        }
        ways(n, &mut HashMap::new())
    }
}
