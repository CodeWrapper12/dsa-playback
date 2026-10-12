# 167. Two Sum II - Input Array Is Sorted — the two-pointer pattern.
#
# Example 1:
# Input: numbers = [2,7,11,15], target = 9
# Output: [1,2]
#
# Example 2:
# Input: numbers = [2,3,4], target = 6
# Output: [1,3]
#
# Press ▶ Visualize above twoSum and watch `left` and `right` walk towards each other.
from typing import List


class Solution:
    def twoSum(self, numbers: List[int], target: int) -> List[int]:
        left, right = 0, len(numbers) - 1
        while left < right:
            total = numbers[left] + numbers[right]
            if total == target:
                return [left + 1, right + 1]
            if total < target:
                left += 1
            else:
                right -= 1
        return [-1, -1]
