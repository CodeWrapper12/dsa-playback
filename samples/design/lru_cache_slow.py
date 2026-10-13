# LeetCode 146. LRU Cache, kept in order by a list: correct, but each call scans the list.
#
# Input
# ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
# [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
# Output
# [null, null, null, 1, null, -1, null, -1, 3, 4]

class LRUCache:
    def __init__(self, capacity: int):
        self.capacity = capacity
        self.values = {}
        self.order = []

    def get(self, key: int) -> int:
        if key not in self.values:
            return -1
        self.order.remove(key)
        self.order.append(key)
        return self.values[key]

    def put(self, key: int, value: int) -> None:
        if key in self.values:
            self.order.remove(key)
        elif len(self.values) == self.capacity:
            del self.values[self.order.pop(0)]
        self.order.append(key)
        self.values[key] = value
