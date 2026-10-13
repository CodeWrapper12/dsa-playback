// LeetCode 155. Min Stack.
//
// Example 1:
// Input
// ["MinStack","push","push","push","getMin","pop","top","getMin"]
// [[],[-2],[0],[-3],[],[],[],[]]
// Output
// [null,null,null,null,-3,null,0,-2]
struct MinStack {
    items: Vec<(i32, i32)>,
}

impl MinStack {
    fn new() -> Self {
        MinStack { items: Vec::new() }
    }

    fn push(&mut self, val: i32) {
        let min = self.items.last().map_or(val, |&(_, m)| m.min(val));
        self.items.push((val, min));
    }

    fn pop(&mut self) {
        self.items.pop();
    }

    fn top(&self) -> i32 {
        self.items.last().unwrap().0
    }

    #[allow(non_snake_case)]
    fn getMin(&self) -> i32 {
        self.items.last().unwrap().1
    }
}
