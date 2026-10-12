using System.Collections.Generic;

// LeetCode 215 with a min-heap of size k: O(n log k).
public class Solution {
    public int FindKthLargest(int[] nums, int k) {
        var heap = new PriorityQueue<int, int>();
        foreach (int value in nums) {
            heap.Enqueue(value, value);
            if (heap.Count > k) heap.Dequeue();
        }
        return heap.Peek();
    }
}
