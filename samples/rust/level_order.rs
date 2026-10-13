// LeetCode 102. Binary Tree Level Order Traversal, with a VecDeque as the queue.
//
// Example 1:
// Input: root = [3,9,20,null,null,15,7]
// Output: [[3],[9,20],[15,7]]
use std::cell::RefCell;
use std::collections::VecDeque;
use std::rc::Rc;
impl Solution {
    pub fn level_order(root: Option<Rc<RefCell<TreeNode>>>) -> Vec<Vec<i32>> {
        let mut levels = Vec::new();
        let mut queue = VecDeque::new();
        if let Some(node) = root {
            queue.push_back(node);
        }
        while !queue.is_empty() {
            let mut level = Vec::new();
            for _ in 0..queue.len() {
                let node = queue.pop_front().unwrap();
                let node = node.borrow();
                level.push(node.val);
                if let Some(left) = node.left.clone() { queue.push_back(left); }
                if let Some(right) = node.right.clone() { queue.push_back(right); }
            }
            levels.push(level);
        }
        levels
    }
}
