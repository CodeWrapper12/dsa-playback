// LeetCode 155. Min Stack. Its constructor takes nothing, but the example names it, so it
// is run through the calls listed.
//
// Example 1:
// Input
// ["MinStack","push","push","push","getMin","pop","top","getMin"]
// [[],[-2],[0],[-3],[],[],[],[]]
// Output
// [null,null,null,null,-3,null,0,-2]
class MinStack {
    stack<pair<int, int>> items;

public:
    MinStack() {}

    void push(int val) {
        items.push({val, items.empty() ? val : min(val, items.top().second)});
    }

    void pop() { items.pop(); }

    int top() { return items.top().first; }

    int getMin() { return items.top().second; }
};
