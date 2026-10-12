// LeetCode 104. Two recursive calls, but on child nodes: each node is visited once.
public class TreeNode {
    public int val;
    public TreeNode left;
    public TreeNode right;
    public TreeNode(int val = 0, TreeNode left = null, TreeNode right = null) {
        this.val = val; this.left = left; this.right = right;
    }
}

public class Solution {
    public int MaxDepth(TreeNode root) {
        if (root == null) return 0;
        return 1 + System.Math.Max(MaxDepth(root.left), MaxDepth(root.right));
    }
}
