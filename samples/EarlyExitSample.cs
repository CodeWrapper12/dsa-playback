// Reads as O(n^2) — two nested loops over the input — but returns as soon as it finds
// a repeated value. On input full of duplicates it exits on the first comparison and
// measures as O(1); on distinct random values it runs the full quadratic search.
//
// The same code, two input strategies, two completely different curves. This is why
// the static estimate and the measurement are reported side by side instead of one
// being presented as the answer.
public static class EarlyExitSample
{
    public static int FindRepeatedValue(int[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = i + 1; j < values.Length; j++)
            {
                if (values[i] == values[j]) return values[i];
            }
        }
        return -1;
    }
}
