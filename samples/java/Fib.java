// Naive Fibonacci: exponential.
//
// Example 1:
// Input: n = 10
// Output: 55
class Solution {
    public int fib(int n) {
        if (n < 2) return n;
        return fib(n - 1) + fib(n - 2);
    }
}
