// LeetCode 20. Valid Parentheses, with a Deque used as a stack, and a switch.
//
// Example 1:
// Input: s = "()[]{}"
// Output: true
//
// Example 2:
// Input: s = "(]"
// Output: false
class Solution {
    public boolean isValid(String s) {
        Deque<Character> stack = new ArrayDeque<>();
        for (char c : s.toCharArray()) {
            switch (c) {
                case '(': case '[': case '{':
                    stack.push(c);
                    break;
                default:
                    if (stack.isEmpty()) return false;
                    char open = stack.pop();
                    if ((c == ')' && open != '(') || (c == ']' && open != '[') || (c == '}' && open != '{')) return false;
            }
        }
        return stack.isEmpty();
    }
}
