using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DsaViz.Engine.Contracts;
using DsaViz.Runtime;

namespace DsaViz.Engine.Inputs;

/// <summary>
/// Records a step-through run as variables and the objects they lead to, keeping each
/// object's identity across steps.
///
/// Identity is what makes pointer-heavy code readable. When <c>prev</c>, <c>curr</c> and
/// <c>head</c> all lead into one linked list, the view has to draw one list with three
/// labels, and it can only do that if the recording says they are the same nodes rather
/// than three lists that happen to look alike.
///
/// Only objects that changed are sent with a step, and an array that changed in a few
/// places is sent as just those places, so a long run stays small.
///
/// <para>It never runs the student's code. Collections are only enumerated when they are
/// the framework's own, whose enumeration has no side effects; the student's own types
/// are read field by field. Code that ran inside the recorder would itself be traced,
/// and would re-enter it.</para>
/// </summary>
public sealed class HeapRecorder : IStepObserver
{
    /// <summary>Elements kept per object: a 1,000-element array is drawn whole.</summary>
    public const int MaxItems = 1_000;

    /// <summary>Objects visited per step, which bounds the cost of a step on a large structure.</summary>
    public const int MaxObjects = 300;

    /// <summary>Fewer changed positions than this are sent as a patch rather than the whole array.</summary>
    private const int MaxPatch = 8;

    /// <summary>Arrays shorter than this are always sent whole: a patch would not be smaller.</summary>
    private const int MinPatchable = 16;

    private const int MaxFields = 8;
    private const int MaxText = 80;
    private const int MaxCharItems = 40;
    private const int ReferenceSize = 8;
    private const int ObjectHeader = 16;
    private const int ArrayHeader = 24;

    private readonly IReadOnlyList<StatementInfo> _statements;
    private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, HeapObject> _sent = new();

    public HeapRecorder(IReadOnlyList<StatementInfo> statements) => _statements = statements;

    public List<ExecutionStep> Steps { get; } = new();
    public List<ReturnEvent> Returns { get; } = new();

    /// <summary>For a design problem: each call's number and the step count when it started.</summary>
    public List<(int Index, int Step)> CallStarts { get; } = new();

    public void CallStarting(int index) => CallStarts.Add((index, Steps.Count));

    public void Step(int statementId, int depth, object?[] values, long allocatedBytes)
    {
        var info = _statements[statementId];
        var pending = new Queue<object>();
        var variables = new List<StepVariable>(values.Length);
        for (int i = 0; i < values.Length && i < info.Variables.Count; i++)
            variables.Add(Variable(info.Variables[i], values[i], pending));

        var changed = Visit(pending);
        Steps.Add(new ExecutionStep(Steps.Count, statementId, info.Line, info.Column, depth, info.Method, variables)
        {
            Heap = changed.Count > 0 ? changed : null,
            Allocated = allocatedBytes
        });
    }

    public void Returned(int depth, object? value)
    {
        // The returned object is not visited: it is either already reachable from a
        // variable, and drawn, or it is about to be, in the caller.
        Returns.Add(new ReturnEvent(Steps.Count, depth, Value(value, pending: null)));
    }

    // ---- variables ----------------------------------------------------------------------

    private StepVariable Variable(string name, object? value, Queue<object> pending)
    {
        switch (value)
        {
            case null: return new(name, "null", "null", null, null);
            case string text: return Text(name, text);
            case StringBuilder builder: return Text(name, builder.ToString());
            case char c: return new(name, $"'{c}'", "scalar", null, null);
            case bool b: return new(name, b ? "true" : "false", "scalar", null, null);
            case Delegate: return new(name, "<function>", "function", null, null);
        }

        if (Integer(value) is { } number)
            return new(name, number.ToString(CultureInfo.InvariantCulture), "scalar", null, number);

        if (value.GetType().IsValueType) return new(name, Inline(value), "scalar", null, null);

        int id = Track(value, pending);
        return new StepVariable(name, Summary(value), KindOf(value), null, null) { Ref = id };
    }

