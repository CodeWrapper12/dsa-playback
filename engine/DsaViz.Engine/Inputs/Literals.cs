using System.Collections;
using System.Globalization;
using System.Text;

namespace DsaViz.Engine.Inputs;

/// <summary>
/// A value written the way LeetCode writes one: <c>9</c>, <c>"abc"</c>, <c>[2,7,11,15]</c>,
/// <c>[["1","0"],["0","1"]]</c>, <c>[3,9,20,null,null,15,7]</c>.
///
/// Parsed into this neutral form first and only then converted to a parameter's type, so
/// one parser serves typed input, example comments and literal arguments alike, and so a
/// value can be compared with an expected output without either being a .NET object.
/// </summary>
public abstract record LeetValue(int Position);

public sealed record LeetNumber(int Position, string Text) : LeetValue(Position)
{
    public bool IsWhole => !Text.Contains('.') && !Text.Contains('e') && !Text.Contains('E');
    public double AsDouble => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed record LeetString(int Position, string Value) : LeetValue(Position);
public sealed record LeetBool(int Position, bool Value) : LeetValue(Position);
public sealed record LeetNull(int Position) : LeetValue(Position);
public sealed record LeetArray(int Position, IReadOnlyList<LeetValue> Items) : LeetValue(Position);

/// <summary>A value that could not be read, with the 1-based character it went wrong at.</summary>
public sealed class LiteralException : Exception
{
    public LiteralException(string message) : base(message) { }
}

public static class Literals
{
    /// <summary>
    /// Matches LeetCode's own tolerance for floating-point answers, so 2.0 and 2.00000
    /// are the same output.
    /// </summary>
    private const double NumberTolerance = 1e-5;

    /// <summary>Stops a self-referencing structure from being written out forever.</summary>
    private const int FormatNodeLimit = 10_000;

    // ---- parsing ----------------------------------------------------------------------

