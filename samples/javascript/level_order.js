// 102. Binary Tree Level Order Traversal
// Input: root = [3,9,20,null,null,15,7]
// Output: [[3],[9,20],[15,7]]

/**
 * @param {TreeNode} root
 * @return {number[][]}
 */
var levelOrder = function(root) {
    const levels = [];
    if (!root) return levels;
    const queue = [root];
    while (queue.length) {
        const level = [];
        for (let i = queue.length; i > 0; i--) {
            const node = queue.shift();
            level.push(node.val);
            if (node.left) queue.push(node.left);
            if (node.right) queue.push(node.right);
        }
        levels.push(level);
    }
    return levels;
};
