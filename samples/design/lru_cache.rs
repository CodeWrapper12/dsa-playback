// LeetCode 146. LRU Cache — a design problem: the example lists the calls to make.
//
// Example 1:
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]
use std::collections::{HashMap, VecDeque};

struct LRUCache {
    capacity: usize,
    values: HashMap<i32, i32>,
    order: VecDeque<i32>,
}

/**
 * `&self` means the method takes an immutable reference.
 * If you need a mutable reference, change it to `&mut self` instead.
 */
impl LRUCache {
    fn new(capacity: i32) -> Self {
        LRUCache { capacity: capacity as usize, values: HashMap::new(), order: VecDeque::new() }
    }

    fn get(&mut self, key: i32) -> i32 {
        match self.values.get(&key) {
            Some(&value) => {
                self.touch(key);
                value
            }
            None => -1,
        }
    }

    fn put(&mut self, key: i32, value: i32) {
        if self.values.contains_key(&key) {
            self.touch(key);
        } else {
            if self.values.len() == self.capacity {
                let oldest = self.order.pop_back().unwrap();
                self.values.remove(&oldest);
            }
            self.order.push_front(key);
        }
        self.values.insert(key, value);
    }

    fn touch(&mut self, key: i32) {
        let at = self.order.iter().position(|&k| k == key).unwrap();
        self.order.remove(at);
        self.order.push_front(key);
    }
}
