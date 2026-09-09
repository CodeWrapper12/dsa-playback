using System.Collections.Generic;

// Braceless `for` body and a braceless if/else-if/else chain.
public static class BinarySearchSample
{
    public static object Solve()
    {
        int[] data = new int[100];
        for (int i = 0; i < data.Length; i++) data[i] = i * 2;

        var found = new List<int>();
        foreach (var target in new[] { 0, 50, 98, 198, 197 })
        {
            int lo = 0, hi = data.Length - 1, index = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (data[mid] == target) { index = mid; break; }
                else if (data[mid] < target) lo = mid + 1;
                else hi = mid - 1;
            }
            found.Add(index);
        }
        return found;
    }
}
