// LeetCode 215. Kth Largest Element, with a min-heap of size k.
//
// Example 1:
// Input: nums = [3,2,1,5,6,4], k = 2
// Output: 5
class Solution {
public:
    int findKthLargest(vector<int>& nums, int k) {
        priority_queue<int, vector<int>, greater<int>> heap;
        for (int x : nums) {
            heap.push(x);
            if ((int)heap.size() > k) heap.pop();
        }
        return heap.top();
    }
};
