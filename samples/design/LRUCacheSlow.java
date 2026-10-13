// LeetCode 146. LRU Cache, kept in order by an ArrayList: correct, but each call scans it.
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

class LRUCache {
    private final int capacity;
    private final Map<Integer, Integer> values = new HashMap<>();
    private final List<Integer> order = new ArrayList<>();

    public LRUCache(int capacity) {
        this.capacity = capacity;
    }

    public int get(int key) {
        if (!values.containsKey(key)) return -1;
        order.remove(Integer.valueOf(key));
        order.add(key);
        return values.get(key);
    }

    public void put(int key, int value) {
        if (values.containsKey(key)) order.remove(Integer.valueOf(key));
        else if (values.size() == capacity) values.remove(order.remove(0));
        order.add(key);
        values.put(key, value);
    }
}
