// LeetCode 206. Reverse Linked List. ListNode is left commented out, as LeetCode has it.
//
// Example 1:
// Input: head = [1,2,3,4,5]
// Output: [5,4,3,2,1]

// Definition for singly-linked list.
// #[derive(PartialEq, Eq, Clone, Debug)]
// pub struct ListNode {
//   pub val: i32,
//   pub next: Option<Box<ListNode>>
// }
impl Solution {
    pub fn reverse_list(head: Option<Box<ListNode>>) -> Option<Box<ListNode>> {
        let mut prev = None;
        let mut curr = head;
        while let Some(mut node) = curr {
            curr = node.next.take();
            node.next = prev;
            prev = Some(node);
        }
        prev
    }
}
