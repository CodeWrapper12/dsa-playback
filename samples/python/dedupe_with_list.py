# `x not in out` scans the whole list: O(n) hidden in one line, O(n²) overall.
from typing import List


class Solution:
    def dedupe(self, nums: List[int]) -> List[int]:
        out = []
        for x in nums:
            if x not in out:
                out.append(x)
        return out
