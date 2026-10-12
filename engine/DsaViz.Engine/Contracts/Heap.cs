using System.Text.Json.Serialization;

namespace DsaViz.Engine.Contracts;

/// <summary>
/// A value inside a variable or an object: a number or text written out, or a reference
/// to an object by its id. <see cref="Text"/> is always set, so anything can be shown as
/// text even where it is not drawn.
/// </summary>
public sealed record HeapValue(string Text)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Number { get; init; }

    /// <summary>The id of the object this refers to; the same id is the same object.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ref { get; init; }
}

public sealed record HeapEntry(HeapValue Key, HeapValue Value);

public sealed record HeapField(string Name, HeapValue Value);

/// <summary>One element changed in place: arrays are sent whole only when much of them changed.</summary>
public sealed record HeapPatch(int Index, HeapValue Value);

/// <summary>
/// An object as it was at one step, sent only at the steps where it changed.
///
/// <para>Kinds: <c>array</c> and <c>list</c> (Items, by position), <c>stack</c> (Items,
/// top first), <c>queue</c> (Items, front first), <c>set</c> (Items), <c>heap</c>
/// (Entries of element and priority, in the heap's own array order), <c>map</c>
/// (Entries), <c>tree</c> and <c>listnode</c> (Fields of LeetCode's TreeNode and
/// ListNode), <c>sequence</c> (any other collection, Items) and <c>object</c> (Fields).</para>
/// </summary>
public sealed record HeapObject(int Id, string Kind, string Type)
{
    /// <summary>How many elements the object really holds; Items may stop short of it.</summary>
    public int Count { get; init; }

    /// <summary>Approximate bytes of the object itself, without what its elements point to.</summary>
    public long Bytes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HeapValue>? Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HeapEntry>? Entries { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HeapField>? Fields { get; init; }

    /// <summary>
    /// Set instead of Items when only a few positions of an array or list changed and its
    /// length did not: apply these to the previous Items.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HeapPatch>? Patch { get; init; }
}

/// <summary>A call returning: after <see cref="AfterStep"/> steps had been recorded, at this depth, with this value.</summary>
public sealed record ReturnEvent(int AfterStep, int Depth, HeapValue Value);

/// <summary>A method or local function and its parameters, in order, so the recursion tree can show a call's arguments.</summary>
public sealed record MethodParameters(string Name, IReadOnlyList<string> Parameters);

/// <summary>
/// Which input is a graph, so it can be drawn as nodes and edges: an adjacency list, or
/// LeetCode's (n, edges) pair.
/// </summary>
public sealed record GraphInput(string Kind, string Variable)
{
    /// <summary>For an edge list, the parameter holding the number of nodes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeCount { get; init; }
}
