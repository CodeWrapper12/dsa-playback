// LeetCode 20. Valid Parentheses, with a stack and a switch.
//
// Example 1:
// Input: s = "()[]{}"
// Output: true
//
// Example 2:
// Input: s = "(]"
// Output: false
class Solution {
public:
    bool isValid(string s) {
        stack<char> open;
        for (char c : s) {
            switch (c) {
                case '(': case '[': case '{':
                    open.push(c);
                    break;
                default:
                    if (open.empty()) return false;
                    char top = open.top();
                    open.pop();
                    if ((c == ')' && top != '(') || (c == ']' && top != '[') || (c == '}' && top != '{')) return false;
            }
        }
        return open.empty();
    }
};
