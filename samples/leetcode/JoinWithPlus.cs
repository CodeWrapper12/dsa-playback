// The trap: += on a string copies everything built so far, every time. O(n²).
public class Solution {
    public string Interleave(string s) {
        string result = "";
        foreach (char c in s) result += c;
        return result;
    }
}
