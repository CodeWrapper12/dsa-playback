// Every line looks O(1), but std::find scans the vector: O(n²) overall.
class Solution {
public:
    int countDistinct(vector<int>& values) {
        vector<int> seen;
        for (int value : values) {
            if (find(seen.begin(), seen.end(), value) == seen.end()) seen.push_back(value);
        }
        return seen.size();
    }
};
