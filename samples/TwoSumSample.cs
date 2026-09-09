using System.Collections.Generic;

// Allocates a dictionary, so it exercises per-line memory attribution.
public static class TwoSumSample
{
    public static object Solve()
    {
        int[] nums = { 2, 7, 11, 15, 3, 6 };
        int target = 9;

        var seen = new Dictionary<int, int>();
        for (int i = 0; i < nums.Length; i++)
        {
            int need = target - nums[i];
            if (seen.TryGetValue(need, out int j)) return new[] { j, i };
            seen[nums[i]] = i;
        }
        return new[] { -1, -1 };
    }
}
