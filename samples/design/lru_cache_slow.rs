// LeetCode 146. LRU Cache, kept in order by a Vec: correct, but each call scans it.
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]
use std::collections::HashMap;

struct LRUCache {
    capacity: usize,
    values: HashMap<i32, i32>,
    order: Vec<i32>,
}

impl LRUCache {
    fn new(capacity: i32) -> Self {
        LRUCache { capacity: capacity as usize, values: HashMap::new(), order: Vec::new() }
    }

    fn get(&mut self, key: i32) -> i32 {
        if !self.values.contains_key(&key) {
            return -1;
        }
        let at = self.order.iter().position(|&k| k == key).unwrap();
        self.order.remove(at);
        self.order.push(key);
        self.values[&key]
    }

    fn put(&mut self, key: i32, value: i32) {
        if self.values.contains_key(&key) {
            let at = self.order.iter().position(|&k| k == key).unwrap();
            self.order.remove(at);
        } else if self.values.len() == self.capacity {
            let oldest = self.order.remove(0);
            self.values.remove(&oldest);
        }
        self.order.push(key);
        self.values.insert(key, value);
    }
}
