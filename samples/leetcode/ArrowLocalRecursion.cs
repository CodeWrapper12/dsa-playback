// A recursive helper written as an arrow-bodied local function.
public class Solution {
    public int CountDown(int n) {
        int Go(int k) => k <= 0 ? 0 : 1 + Go(k - 1);
        return Go(n);
    }
}
