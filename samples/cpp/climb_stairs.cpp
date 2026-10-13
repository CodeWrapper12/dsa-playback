// LeetCode 70. Climbing Stairs, memoised: O(n).
//
// Example 1:
// Input: n = 3
// Output: 3
class Solution {
    unordered_map<int, int> memo;
public:
    int climbStairs(int n) {
        if (n <= 2) return n;
        if (memo.count(n)) return memo[n];
        return memo[n] = climbStairs(n - 1) + climbStairs(n - 2);
    }
};
