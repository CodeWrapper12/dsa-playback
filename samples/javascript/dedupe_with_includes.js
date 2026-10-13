// Keep the first of each value, using includes on an array — every check scans it.
// Input: nums = [3,1,3,2,1]
// Output: [3,1,2]

/**
 * @param {number[]} nums
 * @return {number[]}
 */
var dedupe = function(nums) {
    const seen = [];
    for (const x of nums) {
        if (!seen.includes(x)) seen.push(x);
    }
    return seen;
};
