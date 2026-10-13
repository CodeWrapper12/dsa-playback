// Every line looks O(1), but Vec::contains scans the vector: O(n²) overall.
impl Solution {
    pub fn count_distinct(values: Vec<i32>) -> i32 {
        let mut seen: Vec<i32> = Vec::new();
        for value in values {
            if !seen.contains(&value) {
                seen.push(value);
            }
        }
        seen.len() as i32
    }
}
