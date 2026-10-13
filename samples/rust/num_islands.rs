// LeetCode 200. Number of Islands, with a recursive helper taking the grid by &mut.
//
// Example 1:
// Input: grid = [["1","1","0","0"],["1","0","0","1"],["0","0","1","1"]]
// Output: 2
impl Solution {
    pub fn num_islands(mut grid: Vec<Vec<char>>) -> i32 {
        fn sink(grid: &mut Vec<Vec<char>>, r: i32, c: i32) {
            if r < 0 || c < 0 || r >= grid.len() as i32 || c >= grid[0].len() as i32 || grid[r as usize][c as usize] != '1' {
                return;
            }
            grid[r as usize][c as usize] = '0';
            sink(grid, r + 1, c);
            sink(grid, r - 1, c);
            sink(grid, r, c + 1);
            sink(grid, r, c - 1);
        }
        let mut count = 0;
        for r in 0..grid.len() {
            for c in 0..grid[0].len() {
                if grid[r][c] == '1' {
                    count += 1;
                    sink(&mut grid, r as i32, c as i32);
                }
            }
        }
        count
    }
}
