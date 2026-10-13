// LeetCode 1. Two Sum, in LeetCode's own C++ template: no #include, no using namespace std.
//
// Example 1:
// Input: nums = [2,7,11,15], target = 9
// Output: [0,1]
//
// Example 2:
// Input: nums = [3,2,4], target = 6
// Output: [1,2]
class Solution {
public:
    vector<int> twoSum(vector<int>& nums, int target) {
        unordered_map<int, int> seen;
        for (int i = 0; i < (int)nums.size(); i++) {
            if (seen.count(target - nums[i])) return {seen[target - nums[i]], i};
            seen[nums[i]] = i;
        }
        return {};
    }
};
