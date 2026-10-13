// Counting with a map, then reading it back with structured bindings; a do-while and sort.
//
// Example 1:
// Input: words = ["b","a","b","c","a","b"]
// Output: ["b","a","c"]
class Solution {
public:
    vector<string> byFrequency(vector<string>& words) {
        map<string, int> counts;
        for (const string& w : words) counts[w]++;
        vector<pair<int, string>> order;
        for (const auto& [word, count] : counts) order.push_back({-count, word});
        sort(order.begin(), order.end());
        vector<string> result;
        int i = 0;
        do {
            result.push_back(order[i].second);
            i++;
        } while (i < (int)order.size());
        return result;
    }
};
