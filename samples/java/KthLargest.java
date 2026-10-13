// LeetCode 215. Kth Largest Element, with a min-heap of size k.
//
// Example 1:
// Input: nums = [3,2,1,5,6,4], k = 2
// Output: 5
class Solution {
    public int findKthLargest(int[] nums, int k) {
        PriorityQueue<Integer> heap = new PriorityQueue<>();
        for (int x : nums) {
            heap.offer(x);
            if (heap.size() > k) heap.poll();
        }
        return heap.peek();
    }
}
