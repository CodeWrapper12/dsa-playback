// Three examples for "Run all examples": the first passes, the second's expected output
// disagrees with the code, and the third makes the code throw.
// Example 1:
// Input: nums = [2,7,11,15], target = 9
// Output: [0,1]
//
// Example 2:
// Input: nums = [3,2,4], target = 6
// Output: [2,1]
//
// Example 3:
// Input: nums = [], target = 6
// Output: []
using System.Collections.Generic;
public class Solution {
    public int[] TwoSum(int[] nums, int target) {
        var seen = new Dictionary<int, int>();
        for (int i = 0; i < nums.Length; i++) {
            if (seen.TryGetValue(target - nums[i], out int j)) return new[] { j, i };
            seen[nums[i]] = i;
        }
        return new int[] { nums[0] };
    }
}
