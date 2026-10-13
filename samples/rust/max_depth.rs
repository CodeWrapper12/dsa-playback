// LeetCode 104. Maximum Depth of Binary Tree.
//
// Example 1:
// Input: root = [3,9,20,null,null,15,7]
// Output: 3

// Definition for a binary tree node.
// #[derive(Debug, PartialEq, Eq)]
// pub struct TreeNode {
//   pub val: i32,
//   pub left: Option<Rc<RefCell<TreeNode>>>,
//   pub right: Option<Rc<RefCell<TreeNode>>>,
// }
use std::rc::Rc;
use std::cell::RefCell;
impl Solution {
    pub fn max_depth(root: Option<Rc<RefCell<TreeNode>>>) -> i32 {
        match root {
            None => 0,
            Some(node) => {
                let node = node.borrow();
                1 + Self::max_depth(node.left.clone()).max(Self::max_depth(node.right.clone()))
            }
        }
    }
}
