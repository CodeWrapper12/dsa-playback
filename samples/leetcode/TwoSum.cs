using System.Collections.Generic;

// LeetCode 1, in LeetCode's own C# template: an instance method on class Solution.
public class Solution
{
    public int[] TwoSum(int[] nums, int target)
    {
        var seen = new Dictionary<int, int>();
        for (int i = 0; i < nums.Length; i++)
        {
            if (seen.TryGetValue(target - nums[i], out int j)) return new[] { j, i };
            seen[nums[i]] = i;
        }
        return new int[0];
    }
}
