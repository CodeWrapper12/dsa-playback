// 102. Binary Tree Level Order Traversal — breadth-first search with a queue.
//
// Example 1:
// Input: root = [3,9,20,null,null,15,7]
// Output: [[3],[9,20],[15,7]]
//
// Example 2:
// Input: root = [1]
// Output: [[1]]
//
// Press ▶ Visualize above levelOrder and watch the queue fill and empty, one level at a time.
// TreeNode is supplied, as LeetCode supplies it.
class Solution {
    public List<List<Integer>> levelOrder(TreeNode root) {
        List<List<Integer>> levels = new ArrayList<>();
        if (root == null) return levels;
        Queue<TreeNode> queue = new LinkedList<>();
        queue.offer(root);
        while (!queue.isEmpty()) {
            List<Integer> level = new ArrayList<>();
            for (int i = queue.size(); i > 0; i--) {
                TreeNode node = queue.poll();
                level.add(node.val);
                if (node.left != null) queue.offer(node.left);
                if (node.right != null) queue.offer(node.right);
            }
            levels.add(level);
        }
        return levels;
    }
}
