// LeetCode 200. Flood fill: four recursive calls, but each cell is sunk once it is visited.
public class Solution {
    public int NumIslands(char[][] grid) {
        int count = 0;
        for (int r = 0; r < grid.Length; r++)
            for (int c = 0; c < grid[r].Length; c++)
                if (grid[r][c] == '1') { count++; Sink(grid, r, c); }
        return count;
    }

    private void Sink(char[][] grid, int r, int c) {
        if (r < 0 || c < 0 || r >= grid.Length || c >= grid[r].Length || grid[r][c] != '1') return;
        grid[r][c] = '0';
        Sink(grid, r + 1, c);
        Sink(grid, r - 1, c);
        Sink(grid, r, c + 1);
        Sink(grid, r, c - 1);
    }
}