    private static StepVariable Text(string name, string text)
    {
        string shown = text.Length > MaxText ? text[..MaxText] + "…" : text;
        var items = text.Length <= MaxCharItems ? text.Select(c => c.ToString()).ToList() : null;
        return new(name, "\"" + shown + "\"", "string", items, null);
    }

    // ---- objects ------------------------------------------------------------------------

    private int Track(object value, Queue<object>? pending)
    {
        if (!_ids.TryGetValue(value, out int id))
        {
            id = _ids.Count + 1;
            _ids[value] = id;
        }
        pending?.Enqueue(value);
        return id;
    }

    /// <summary>Every object reachable from the variables, up to the limit; returns those that changed.</summary>
    private List<HeapObject> Visit(Queue<object> pending)
    {
        var changed = new List<HeapObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        while (pending.Count > 0 && seen.Count < MaxObjects)
        {
            var value = pending.Dequeue();
            if (!seen.Add(value)) continue;

            var current = Describe(value, _ids[value], pending);
            if (_sent.TryGetValue(current.Id, out var previous))
            {
                if (Same(previous, current)) continue;
                _sent[current.Id] = current;
                changed.Add(AsPatch(previous, current) ?? current);
            }
            else
            {
                _sent[current.Id] = current;
                changed.Add(current);
            }
        }
        return changed;
    }

    private HeapObject Describe(object value, int id, Queue<object> pending)
    {
        var type = value.GetType();
        string kind = KindOf(value);
        string typeName = ParameterBinder.FriendlyName(type);
        var described = new HeapObject(id, kind, typeName) { Bytes = Bytes(value) };

        switch (kind)
        {
            case "tree":
            {
                var shape = NodeShapes.AsTree(type)!;
                return described with
                {
                    Fields = new[]
                    {
                        new HeapField("val", Value(shape.Value.Get(value), pending)),
                        new HeapField("left", Value(shape.Left.Get(value), pending)),
                        new HeapField("right", Value(shape.Right.Get(value), pending))
                    }
                };
            }
            case "listnode":
            {
                var shape = NodeShapes.AsList(type)!;
                return described with
                {
                    Fields = new[]
                    {
                        new HeapField("val", Value(shape.Value.Get(value), pending)),
                        new HeapField("next", Value(shape.Next.Get(value), pending))
                    }
                };
            }
            case "map":
            {
                var entries = new List<HeapEntry>();
                var enumerator = ((IDictionary)value).GetEnumerator();
                while (entries.Count < MaxItems && enumerator.MoveNext())
                    entries.Add(new HeapEntry(Value(enumerator.Key, pending), Value(enumerator.Value, pending)));
                return described with { Entries = entries, Count = ((IDictionary)value).Count };
            }
            case "heap":
            {
                // UnorderedItems is the heap's own array, so position i's children are at
                // 4i+1 … 4i+4 — .NET's PriorityQueue is a four-way heap.
                var unordered = (IEnumerable)type.GetProperty("UnorderedItems")!.GetValue(value)!;
                var entries = new List<HeapEntry>();
                foreach (var item in unordered)
                {
                    if (entries.Count >= MaxItems) break;
                    var tuple = (ITuple)item!;
                    entries.Add(new HeapEntry(Value(tuple[0], pending), Value(tuple[1], pending)));
                }
                return described with { Entries = entries, Count = (int)type.GetProperty("Count")!.GetValue(value)!, Arity = 4 };
            }
            case "object":
                return described with { Fields = Fields(value, type, pending) };
            default:
            {
                // Only collections that hold their elements are enumerated. A lazy sequence
                // — a LINQ query — would run the student's lambdas to produce them, inside
                // the recorder, and could give different answers each time.
                if (value is not ICollection) return described;

                var items = new List<HeapValue>();
                foreach (var item in (IEnumerable)value)
                {
                    if (items.Count >= MaxItems) break;
                    items.Add(Value(item, pending));
                }
                return described with { Items = items, Count = ((ICollection)value).Count };
            }
        }
    }

