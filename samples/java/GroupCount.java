// Counting with a TreeMap, then ordering by count with a lambda.
//
// Example 1:
// Input: words = ["b","a","b","c","a","b"]
// Output: ["b","a","c"]
class Solution {
    public List<String> byFrequency(String[] words) {
        Map<String, Integer> counts = new TreeMap<>();
        for (String w : words) counts.merge(w, 1, Integer::sum);
        List<String> order = new ArrayList<>(counts.keySet());
        order.sort((a, b) -> {
            int byCount = counts.get(b) - counts.get(a);
            return byCount != 0 ? byCount : a.compareTo(b);
        });
        return order;
    }
}
