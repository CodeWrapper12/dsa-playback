// 70. Climbing Stairs
// Input: n = 5
// Output: 8

/**
 * @param {number} n
 * @return {number}
 */
var climbStairs = function(n) {
    const memo = new Map();
    const ways = (k) => {
        if (k <= 1) return 1;
        if (memo.has(k)) return memo.get(k);
        const total = ways(k - 1) + ways(k - 2);
        memo.set(k, total);
        return total;
    };
    return ways(n);
};
