// 104. Maximum Depth of Binary Tree
// Input: root = [3,9,20,null,null,15,7]
// Output: 3

/**
 * Definition for a binary tree node.
 * class TreeNode {
 *     val: number
 *     left: TreeNode | null
 *     right: TreeNode | null
 * }
 */
function maxDepth(root: TreeNode | null): number {
    if (root === null) return 0;
    return 1 + Math.max(maxDepth(root.left), maxDepth(root.right));
}
