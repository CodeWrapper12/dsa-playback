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

/**
 * @param {number} n
 * @return {number}
 */
var climbStairs = function(n) {
    const memo = new Map();
    const ways = (k) => {
        if (k <= 2) return k;
        if (memo.has(k)) return memo.get(k);
        const total = ways(k - 1) + ways(k - 2);
        memo.set(k, total);
        return total;
    };
    return ways(n);
};
