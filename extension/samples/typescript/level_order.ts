// 102. Binary Tree Level Order Traversal — breadth-first search with a queue.
//
// Example 1:
// Input: root = [3,9,20,null,null,15,7]
// Output: [[3],[9,20],[15,7]]
//
// Press ▶ Visualize above levelOrder, then type another tree in the box, like
// [1,2,3,4,5,6,7], and press Enter.

/**
 * Definition for a binary tree node.
 * class TreeNode {
 *     val: number
 *     left: TreeNode | null
 *     right: TreeNode | null
 *     constructor(val?: number, left?: TreeNode | null, right?: TreeNode | null) { ... }
 * }
 */
function levelOrder(root: TreeNode | null): number[][] {
    const levels: number[][] = [];
    if (root === null) return levels;
    const queue: TreeNode[] = [root];
    while (queue.length > 0) {
        const level: number[] = [];
        for (let i = queue.length; i > 0; i--) {
            const node = queue.shift()!;
            level.push(node.val);
            if (node.left) queue.push(node.left);
            if (node.right) queue.push(node.right);
        }
        levels.push(level);
    }
    return levels;
}
