// LeetCode 704. Binary Search: O(log n).
//
// Example 1:
// Input: nums = [-1,0,3,5,9,12], target = 9
// Output: 4
impl Solution {
    pub fn search(nums: Vec<i32>, target: i32) -> i32 {
        let (mut lo, mut hi) = (0i32, nums.len() as i32 - 1);
        while lo <= hi {
            let mid = lo + (hi - lo) / 2;
            if nums[mid as usize] == target {
                return mid;
            } else if nums[mid as usize] < target {
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }
        -1
    }
}
