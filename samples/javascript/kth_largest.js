// 215. Kth Largest Element in an Array, with LeetCode's MinPriorityQueue.
// Input: nums = [3,2,1,5,6,4], k = 2
// Output: 5

/**
 * @param {number[]} nums
 * @param {number} k
 * @return {number}
 */
var findKthLargest = function(nums, k) {
    const heap = new MinPriorityQueue();
    for (const x of nums) {
        heap.enqueue(x);
        if (heap.size() > k) heap.dequeue();
    }
    return heap.front();
};
