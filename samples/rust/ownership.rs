// Moves and borrows: the run view says which variables cannot be read at a step, and why.
//
// Example 1:
// Input: values = [3,1,2]
// Output: [1,2,4]
impl Solution {
    pub fn ownership(values: Vec<i32>) -> Vec<i32> {
        let mut owned = values;
        let largest = owned.iter_mut().max().unwrap();
        *largest += 1;
        let total: i32 = owned.iter().sum();
        let mut sorted = owned;
        sorted.sort();
        let label;
        if total > 0 {
            label = "positive";
        } else {
            label = "not positive";
        }
        let _ = label;
        sorted
    }
}
