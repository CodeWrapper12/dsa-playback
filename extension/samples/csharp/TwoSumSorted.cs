// 167. Two Sum II - Input Array Is Sorted — the two-pointer pattern.
//
// Example 1:
// Input: numbers = [2,7,11,15], target = 9
// Output: [1,2]
//
// Example 2:
// Input: numbers = [2,3,4], target = 6
// Output: [1,3]
//
// Press ▶ Visualize above TwoSum and watch `left` and `right` walk towards each other.

public class Solution {
    public int[] TwoSum(int[] numbers, int target) {
        int left = 0;
        int right = numbers.Length - 1;

        while (left < right) {
            int sum = numbers[left] + numbers[right];
            if (sum == target) {
                return new int[] { left + 1, right + 1 };
            }
            if (sum < target) {
                left++;
            } else {
                right--;
            }
        }

        return new int[] { -1, -1 };
    }
}
