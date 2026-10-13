// LeetCode 155. Min Stack. Its constructor takes nothing, but it is still a design
// problem: the example names it, so it is run through the calls listed.
//
// Example 1:
// Input
// ["MinStack","push","push","push","getMin","pop","top","getMin"]
// [[],[-2],[0],[-3],[],[],[],[]]
// Output
// [null,null,null,null,-3,null,0,-2]

public class MinStack
{
    private readonly Stack<(int Value, int Min)> stack = new();

    public void Push(int val) => stack.Push((val, stack.Count == 0 ? val : Math.Min(val, stack.Peek().Min)));
    public void Pop() => stack.Pop();
    public int Top() => stack.Peek().Value;
    public int GetMin() => stack.Peek().Min;
}
