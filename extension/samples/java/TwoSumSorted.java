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
// Press ▶ Visualize above twoSum and watch `left` and `right` walk towards each other.
class Solution {
    public int[] twoSum(int[] numbers, int target) {
        int left = 0, right = numbers.length - 1;
        while (left < right) {
            int total = numbers[left] + numbers[right];
            if (total == target) return new int[] { left + 1, right + 1 };
            if (total < target) left++;
            else right--;
        }
        return new int[] { -1, -1 };
    }
}
