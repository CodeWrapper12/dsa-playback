// LeetCode 146. LRU Cache, kept in order by a vector: correct, but each call scans it.
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

class LRUCache {
    int capacity;
    unordered_map<int, int> values;
    vector<int> order;

public:
    LRUCache(int capacity) : capacity(capacity) {}

    int get(int key) {
        if (!values.count(key)) return -1;
        order.erase(find(order.begin(), order.end(), key));
        order.push_back(key);
        return values[key];
    }

    void put(int key, int value) {
        if (values.count(key)) order.erase(find(order.begin(), order.end(), key));
        else if ((int)values.size() == capacity) {
            values.erase(order.front());
            order.erase(order.begin());
        }
        order.push_back(key);
        values[key] = value;
    }
};
