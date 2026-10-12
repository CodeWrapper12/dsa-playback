# 70. Climbing Stairs — dynamic programming with memoisation.
#
# Example 1:
# Input: n = 2
# Output: 2
#
# Example 2:
# Input: n = 5
# Output: 8
#
# Press ▶ Visualize above climbStairs to watch the memo fill in. Then press Complexity:
# without the memo this would be O(2ⁿ); with it, every step is solved once — O(n).


class Solution:
    def __init__(self):
        self.memo = {}

    def climbStairs(self, n: int) -> int:
        if n <= 2:
            return n
        if n in self.memo:
            return self.memo[n]
        ways = self.climbStairs(n - 1) + self.climbStairs(n - 2)
        self.memo[n] = ways
        return ways
