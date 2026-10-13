using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;

namespace DsaViz.Engine.Analysis;

/// <summary>What one argument of a design problem's call is, for making up values of it.</summary>
public enum DesignArgKind { Int, Double, Bool, Char, String, IntArray, StringArray, Unsupported }

/// <summary>One parameter of a design problem's constructor or method.</summary>
public sealed record DesignArg(string Name, DesignArgKind Kind, string Written)
{
    /// <summary>
    /// The kind of a parameter from its type as written in any of the five languages:
    /// <c>int</c>, <c>i32</c>, <c>long long</c>, <c>const string&amp;</c>, <c>Vec&lt;i32&gt;</c>, <c>List[int]</c>.
    /// </summary>
    public static DesignArg Of(string name, string written)
    {
        string type = Regex.Replace(written, @"\bconst\b|\bstd::|&|\bmut\b|\s+", "");
        return new DesignArg(name, KindOf(type), written.Trim());
    }

    private static DesignArgKind KindOf(string type)
    {
        if (Regex.IsMatch(type, @"^(int|long|longlong|short|unsignedint|unsigned|size_t|Integer|Long|Int32|Int64|i8|i16|i32|i64|isize|u8|u16|u32|u64|usize|number)$"))
            return DesignArgKind.Int;
        if (Regex.IsMatch(type, @"^(double|float|Double|Float|f32|f64)$")) return DesignArgKind.Double;
        if (Regex.IsMatch(type, @"^(bool|boolean|Boolean)$")) return DesignArgKind.Bool;
        if (Regex.IsMatch(type, @"^(char|Character|character)$")) return DesignArgKind.Char;
        if (Regex.IsMatch(type, @"^(string|String|str|Str)$")) return DesignArgKind.String;
        if (Regex.IsMatch(type, @"^(int\[\]|long\[\]|number\[\]|Array<number>|vector<(int|long|longlong)>|Vec<(i32|i64|usize)>|List\[int\]|list\[int\]|(I)?List<(int|Integer|long|Long)>)$"))
            return DesignArgKind.IntArray;
        if (Regex.IsMatch(type, @"^(string\[\]|String\[\]|vector<string>|Vec<String>|List\[str\]|list\[str\]|(I)?List<(string|String)>)$"))
            return DesignArgKind.StringArray;
        return DesignArgKind.Unsupported;
    }
}

/// <summary>
/// The calls a design problem is measured with. Its cost is per call, so each size n is
/// n calls: the example's calls after the constructor, repeated in their order with new
/// values until there are n of them. The order is the example's because it is a valid
/// one — a stack is pushed before it is popped — and made-up orders need not be.
///
/// Whole numbers given to the constructor are n — a cache of capacity n, which can fill —
/// and arrays given to it hold n items. Whole numbers given to the methods are drawn from
/// 0 to n − 1, so keys repeat as the run goes on and both hits and misses happen.
/// </summary>
public sealed class DesignWorkload
{
    /// <summary>Said when no example in the code gives the calls to repeat.</summary>
    public const string NoExample =
        "Complexity for a design problem repeats the calls in its example, with new values, at bigger and bigger sizes. " +
        "Add LeetCode's example as a comment above the class — the list of operations, then the list of their arguments.";

    private readonly string _className;
    private readonly IReadOnlyList<string> _calls;
    private readonly IReadOnlyList<IReadOnlyList<DesignArg>> _parameters;
    private readonly IReadOnlyList<DesignArg> _constructor;
    private readonly IReadOnlyList<IReadOnlyList<LeetValue>> _exampleArguments;

    private DesignWorkload(string className, IReadOnlyList<DesignArg> constructor, IReadOnlyList<string> calls,
        IReadOnlyList<IReadOnlyList<DesignArg>> parameters, IReadOnlyList<IReadOnlyList<LeetValue>> exampleArguments)
    {
        _className = className;
        _constructor = constructor;
        _calls = calls;
        _parameters = parameters;
        _exampleArguments = exampleArguments;
    }

    /// <summary>The class the calls are made on: its constructor is not a helper.</summary>
    public string ClassName => _className;

    /// <summary>The operations repeated, in order, once each per round.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>
    /// The workload for a class, from its first example that makes a call. <paramref name="signature"/>
    /// gives an operation's parameters for a number of arguments: the class's name for the constructor.
    /// </summary>
    /// <exception cref="DesignWorkloadException">There is nothing to repeat, or a value cannot be made up.</exception>
    public static DesignWorkload From(string className, ExampleSearch search, Func<string, int, IReadOnlyList<DesignArg>?> signature)
    {
        foreach (var example in search.Examples)
        {
            if (example.Values.Count < 2 || example.Values[0] is not { } opsText || example.Values[1] is not { } argsText) continue;
            if (Parse(opsText, argsText) is not { } parsed || parsed.Operations.Count < 2) continue;

            var (operations, arguments) = parsed;
            var constructor = signature(className, arguments[0].Count)
                ?? throw new DesignWorkloadException($"{className} has no constructor taking {Count(arguments[0].Count)}, as the example's first call does.");
            var parameters = new List<IReadOnlyList<DesignArg>>();
            for (int i = 1; i < operations.Count; i++)
            {
                parameters.Add(signature(operations[i], arguments[i].Count)
                    ?? throw new DesignWorkloadException($"{className} has no method {operations[i]} taking {Count(arguments[i].Count)}, as the example calls it."));
            }

            foreach (var arg in constructor.Concat(parameters.SelectMany(p => p)))
            {
                if (arg.Kind == DesignArgKind.Unsupported && arg.Written.Length == 0)
                    throw new DesignWorkloadException(
                        $"'{arg.Name}' has no type hint, so values for it cannot be made up. Add one the way LeetCode does, like '{arg.Name}: int'.");
                if (arg.Kind == DesignArgKind.Unsupported)
                    throw new DesignWorkloadException(
                        $"'{arg.Name}' is a {arg.Written}, which cannot be made up, so the calls cannot be repeated at bigger sizes. " +
                        "Press Visualize to watch your example's calls run.");
            }

            return new DesignWorkload(className, constructor, operations.Skip(1).ToList(), parameters, arguments.Skip(1).ToList());
        }
        throw new DesignWorkloadException(NoExample);
    }

