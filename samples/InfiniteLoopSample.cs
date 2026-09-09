// Never terminates. Kept out of the differential set on purpose: it exists only to
// prove the execution budget stops a runaway solution instead of hanging the host.
public static class InfiniteLoopSample
{
    public static object Solve()
    {
        int i = 0;
        while (true) i++;
    }
}