    private IReadOnlyList<HeapField> Fields(object value, Type type, Queue<object> pending)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type.GetFields(flags)
            .Take(MaxFields)
            .Select(field => new HeapField(FieldName(field), Value(field.GetValue(value), pending)))
            .ToList();
    }

    /// <summary>An auto-property's backing field is shown under the property's name.</summary>
    private static string FieldName(FieldInfo field)
        => field.Name.StartsWith('<') && field.Name.IndexOf('>') is > 1 and var end ? field.Name[1..end] : field.Name;

    /// <summary>A value inside an object: written out, or a reference to another object.</summary>
    private HeapValue Value(object? value, Queue<object>? pending)
    {
        switch (value)
        {
            case null: return new HeapValue("null");
            case string text: return new HeapValue("\"" + (text.Length > MaxText ? text[..MaxText] + "…" : text) + "\"");
            case Delegate: return new HeapValue("<function>");
        }

        if (Integer(value) is { } number) return new HeapValue(number.ToString(CultureInfo.InvariantCulture)) { Number = number };
        if (value.GetType().IsValueType) return new HeapValue(Inline(value));
        return new HeapValue(Summary(value)) { Ref = Track(value, pending) };
    }

    internal static bool Same(HeapObject a, HeapObject b)
        => a.Kind == b.Kind && a.Count == b.Count && a.Bytes == b.Bytes &&
           SameList(a.Items, b.Items) && SameList(a.Entries, b.Entries) && SameList(a.Fields, b.Fields);

    private static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
        => a is null ? b is null : b is not null && a.SequenceEqual(b);

    /// <summary>The change as a few positions, when that is all it is.</summary>
    internal static HeapObject? AsPatch(HeapObject previous, HeapObject current)
    {
        if (current.Kind is not ("array" or "list") || previous.Items is null || current.Items is null) return null;
        if (previous.Items.Count != current.Items.Count || current.Items.Count < MinPatchable) return null;

        var patch = new List<HeapPatch>();
        for (int i = 0; i < current.Items.Count; i++)
        {
            if (previous.Items[i] == current.Items[i]) continue;
            if (patch.Count == MaxPatch) return null;
            patch.Add(new HeapPatch(i, current.Items[i]));
        }
        return current with { Items = null, Patch = patch };
    }

    // ---- classification ---------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Kinds = new();

    /// <summary>What an object is drawn as. Depends only on its type, so it is worked out once per type.</summary>
    public static string KindOf(object value) => Kinds.GetOrAdd(value.GetType(), Classify);

    private static string Classify(Type type)
    {

        // The student's own types are never enumerated — that would run their code.
        if (!IsFramework(type))
        {
            if (NodeShapes.AsTree(type) is not null) return "tree";
            if (NodeShapes.AsList(type) is not null) return "listnode";
            return "object";
        }

        if (type.IsArray) return type.GetArrayRank() == 1 ? "array" : "object";
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(PriorityQueue<,>)) return "heap";
            if (definition == typeof(Stack<>)) return "stack";
            if (definition == typeof(Queue<>)) return "queue";
        }
        if (typeof(IDictionary).IsAssignableFrom(type)) return "map";
        if (type.GetInterfaces().Any(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(ISet<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>))))
            return "set";
        if (typeof(IList).IsAssignableFrom(type)) return "list";
        if (typeof(IEnumerable).IsAssignableFrom(type)) return "sequence";
        return "object";
    }

    /// <summary>
    /// The framework's own types are loaded from files; the solution is compiled in memory
    /// and has no location.
    /// </summary>
    private static bool IsFramework(Type type) => !string.IsNullOrEmpty(type.Assembly.Location);

    private static long? Integer(object value) => value switch
    {
        int or long or short or byte or sbyte or uint or ushort => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => null
    };

    /// <summary>A short label for an object: its type and size, or a node's value.</summary>
    private static string Summary(object value)
    {
        var type = value.GetType();
        switch (KindOf(value))
        {
            case "tree": return $"{type.Name}({Inline(NodeShapes.AsTree(type)!.Value.Get(value))})";
            case "listnode": return $"{type.Name}({Inline(NodeShapes.AsList(type)!.Value.Get(value))})";
            case "object": return type.Name;
        }
        if (value is Array array) return $"{ParameterBinder.FriendlyName(type.GetElementType()!)}[{array.Length}]";
        int count = value is ICollection collection ? collection.Count
            : type.GetProperty("Count")?.GetValue(value) is int c ? c : -1;
        return count >= 0 ? $"{ParameterBinder.FriendlyName(type)} ({count})" : ParameterBinder.FriendlyName(type);
    }

    /// <summary>A value type written out: a number, a character, a tuple, a key and value.</summary>
    private static string Inline(object? value) => value switch
    {
        null => "null",
        string text => "\"" + (text.Length > 12 ? text[..12] + "…" : text) + "\"",
        char c => c.ToString(),
        bool b => b ? "true" : "false",
        double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.#####", CultureInfo.InvariantCulture),
        IFormattable formattable when value.GetType().IsPrimitive || value is decimal
            => formattable.ToString(null, CultureInfo.InvariantCulture),
        ITuple tuple => "(" + string.Join(", ", Enumerable.Range(0, tuple.Length).Select(i => Inline(tuple[i]))) + ")",
        _ when value.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
            => $"{Inline(t.GetProperty("Key")!.GetValue(value))}: {Inline(t.GetProperty("Value")!.GetValue(value))}",
        Array array => $"[{array.Length}]",
        ICollection collection => $"{{{collection.Count}}}",
        _ => value.GetType().Name
    };

    // ---- size ---------------------------------------------------------------------------------

    /// <summary>
    /// The approximate bytes an object takes itself, on a 64-bit runtime: header, fields
    /// and its own storage, not the objects its elements point to. Exact for arrays and
    /// strings; for the framework's collections, estimated from their element count.
    /// </summary>
    public static long Bytes(object value)
    {
        var type = value.GetType();
        if (value is Array array && array.Rank == 1)
            return Align(ArrayHeader + (long)array.Length * SlotSize(type.GetElementType()!));

        if (!IsFramework(type) || KindOf(value) == "object")
            return Align(ObjectHeader + type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Sum(f => (long)SlotSize(f.FieldType)));

        var arguments = type.IsGenericType ? type.GetGenericArguments() : Array.Empty<Type>();
        long element = arguments.Sum(a => (long)SlotSize(a));
        int count = value is ICollection collection ? collection.Count
            : type.GetProperty("Count")?.GetValue(value) is int c ? c : 0;

        long capacity = type.GetProperty("Capacity")?.GetValue(value) is int capacityValue ? capacityValue : count;

        // Hashed collections keep a bucket index and a hash and next-link per entry beside the elements.
        if (value is IDictionary || KindOf(value) == "set")
            return Align(ObjectHeader + 64) + Align(ArrayHeader + capacity * 4) + Align(ArrayHeader + capacity * (element + 8));

        return Align(ObjectHeader + 32) + Align(ArrayHeader + capacity * Math.Max(element, 1));
    }

    private static int SlotSize(Type type)
    {
        if (!type.IsValueType) return ReferenceSize;
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
        if (type == typeof(char) || type == typeof(short) || type == typeof(ushort)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type.IsPrimitive || type == typeof(decimal)) return type == typeof(decimal) ? 16 : 8;
        return Math.Max(1, type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Sum(f => SlotSize(f.FieldType)));
    }

    private static long Align(long bytes) => (bytes + 7) / 8 * 8;
}
