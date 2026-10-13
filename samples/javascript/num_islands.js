// 200. Number of Islands
// Input: grid = [["1","1","0"],["1","0","0"],["0","0","1"]]
// Output: 2

/**
 * @param {character[][]} grid
 * @return {number}
 */
var numIslands = function(grid) {
    let count = 0;
    function sink(r, c) {
        if (r < 0 || c < 0 || r >= grid.length || c >= grid[0].length || grid[r][c] !== "1") return;
        grid[r][c] = "0";
        sink(r + 1, c); sink(r - 1, c); sink(r, c + 1); sink(r, c - 1);
    }
    for (let r = 0; r < grid.length; r++)
        for (let c = 0; c < grid[0].length; c++)
            if (grid[r][c] === "1") { count++; sink(r, c); }
    return count;
};
