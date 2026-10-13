// LeetCode 215. Kth Largest Element, with a min-heap of size k (BinaryHeap of Reverse).
//
// Example 1:
// Input: nums = [3,2,1,5,6,4], k = 2
// Output: 5
use std::cmp::Reverse;
use std::collections::BinaryHeap;
impl Solution {
    pub fn find_kth_largest(nums: Vec<i32>, k: i32) -> i32 {
        let mut heap = BinaryHeap::new();
        for x in nums {
            heap.push(Reverse(x));
            if heap.len() > k as usize {
                heap.pop();
            }
        }
        heap.peek().unwrap().0
    }
}
