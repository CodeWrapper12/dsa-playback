// LeetCode 146. LRU Cache, kept in order by a List: correct, but each call scans the list.
//
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

public class LRUCache
{
    private readonly int capacity;
    private readonly Dictionary<int, int> values = new();
    private readonly List<int> order = new();

    public LRUCache(int capacity)
    {
        this.capacity = capacity;
    }

    public int Get(int key)
    {
        if (!values.ContainsKey(key)) return -1;
        order.Remove(key);
        order.Add(key);
        return values[key];
    }

    public void Put(int key, int value)
    {
        if (values.ContainsKey(key)) order.Remove(key);
        else if (values.Count == capacity)
        {
            values.Remove(order[0]);
            order.RemoveAt(0);
        }
        order.Add(key);
        values[key] = value;
    }
}
