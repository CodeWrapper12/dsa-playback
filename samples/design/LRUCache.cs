// LeetCode 146. LRU Cache — a design problem: the example lists the calls to make.
//
// Example 1:
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

public class LRUCache
{
    private readonly int capacity;
    private readonly Dictionary<int, LinkedListNode<(int Key, int Value)>> map = new();
    private readonly LinkedList<(int Key, int Value)> order = new();

    public LRUCache(int capacity)
    {
        this.capacity = capacity;
    }

    public int Get(int key)
    {
        if (!map.TryGetValue(key, out var node)) return -1;
        order.Remove(node);
        order.AddFirst(node);
        return node.Value.Value;
    }

    public void Put(int key, int value)
    {
        if (map.TryGetValue(key, out var existing))
        {
            order.Remove(existing);
            map.Remove(key);
        }
        else if (map.Count == capacity)
        {
            var oldest = order.Last!;
            order.RemoveLast();
            map.Remove(oldest.Value.Key);
        }
        map[key] = order.AddFirst((key, value));
    }
}
