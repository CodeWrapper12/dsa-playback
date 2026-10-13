// LeetCode 155. Min Stack.
//
// Example 1:
// Input
// ["MinStack","push","push","push","getMin","pop","top","getMin"]
// [[],[-2],[0],[-3],[],[],[],[]]
// Output
// [null,null,null,null,-3,null,0,-2]
class MinStack {
    private final Deque<int[]> stack = new ArrayDeque<>();

    public void push(int val) {
        stack.push(new int[] { val, stack.isEmpty() ? val : Math.min(val, stack.peek()[1]) });
    }

    public void pop() { stack.pop(); }

    public int top() { return stack.peek()[0]; }

    public int getMin() { return stack.peek()[1]; }
}
