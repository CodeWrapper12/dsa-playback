// LeetCode 70. Climbing Stairs, memoised in a field, with a helper: O(n).
//
// Example 1:
// Input: n = 3
// Output: 3
class Solution {
    private final Map<Integer, Integer> memo = new HashMap<>();

    public int climbStairs(int n) {
        if (n <= 2) return n;
        if (memo.containsKey(n)) return memo.get(n);
        int ways = climbStairs(n - 1) + climbStairs(n - 2);
        memo.put(n, ways);
        return ways;
    }
}
