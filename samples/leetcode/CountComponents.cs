using System.Collections.Generic;

// LeetCode 323. A graph given the LeetCode way: a node count and an edge list.
public class Solution {
    public int CountComponents(int n, int[][] edges) {
        var neighbours = new List<int>[n];
        for (int i = 0; i < n; i++) neighbours[i] = new List<int>();
        foreach (var edge in edges) {
            neighbours[edge[0]].Add(edge[1]);
            neighbours[edge[1]].Add(edge[0]);
        }

        var visited = new bool[n];
        int components = 0;
        var queue = new Queue<int>();
        for (int start = 0; start < n; start++) {
            if (visited[start]) continue;
            components++;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0) {
                int node = queue.Dequeue();
                foreach (int next in neighbours[node]) {
                    if (visited[next]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
        }
        return components;
    }
}
