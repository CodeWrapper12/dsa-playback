// Every line looks O(1), but List.contains scans the list: O(n²) overall.
class Solution {
    public int countDistinct(int[] values) {
        List<Integer> seen = new ArrayList<>();
        for (int value : values) {
            if (!seen.contains(value)) seen.add(value);
        }
        return seen.size();
    }
}
