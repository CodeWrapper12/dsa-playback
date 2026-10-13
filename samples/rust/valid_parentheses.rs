// LeetCode 20. Valid Parentheses, with a Vec used as a stack, and a match.
//
// Example 1:
// Input: s = "()[]{}"
// Output: true
//
// Example 2:
// Input: s = "(]"
// Output: false
impl Solution {
    pub fn is_valid(s: String) -> bool {
        let mut stack = Vec::new();
        for c in s.chars() {
            match c {
                '(' | '[' | '{' => stack.push(c),
                _ => {
                    let open = stack.pop();
                    let expected = match c { ')' => '(', ']' => '[', _ => '{' };
                    if open != Some(expected) {
                        return false;
                    }
                }
            }
        }
        stack.is_empty()
    }
}
