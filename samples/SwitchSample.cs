using System.Collections.Generic;

// Switch sections hold statements directly rather than inside a block.
public static class SwitchSample
{
    private static string Name(int day)
    {
        switch (day)
        {
            case 0: return "Sun";
            case 1: return "Mon";
            case 2:
            case 3: return "Midweek";
            default: return "Other";
        }
    }

    public static object Solve()
    {
        var names = new List<string>();
        for (int i = 0; i < 6; i++) names.Add(Name(i));
        return names;
    }
}
