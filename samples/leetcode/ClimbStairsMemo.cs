using System.Collections.Generic;

// LeetCode 70, top-down DP. Reads like Fibonacci; the cache makes it linear.
public class Solution {
    private readonly Dictionary<int, int> memo = new();

    public int ClimbStairs(int n) {
        if (n <= 2) return n;
        if (memo.ContainsKey(n)) return memo[n];
        int ways = ClimbStairs(n - 1) + ClimbStairs(n - 2);
        memo[n] = ways % 1_000_000_007;
        return memo[n];
    }
}
