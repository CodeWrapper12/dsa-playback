// Nested braceless loops — the canonical O(n^2) shape.
public static class BubbleSortSample
{
    public const int N = 60;

    public static object Solve()
    {
        int[] a = new int[N];
        for (int i = 0; i < N; i++) a[i] = N - i;

        for (int i = 0; i < N - 1; i++)
            for (int j = 0; j < N - 1 - i; j++)
                if (a[j] > a[j + 1]) { (a[j], a[j + 1]) = (a[j + 1], a[j]); }

        return a;
    }
}
