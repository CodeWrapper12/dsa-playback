// 70. Climbing Stairs — dynamic programming with memoisation.
//
// Example 1:
// Input: n = 2
// Output: 2
//
// Example 2:
// Input: n = 5
// Output: 8
//
// Press ▶ Visualize above climb_stairs to watch the memo fill in. Then press Complexity:
// without the memo this would be O(2ⁿ); with it, every step is solved once — O(n).
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
