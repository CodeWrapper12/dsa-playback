// LeetCode 704. Binary Search: O(log n).
//
// Example 1:
// Input: nums = [-1,0,3,5,9,12], target = 9
// Output: 4
class Solution {
    public int search(int[] nums, int target) {
        int lo = 0, hi = nums.length - 1;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            if (nums[mid] == target) return mid;
            else if (nums[mid] < target) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }
}
