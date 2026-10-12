using System.Text;

// The fix: StringBuilder appends in amortised O(1). O(n).
public class Solution {
    public string Interleave(string s) {
        var result = new StringBuilder();
        foreach (char c in s) result.Append(c);
        return result.ToString();
    }
}
