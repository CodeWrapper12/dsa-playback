// A variable declared before it is set: shown as not set yet until it is.
//
// Example 1:
// Input: n = 5
// Output: "odd"
class Solution {
    public String parity(int n) {
        String label;
        if (n % 2 == 0) {
            label = "even";
        } else {
            label = "odd";
        }
        return label;
    }
}
