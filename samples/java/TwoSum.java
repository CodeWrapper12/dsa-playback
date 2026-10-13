// LeetCode 1. Two Sum, in LeetCode's own Java template: no imports.
//
// Example 1:
// Input: nums = [2,7,11,15], target = 9
// Output: [0,1]
//
// Example 2:
// Input: nums = [3,2,4], target = 6
// Output: [1,2]
class Solution {
    public int[] twoSum(int[] nums, int target) {
        Map<Integer, Integer> seen = new HashMap<>();
        for (int i = 0; i < nums.length; i++) {
            if (seen.containsKey(target - nums[i])) return new int[] { seen.get(target - nums[i]), i };
            seen.put(nums[i], i);
        }
        return new int[0];
    }
}