    private static (List<string> Operations, List<IReadOnlyList<LeetValue>> Arguments)? Parse(string opsText, string argsText)
    {
        try
        {
            if (Literals.Parse(opsText) is not LeetArray ops || Literals.Parse(argsText) is not LeetArray args) return null;
            if (ops.Items.Count != args.Items.Count || ops.Items.Any(o => o is not LeetString)) return null;
            if (args.Items.Any(a => a is not LeetArray)) return null;
            return (ops.Items.Select(o => ((LeetString)o).Value).ToList(), args.Items.Select(a => ((LeetArray)a).Items).ToList());
        }
        catch (LiteralException)
        {
            return null;
        }
    }

    /// <summary>The operations and their arguments for <paramref name="size"/> calls, as LeetCode writes them.</summary>
    public (string Operations, string Arguments) At(int size)
    {
        var random = new Random(unchecked(size * 7919 + 17));
        var operations = new StringBuilder("[").Append(Quote(_className));
        var arguments = new StringBuilder("[").Append(Values(_constructor, null, size, random, constructor: true));

        for (int call = 0; call < size; call++)
        {
            int at = call % _calls.Count;
            operations.Append(',').Append(Quote(_calls[at]));
            arguments.Append(',').Append(Values(_parameters[at], _exampleArguments[at], size, random, constructor: false));
        }
        return (operations.Append(']').ToString(), arguments.Append(']').ToString());
    }

    /// <summary>What the calls are, in words, for the explanation above the results.</summary>
    public string Describe()
    {
        var round = string.Join(", ", _calls);
        var sentence = new StringBuilder(
            $"Each size n makes n calls: the example's {Plural(_calls.Count, "call")} after the constructor ({round}), repeated with new values.");

        var scaled = _constructor.Where(a => a.Kind is DesignArgKind.Int).Select(a => $"{a.Name} = n").ToList();
        scaled.AddRange(_constructor.Where(a => a.Kind is DesignArgKind.IntArray or DesignArgKind.StringArray).Select(a => $"{a.Name} holds n items"));
        if (scaled.Count > 0) sentence.Append($" The object is made with {string.Join(" and ", scaled)}.");
        if (_parameters.SelectMany(p => p).Any(a => a.Kind == DesignArgKind.Int))
            sentence.Append(" Whole numbers passed to the calls are drawn from 0 to n − 1, so the same values come up again as the calls go on.");
        return sentence.ToString();
    }

    private static string Values(IReadOnlyList<DesignArg> args, IReadOnlyList<LeetValue>? example, int size, Random random, bool constructor)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) text.Append(',');
            int exampleLength = example is not null && i < example.Count && example[i] is LeetArray a ? a.Items.Count : 1;
            text.Append(Value(args[i].Kind, size, random, constructor ? size : Math.Max(1, exampleLength), constructor));
        }
        return text.Append(']').ToString();
    }

    private static string Value(DesignArgKind kind, int size, Random random, int length, bool constructor) => kind switch
    {
        DesignArgKind.Int => (constructor ? size : random.Next(Math.Max(1, size))).ToString(CultureInfo.InvariantCulture),
        DesignArgKind.Double => (random.NextDouble() * Math.Max(1, size)).ToString("0.##", CultureInfo.InvariantCulture),
        DesignArgKind.Bool => random.Next(2) == 0 ? "false" : "true",
        DesignArgKind.Char => Quote(((char)('a' + random.Next(26))).ToString()),
        DesignArgKind.String => Quote(Word(random)),
        DesignArgKind.IntArray => "[" + string.Join(",", Enumerable.Range(0, length).Select(_ => random.Next(Math.Max(1, size)))) + "]",
        DesignArgKind.StringArray => "[" + string.Join(",", Enumerable.Range(0, length).Select(_ => Quote(Word(random)))) + "]",
        _ => throw new InvalidOperationException("An unsupported parameter reached generation.")
    };

    /// <summary>A short word of lowercase letters, as LeetCode's string design problems use.</summary>
    private static string Word(Random random)
    {
        var chars = new char[random.Next(3, 9)];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)('a' + random.Next(26));
        return new string(chars);
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Count(int arguments) => arguments == 1 ? "1 argument" : $"{arguments} arguments";
    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

public sealed class DesignWorkloadException : Exception
{
    public DesignWorkloadException(string message) : base(message) { }
}
