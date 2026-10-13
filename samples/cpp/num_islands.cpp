// LeetCode 200. Number of Islands, with a recursive lambda.
//
// Example 1:
// Input: grid = [["1","1","0","0"],["1","0","0","1"],["0","0","1","1"]]
// Output: 2
class Solution {
public:
    int numIslands(vector<vector<char>>& grid) {
        int rows = grid.size(), cols = grid[0].size(), count = 0;
        function<void(int, int)> sink = [&](int r, int c) {
            if (r < 0 || c < 0 || r >= rows || c >= cols || grid[r][c] != '1') return;
            grid[r][c] = '0';
            sink(r + 1, c); sink(r - 1, c); sink(r, c + 1); sink(r, c - 1);
        };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (grid[r][c] == '1') { count++; sink(r, c); }
        return count;
    }
};
