# Input: head = [1,2,3,4,5]
# Output: [5,4,3,2,1]
from typing import Optional


class ListNode:
    def __init__(self, val=0, next=None):
        self.val = val
        self.next = next


class Solution:
    def reverseList(self, head: Optional[ListNode]) -> Optional[ListNode]:
        previous = None
        while head:
            nxt = head.next
            head.next = previous
            previous = head
            head = nxt
        return previous
