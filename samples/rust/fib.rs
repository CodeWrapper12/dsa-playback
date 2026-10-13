// Naive Fibonacci: exponential.
//
// Example 1:
// Input: n = 10
// Output: 55
fn fib(n: i32) -> i32 {
    if n < 2 {
        return n;
    }
    fib(n - 1) + fib(n - 2)
}
