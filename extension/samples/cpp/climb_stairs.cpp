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
// Press ▶ Visualize above climbStairs to watch the memo fill in. Then press Complexity:
// without the memo this would be O(2ⁿ); with it, every step is solved once — O(n).
class Solution {
    unordered_map<int, int> memo;

public:
    int climbStairs(int n) {
        if (n <= 2) return n;
        if (memo.count(n)) return memo[n];
        int ways = climbStairs(n - 1) + climbStairs(n - 2);
        memo[n] = ways;
        return ways;
    }
};
