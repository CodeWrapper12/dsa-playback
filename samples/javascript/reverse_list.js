// 206. Reverse Linked List
// Input: head = [1,2,3,4,5]
// Output: [5,4,3,2,1]

/**
 * Definition for singly-linked list.
 * function ListNode(val, next) {
 *     this.val = (val===undefined ? 0 : val)
 *     this.next = (next===undefined ? null : next)
 * }
 */
/**
 * @param {ListNode} head
 * @return {ListNode}
 */
var reverseList = function(head) {
    let previous = null;
    while (head) {
        const nxt = head.next;
        head.next = previous;
        previous = head;
        head = nxt;
    }
    return previous;
};
