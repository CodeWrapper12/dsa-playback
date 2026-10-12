# Input: n = 5, edges = [[0,1],[1,2],[3,4]]
# Output: 2
from collections import deque
from typing import List


class Solution:
    def countComponents(self, n: int, edges: List[List[int]]) -> int:
        neighbours = [[] for _ in range(n)]
        for a, b in edges:
            neighbours[a].append(b)
            neighbours[b].append(a)
        visited = [False] * n
        components = 0
        for start in range(n):
            if visited[start]:
                continue
            components += 1
            visited[start] = True
            queue = deque([start])
            while queue:
                node = queue.popleft()
                for nxt in neighbours[node]:
                    if not visited[nxt]:
                        visited[nxt] = True
                        queue.append(nxt)
        return components
