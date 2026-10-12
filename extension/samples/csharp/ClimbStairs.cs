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
// Press ▶ Visualize above ClimbStairs to watch the memo fill in. Then press Complexity:
// without the memo this would be O(2ⁿ); with it, every step is solved once — O(n).

using System.Collections.Generic;

public class Solution {
    private readonly Dictionary<int, int> memo = new Dictionary<int, int>();

    public int ClimbStairs(int n) {
        if (n <= 2) return n;
        if (memo.TryGetValue(n, out int known)) return known;

        int ways = ClimbStairs(n - 1) + ClimbStairs(n - 2);
        memo[n] = ways;
        return ways;
    }
}
