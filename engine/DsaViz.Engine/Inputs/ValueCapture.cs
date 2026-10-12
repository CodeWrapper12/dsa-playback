using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DsaViz.Runtime;

namespace DsaViz.Engine.Inputs;

/// <summary>
/// Renders a variable for one step of a step-through, at the moment it is captured.
///
/// Every rendering is capped. A step-through runs at a small n, but a solution can still
/// build something large — and a step list that took seconds to serialise, or megabytes
/// to send to the editor, would make the view unusable. Caps are marked with an ellipsis
/// so a truncated value never passes for a complete one.
/// </summary>
public static class ValueCapture
{
    private const int MaxItems = 40;
    private const int MaxText = 80;
    private const int MaxRows = 20;
    private const int MaxNodes = 31;

    public static CapturedValue Capture(object? value)
    {
        switch (value)
        {
            case null: return new("null", "null");
            case string text: return Text(text);
            case StringBuilder builder: return Text(builder.ToString());
            case char c: return new($"'{c}'", "scalar");
            case bool b: return new(b ? "true" : "false", "scalar");
            case int or long or short or byte or sbyte or uint or ushort:
            {
                long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return new(number.ToString(CultureInfo.InvariantCulture), "scalar", null, number);
            }
            case float or double or decimal or ulong:
                return new(Convert.ToString(value, CultureInfo.InvariantCulture)!, "scalar");
            case Delegate: return new("<function>", "function");
        }

        var type = value.GetType();
        if (NodeShapes.AsTree(type) is { } tree) return Tree(value, tree);
        if (NodeShapes.AsList(type) is { } list) return Chain(value, list);

        if (value is Array array)
        {
            if (array.Rank == 1 && type.GetElementType()!.IsArray) return Grid(array);
            if (array.Rank == 2) return new($"[{array.GetLength(0)} × {array.GetLength(1)} array]", "grid");
            return Sequence(array.Cast<object?>(), array.Length, "array");
        }

        if (value is IDictionary dictionary)
        {
            // Through IDictionaryEnumerator explicitly: a generic Dictionary's plain
            // IEnumerable yields boxed KeyValuePairs, not DictionaryEntry.
            var entries = new List<string>();
            var enumerator = dictionary.GetEnumerator();
            while (entries.Count < MaxItems && enumerator.MoveNext())
                entries.Add($"{Inline(enumerator.Key)}: {Inline(enumerator.Value)}");
            return Collection(entries, dictionary.Count, '{', '}');
        }

        // An indexable list is drawn as an array, so indices can point into it.
        if (value is IList indexed) return Sequence(indexed.Cast<object?>(), indexed.Count, "array");

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PriorityQueue<,>)) return Heap(value, type);

        if (value is IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().Take(MaxItems + 1).ToList();
            int count = (value as ICollection)?.Count ?? (int)TraceState.SizeOf(value);
            return Collection(items.Take(MaxItems).Select(Inline).ToList(), Math.Max(count, items.Count), '{', '}');
        }

        return Fields(value, type);
    }

    private static CapturedValue Text(string text)
    {
        string shown = text.Length > MaxText ? text[..MaxText] + "…" : text;
        var items = text.Length <= MaxItems ? text.Select(c => c.ToString()).ToList() : null;
        return new("\"" + shown + "\"", "string", items);
    }

    private static CapturedValue Sequence(IEnumerable<object?> source, int count, string kind)
    {
        var items = source.Take(MaxItems).Select(Inline).ToList();
        string display = "[" + string.Join(", ", items) + (count > MaxItems ? ", …" : "") + "]";
        return new(display, kind, count <= MaxItems ? items : items.Append("…").ToList());
    }

    private static CapturedValue Collection(IReadOnlyList<string> items, int count, char open, char close)
        => new(open + string.Join(", ", items) + (count > items.Count ? ", …" : "") + close, "collection", items);

    private static CapturedValue Grid(Array rows)
    {
        var rendered = rows.Cast<object?>().Take(MaxRows).Select(row => row switch
        {
            char[] chars => new string(chars.Take(MaxItems).ToArray()) + (chars.Length > MaxItems ? "…" : ""),
            Array cells => string.Join(" ", cells.Cast<object?>().Take(MaxItems).Select(Inline)) + (cells.Length > MaxItems ? " …" : ""),
            _ => Inline(row)
        }).ToList();
        if (rows.Length > MaxRows) rendered.Add("…");
        return new($"[{rows.Length} rows]", "grid", rendered);
    }

    private static CapturedValue Tree(object root, TreeShape shape)
    {
        var parts = new List<string>();
        var queue = new Queue<object?>();
        queue.Enqueue(root);
        while (queue.Count > 0 && parts.Count < MaxNodes)
        {
            var node = queue.Dequeue();
            if (node is null) { parts.Add("null"); continue; }
            parts.Add(Inline(shape.Value.Get(node)));
            queue.Enqueue(shape.Left.Get(node));
            queue.Enqueue(shape.Right.Get(node));
        }
        while (parts.Count > 0 && parts[^1] == "null") parts.RemoveAt(parts.Count - 1);
        bool more = queue.Any(n => n is not null);
        return new("[" + string.Join(",", parts) + (more ? ",…" : "") + "]", "tree", parts);
    }

    private static CapturedValue Chain(object head, ListShape shape)
    {
        var parts = new List<string>();
        object? node = head;
        // Capped, which also stops a cycle from rendering forever.
        for (; node is not null && parts.Count < MaxItems; node = shape.Next.Get(node))
            parts.Add(Inline(shape.Value.Get(node)));
        return new(string.Join(" → ", parts) + (node is not null ? " → …" : ""), "list", parts);
    }

    private static CapturedValue Heap(object queue, Type type)
    {
        var unordered = (IEnumerable)type.GetProperty("UnorderedItems")!.GetValue(queue)!;
        var items = unordered.Cast<object?>().Take(MaxItems).Select(Inline).ToList();
        int count = (int)type.GetProperty("Count")!.GetValue(queue)!;
        return Collection(items, count, '{', '}');
    }

    private static CapturedValue Fields(object value, Type type)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Take(6)
            .Select(f => $"{f.Name}={Inline(f.GetValue(value))}");
        return new($"{type.Name} {{ {string.Join(", ", fields)} }}", "object");
    }

    /// <summary>A short, single-token rendering for an element inside another value.</summary>
    private static string Inline(object? value) => value switch
    {
        null => "null",
        string text => "\"" + (text.Length > 12 ? text[..12] + "…" : text) + "\"",
        char c => c.ToString(),
        bool b => b ? "true" : "false",
        IFormattable formattable when value.GetType().IsPrimitive || value is decimal
            => formattable.ToString(null, CultureInfo.InvariantCulture),
        ITuple tuple => "(" + string.Join(", ", Enumerable.Range(0, tuple.Length).Select(i => Inline(tuple[i]))) + ")",
        Array array => $"[{array.Length}]",
        ICollection collection => $"{{{collection.Count}}}",
        _ => value.GetType().Name
    };
}
