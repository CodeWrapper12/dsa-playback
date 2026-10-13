// LeetCode 104. Maximum Depth of Binary Tree.
//
// Example 1:
// Input: root = [3,9,20,null,null,15,7]
// Output: 3
class Solution {
    public int maxDepth(TreeNode root) {
        if (root == null) return 0;
        return 1 + Math.max(maxDepth(root.left), maxDepth(root.right));
    }
}
