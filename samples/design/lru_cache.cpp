// LeetCode 146. LRU Cache — a design problem: the example lists the calls to make.
//
// Example 1:
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]
class LRUCache {
    int capacity;
    list<pair<int, int>> order;
    unordered_map<int, list<pair<int, int>>::iterator> where;

public:
    LRUCache(int capacity) : capacity(capacity) {}

    int get(int key) {
        auto found = where.find(key);
        if (found == where.end()) return -1;
        order.splice(order.begin(), order, found->second);
        return found->second->second;
    }

    void put(int key, int value) {
        auto found = where.find(key);
        if (found != where.end()) {
            found->second->second = value;
            order.splice(order.begin(), order, found->second);
            return;
        }
        if ((int)order.size() == capacity) {
            where.erase(order.back().first);
            order.pop_back();
        }
        order.push_front({key, value});
        where[key] = order.begin();
    }
};
