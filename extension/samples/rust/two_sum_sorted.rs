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
// Press ▶ Visualize above two_sum and watch `left` and `right` walk towards each other.
impl Solution {
    pub fn two_sum(numbers: Vec<i32>, target: i32) -> Vec<i32> {
        let (mut left, mut right) = (0, numbers.len() - 1);
        while left < right {
            let total = numbers[left] + numbers[right];
            if total == target {
                return vec![left as i32 + 1, right as i32 + 1];
            }
            if total < target {
                left += 1;
            } else {
                right -= 1;
            }
        }
        vec![-1, -1]
    }
}
