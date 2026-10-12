using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Tests;

/// <summary>
/// Rebuilds the objects of a recorded run as they were at a given step, from the changes
/// each step carries — what the panel does, so tests read values the way it does.
/// </summary>
public static class StepHeap
{
    public static Dictionary<int, HeapObject> At(StepTrace trace, int stepIndex)
    {
        var heap = new Dictionary<int, HeapObject>();
        for (int i = 0; i <= stepIndex; i++)
        {
            foreach (var change in trace.Steps[i].Heap ?? Array.Empty<HeapObject>())
            {
                if (change.Patch is { } patch)
                {
                    var items = heap[change.Id].Items!.ToArray();
                    foreach (var p in patch) items[p.Index] = p.Value;
                    heap[change.Id] = change with { Items = items, Patch = null };
                }
                else heap[change.Id] = change;
            }
        }
        return heap;
    }

    /// <summary>A variable at a step written out: "[1, 2]", "{1: 10}", or its own text for a scalar.</summary>
    public static string? Display(StepTrace trace, ExecutionStep step, string name)
    {
        var variable = step.Variables.FirstOrDefault(v => v.Name == name);
        if (variable is null) return null;
        if (variable.Ref is not { } id) return variable.Display;
        return Write(At(trace, step.Index)[id]);
    }

    public static HeapObject Object(StepTrace trace, ExecutionStep step, string name)
        => At(trace, step.Index)[step.Variables.Single(v => v.Name == name).Ref!.Value];

    public static string Write(HeapObject o) => o.Kind switch
    {
        "map" or "heap" => "{" + string.Join(", ", o.Entries!.Select(e => $"{e.Key.Text}: {e.Value.Text}")) + "}",
        "tree" or "listnode" or "object" => o.Type + "{" + string.Join(", ", o.Fields!.Select(f => $"{f.Name}={f.Value.Text}")) + "}",
        _ => "[" + string.Join(", ", (o.Items ?? Array.Empty<HeapValue>()).Select(v => v.Text)) + "]"
    };
}
