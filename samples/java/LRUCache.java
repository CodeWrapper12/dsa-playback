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
    private final int capacity;
    private final LinkedHashMap<Integer, Integer> items = new LinkedHashMap<>();

    public LRUCache(int capacity) {
        this.capacity = capacity;
    }

    public int get(int key) {
        if (!items.containsKey(key)) return -1;
        int value = items.remove(key);
        items.put(key, value);
        return value;
    }

    public void put(int key, int value) {
        items.remove(key);
        if (items.size() == capacity) items.remove(items.keySet().iterator().next());
        items.put(key, value);
    }
}
