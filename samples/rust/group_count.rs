// Counting with a BTreeMap, then ordering by count with sort_by.
//
// Example 1:
// Input: words = ["b","a","b","c","a","b"]
// Output: ["b","a","c"]
use std::collections::BTreeMap;
impl Solution {
    pub fn by_frequency(words: Vec<String>) -> Vec<String> {
        let mut counts: BTreeMap<String, i32> = BTreeMap::new();
        for w in &words {
            *counts.entry(w.clone()).or_insert(0) += 1;
        }
        let mut order: Vec<(String, i32)> = counts.into_iter().collect();
        order.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(&b.0)));
        order.into_iter().map(|(word, _)| word).collect()
    }
}