    public static LeetValue Parse(string text)
    {
        var parser = new Parser(text);
        var value = parser.Value();
        parser.SkipSpace();
        if (!parser.AtEnd) throw parser.Error("unexpected text after the value");
        return value;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _at;

        public Parser(string text) => _text = text;

        public bool AtEnd => _at >= _text.Length;

        public LiteralException Error(string what)
            => new(AtEnd ? $"{what} at the end" : $"{what} at character {_at + 1}");

        public void SkipSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_at])) _at++;
        }

        public LeetValue Value()
        {
            SkipSpace();
            if (AtEnd) throw Error("expected a value");

            char c = _text[_at];
            if (c == '[') return Array();
            if (c is '"' or '\'') return Text();
            if (c == '-' || char.IsDigit(c)) return Number();
            if (Word("true")) return new LeetBool(_at - 4, true);
            if (Word("false")) return new LeetBool(_at - 5, false);
            if (Word("null")) return new LeetNull(_at - 4);
            throw Error("expected a number, \"text\", true, false, null or [ ]");
        }

        private bool Word(string word)
        {
            if (string.CompareOrdinal(_text, _at, word, 0, word.Length) != 0) return false;
            int end = _at + word.Length;
            if (end < _text.Length && char.IsLetterOrDigit(_text[end])) return false;
            _at = end;
            return true;
        }

        private LeetArray Array()
        {
            int start = _at++;
            var items = new List<LeetValue>();
            SkipSpace();
            if (!AtEnd && _text[_at] == ']') { _at++; return new LeetArray(start, items); }

            while (true)
            {
                items.Add(Value());
                SkipSpace();
                if (AtEnd) throw Error("expected , or ]");
                if (_text[_at] == ',') { _at++; continue; }
                if (_text[_at] == ']') { _at++; return new LeetArray(start, items); }
                throw Error("expected , or ]");
            }
        }

        private LeetString Text()
        {
            int start = _at;
            char quote = _text[_at++];
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new LiteralException($"the text starting at character {start + 1} has no closing {quote}");
                char c = _text[_at++];
                if (c == quote) return new LeetString(start, value.ToString());
                if (c != '\\') { value.Append(c); continue; }

                if (AtEnd) throw Error("expected a character after \\");
                char escaped = _text[_at++];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => escaped
                });
            }
        }

        private LeetNumber Number()
        {
            int start = _at;
            if (_text[_at] == '-') _at++;
            if (AtEnd || !char.IsDigit(_text[_at])) throw Error("expected a digit");
            while (!AtEnd && char.IsDigit(_text[_at])) _at++;
            if (!AtEnd && _text[_at] == '.')
            {
                _at++;
                if (AtEnd || !char.IsDigit(_text[_at])) throw Error("expected a digit after the decimal point");
                while (!AtEnd && char.IsDigit(_text[_at])) _at++;
            }
            if (!AtEnd && _text[_at] is 'e' or 'E')
            {
                _at++;
                if (!AtEnd && _text[_at] is '+' or '-') _at++;
                if (AtEnd || !char.IsDigit(_text[_at])) throw Error("expected a digit in the exponent");
                while (!AtEnd && char.IsDigit(_text[_at])) _at++;
            }
            return new LeetNumber(start, _text[start.._at]);
        }
    }

    // ---- conversion to a parameter's type ---------------------------------------------

    /// <summary>Reads <paramref name="text"/> as a value of <paramref name="type"/>.</summary>
    public static object? ToObject(string text, Type type)
    {
        LeetValue value;
        try
        {
            value = Parse(text);
        }
        catch (LiteralException) when (!StartsLikeValue(text))
        {
            // Nothing recognisable at all — "x" in a number box, abc without quotes. Saying
            // what this parameter takes helps more than listing every kind of value.
            throw new LiteralException($"expected {Describe(type)}");
        }
        return ToObject(value, type);
    }

    private static bool StartsLikeValue(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0) return false;
        char c = trimmed[0];
        return c is '[' or '"' or '\'' or '-' || char.IsDigit(c) ||
               trimmed.StartsWith("true") || trimmed.StartsWith("false") || trimmed.StartsWith("null");
    }

    /// <summary>What a parameter of this type takes, with an example, for error messages.</summary>
    public static string Describe(Type type)
    {
        if (type == typeof(int) || type == typeof(long)) return "a whole number, like 9";
        if (type == typeof(double)) return "a number, like 2.5";
        if (type == typeof(bool)) return "true or false";
        if (type == typeof(char)) return "a single character in quotes, like \"a\"";
        if (type == typeof(string)) return "text in double quotes, like \"abc\"";
        if (type == typeof(char[])) return "text in double quotes, like \"abc\", or a list like [\"a\",\"b\"]";
        if (type == typeof(char[][])) return "a grid, like [[\"1\",\"0\"],[\"0\",\"1\"]]";
        if (type == typeof(int[][])) return "a list of lists, like [[1,0],[0,1]]";
        if (NodeShapes.AsTree(type) is not null) return "a tree in level order, like [3,9,20,null,null,15,7]";
        if (type.IsArray || ListElement(type) is not null || NodeShapes.AsList(type) is not null) return "a list, like [1,2,3]";
        return "a value";
    }

    public static object? ToObject(LeetValue value, Type type)
    {
        if (type == typeof(int)) return (int)Whole(value, int.MinValue, int.MaxValue, "a whole number");
        if (type == typeof(long)) return Whole(value, long.MinValue, long.MaxValue, "a whole number");
        if (type == typeof(double)) return value is LeetNumber n ? n.AsDouble : throw Expected("a number", value);
        if (type == typeof(bool)) return value is LeetBool b ? b.Value : throw Expected("true or false", value);
        if (type == typeof(char)) return Character(value);
        if (type == typeof(string))
            return value switch
            {
                LeetString s => s.Value,
                LeetNull => null,
                _ => throw Expected("text in double quotes, like \"abc\"", value)
            };

        if (type == typeof(char[]))
        {
            // LeetCode writes a char[] both ways: "hello" and ["h","e","l","l","o"].
            if (value is LeetString s) return s.Value.ToCharArray();
            return Items(value, "a list of characters").Select(Character).ToArray();
        }

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var items = Items(value, $"a list, like [1,2,3]");
            var array = System.Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++) array.SetValue(ToObject(items[i], element), i);
            return array;
        }

        if (NodeShapes.AsTree(type) is { } tree) return Tree(value, tree);
        if (NodeShapes.AsList(type) is { } list) return Chain(value, list);

        if (ListElement(type) is { } elementType)
        {
            var items = Items(value, "a list, like [1,2,3]");
            var concrete = typeof(List<>).MakeGenericType(elementType);
            if (!type.IsAssignableFrom(concrete))
                throw new LiteralException($"cannot build a {ParameterBinder.FriendlyName(type)} from a list");
            var result = (IList)Activator.CreateInstance(concrete)!;
            foreach (var item in items) result.Add(ToObject(item, elementType));
            return result;
        }

        throw new LiteralException($"values of type {ParameterBinder.FriendlyName(type)} cannot be typed in");
    }

    /// <summary>
    /// The element type a list parameter holds: int for IList&lt;int&gt; or List&lt;int&gt;,
    /// IList&lt;int&gt; for IList&lt;IList&lt;int&gt;&gt;. Null for anything that is not a list.
    /// </summary>
    private static Type? ListElement(Type type)
    {
        if (!type.IsGenericType) return null;
        var arguments = type.GetGenericArguments();
        if (arguments.Length != 1) return null;
        return type.IsAssignableFrom(typeof(List<>).MakeGenericType(arguments[0])) ? arguments[0] : null;
    }

    private static long Whole(LeetValue value, long min, long max, string what)
    {
        if (value is not LeetNumber { IsWhole: true } number) throw Expected(what, value);
        if (!long.TryParse(number.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed)
            || parsed < min || parsed > max)
            throw new LiteralException($"{number.Text} is too large at character {number.Position + 1}");
        return parsed;
    }

    private static char Character(LeetValue value)
        => value is LeetString { Value.Length: 1 } s ? s.Value[0] : throw Expected("a single character, like \"a\"", value);

    private static IReadOnlyList<LeetValue> Items(LeetValue value, string what)
        => value is LeetArray array ? array.Items : throw Expected(what, value);

    private static LiteralException Expected(string what, LeetValue found)
        => new($"expected {what} at character {found.Position + 1}");

    /// <summary>Level order with nulls for missing children, exactly as LeetCode writes a tree.</summary>
    private static object? Tree(LeetValue value, TreeShape shape)
    {
        var items = Items(value, "a tree in level order, like [3,9,20,null,null,15,7]");
        if (items.Count == 0 || items[0] is LeetNull) return null;

        object Node(LeetValue item)
            => NodeShapes.NewNode(shape.Type, shape.Value, (int)Whole(item, int.MinValue, int.MaxValue, "a whole number or null"));

        var root = Node(items[0]);
        var waiting = new Queue<object>();
        waiting.Enqueue(root);
        int next = 1;

        while (next < items.Count)
        {
            if (waiting.Count == 0)
                throw new LiteralException(
                    $"the value at character {items[next].Position + 1} has no parent: every node above it is null");
            var parent = waiting.Dequeue();
            foreach (var side in new[] { shape.Left, shape.Right })
            {
                if (next >= items.Count) break;
                var item = items[next++];
                if (item is LeetNull) continue;
                var child = Node(item);
                side.Set(parent, child);
                waiting.Enqueue(child);
            }
        }
        return root;
    }

    private static object? Chain(LeetValue value, ListShape shape)
    {
        var items = Items(value, "a list, like [1,2,3]");
        object? head = null, tail = null;
        foreach (var item in items)
        {
            var node = NodeShapes.NewNode(shape.Type, shape.Value, (int)Whole(item, int.MinValue, int.MaxValue, "a whole number"));
            if (tail is null) head = node; else shape.Next.Set(tail, node);
            tail = node;
        }
        return head;
    }

    // ---- formatting ---------------------------------------------------------------------

    /// <summary>
    /// Writes a value the way LeetCode prints it, so it can be read back by <see cref="Parse"/>.
    /// Shown values are capped at <see cref="FormatNodeLimit"/> elements, which also stops a
    /// cycle; a value handed to another process must be whole, and passes no limit.
    /// </summary>
    public static string Format(object? value, int limit = FormatNodeLimit)
    {
        var text = new StringBuilder();
        int budget = limit;
        Write(value, text, ref budget);
        return text.ToString();
    }

    private static void Write(object? value, StringBuilder text, ref int budget)
    {
        if (--budget < 0) { text.Append('…'); return; }

        switch (value)
        {
            case null: text.Append("null"); return;
            case bool b: text.Append(b ? "true" : "false"); return;
            case string s: Quote(s, text); return;
            case char c: Quote(c.ToString(), text); return;
            case double d: text.Append(d.ToString("0.0####", CultureInfo.InvariantCulture)); return;
            case float f: text.Append(((double)f).ToString("0.0####", CultureInfo.InvariantCulture)); return;
            case IFormattable number when value.GetType().IsPrimitive || value is decimal:
                text.Append(number.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        var type = value.GetType();
        if (NodeShapes.AsTree(type) is { } tree) { WriteTree(value, tree, text, ref budget); return; }
        if (NodeShapes.AsList(type) is { } list) { WriteChain(value, list, text, ref budget); return; }

        if (value is IEnumerable sequence)
        {
            text.Append('[');
            bool first = true;
            foreach (var item in sequence)
            {
                if (!first) text.Append(',');
                first = false;
                Write(item, text, ref budget);
                if (budget < 0) break;
            }
            text.Append(']');
            return;
        }

        Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? type.Name, text);
    }

    private static void WriteTree(object root, TreeShape shape, StringBuilder text, ref int budget)
    {
        var parts = new List<string>();
        var queue = new Queue<object?>();
        queue.Enqueue(root);
        while (queue.Count > 0 && budget-- > 0)
        {
            var node = queue.Dequeue();
            if (node is null) { parts.Add("null"); continue; }
            parts.Add(Convert.ToString(shape.Value.Get(node), CultureInfo.InvariantCulture)!);
            queue.Enqueue(shape.Left.Get(node));
            queue.Enqueue(shape.Right.Get(node));
        }
        while (parts.Count > 0 && parts[^1] == "null") parts.RemoveAt(parts.Count - 1);
        text.Append('[').Append(string.Join(",", parts)).Append(']');
    }

    private static void WriteChain(object head, ListShape shape, StringBuilder text, ref int budget)
    {
        text.Append('[');
        bool first = true;
        for (object? node = head; node is not null; node = shape.Next.Get(node))
        {
            if (budget-- <= 0) { text.Append(",…"); break; }
            if (!first) text.Append(',');
            first = false;
            text.Append(Convert.ToString(shape.Value.Get(node), CultureInfo.InvariantCulture));
        }
        text.Append(']');
    }

    private static void Quote(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ => c.ToString()
            });
        }
        text.Append('"');
    }

    // ---- comparing with an expected output ---------------------------------------------

    /// <summary>
    /// The same comparison for problems that accept "the answer in any order": every list is
    /// compared as a collection, at every level, so [[1,2],[3]] matches [[3],[2,1]].
    /// </summary>
    public static bool SameValueAnyOrder(LeetValue expected, LeetValue actual)
        => SameValue(Canonical(expected), Canonical(actual));

    private static LeetValue Canonical(LeetValue value) => value switch
    {
        LeetArray array => new LeetArray(array.Position, array.Items
            .Select(Canonical)
            .OrderBy(Key, StringComparer.Ordinal)
            .ToList()),
        _ => value
    };

    /// <summary>A sort key that puts equal values together; numbers are normalised so 2 and 2.0 sort alike.</summary>
    private static string Key(LeetValue value) => value switch
    {
        LeetNumber n => "n" + n.AsDouble.ToString("R", CultureInfo.InvariantCulture),
        LeetString s => "s" + s.Value,
        LeetBool b => "b" + b.Value,
        LeetNull => "z",
        LeetArray a => "a[" + string.Join(",", a.Items.Select(Key)) + "]",
        _ => ""
    };

    /// <summary>
    /// Whether two values are the same output: structurally, with numbers compared within
    /// LeetCode's tolerance, so "[0, 1]" matches "[0,1]" and 2 matches 2.00000.
    /// </summary>
    public static bool SameValue(LeetValue expected, LeetValue actual) => (expected, actual) switch
    {
        (LeetNumber a, LeetNumber b) => Math.Abs(a.AsDouble - b.AsDouble) <= NumberTolerance,
        (LeetString a, LeetString b) => a.Value == b.Value,
        (LeetBool a, LeetBool b) => a.Value == b.Value,
        (LeetNull, LeetNull) => true,
        (LeetArray a, LeetArray b) => a.Items.Count == b.Items.Count &&
                                      a.Items.Zip(b.Items).All(pair => SameValue(pair.First, pair.Second)),
        _ => false
    };
}
