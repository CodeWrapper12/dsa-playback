// LeetCode 167: two pointers closing in on a sorted array.
public class Solution {
    public int[] TwoSum(int[] numbers, int target) {
        int left = 0, right = numbers.Length - 1;
        while (left < right) {
            int sum = numbers[left] + numbers[right];
            if (sum == target) return new[] { left + 1, right + 1 };
            if (sum < target) left++;
            else right--;
        }
        return new int[0];
    }
}
