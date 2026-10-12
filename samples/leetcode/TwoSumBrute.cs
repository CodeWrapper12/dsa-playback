// The brute-force answer to the same problem, for comparison against TwoSum.cs.
public class Solution {
    public int[] TwoSum(int[] nums, int target) {
        for (int i = 0; i < nums.Length; i++)
            for (int j = i + 1; j < nums.Length; j++)
                if (nums[i] + nums[j] == target) return new[] { i, j };
        return new int[0];
    }
}
