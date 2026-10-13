// Building a string with += in a loop copies it every time: O(n²) work.
class Solution {
    public String join(int[] values) {
        String out = "";
        for (int v : values) out += v + ",";
        return out;
    }
}
