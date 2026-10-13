using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Cpp;

/// <summary>
/// A reading of a C++ function's time complexity from its shape: how deeply its loops nest,
/// whether a loop halves what is left, and whether it calls itself. A second opinion beside
/// the measurement, as for C# and Python — when the two disagree, the measurement is the one
/// that ran.
/// </summary>
public static class CppEstimator
{
    public static readonly StaticEstimate Space = new(ComplexityClass.Unknown,
        "Reading C++ code for space is not supported; the measured figures are the answer.", 0, 0, 0);

    public static StaticEstimate Time(CppFile file, CppFunction function)
    {
        var (linear, log) = Depth(file, function.Body);
        int selfCalls = file.Tokens
            .Where(t => t.Start > file.Tokens[function.Body.First].Start && t.End <= file.Tokens[function.Body.Last].End)
            .Select((t, i) => (t, i))
            .Count(x => x.t.Text == function.Name);

        if (selfCalls >= 2 && OnChildren(file, function))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself on a node's children, so each node is visited once.", linear, log, selfCalls);
        if (selfCalls >= 2 && Memoised(file, function))
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself {selfCalls} times, but stores each answer and returns it when asked again, " +
                "so each value is worked out once.", linear, log, selfCalls);
        if (selfCalls >= 2)
            return new StaticEstimate(ComplexityClass.Exponential,
                $"{function.Name} calls itself {selfCalls} times in its body; without memoisation that branches at every call.",
                linear, log, selfCalls);
        if (selfCalls == 1)
            return new StaticEstimate(linear > 0 ? ComplexityClass.Quadratic : ComplexityClass.Linear,
                $"{function.Name} calls itself once per call{(linear > 0 ? ", with a loop in each call" : "")}.", linear, log, selfCalls);

        var cls = (linear, log) switch
        {
            (0, 0) => ComplexityClass.Constant,
            (0, _) => ComplexityClass.Logarithmic,
            (1, 0) => ComplexityClass.Linear,
            (1, _) => ComplexityClass.Linearithmic,
            (2, _) => ComplexityClass.Quadratic,
            _ => ComplexityClass.Cubic
        };
        string reasoning = linear == 0 && log == 0
            ? "No loops or recursion."
            : $"Loops nest {linear} deep over the input{(log > 0 ? $", and {log} loop{(log == 1 ? "" : "s")} halve what is left" : "")}.";
        return new StaticEstimate(cls, reasoning, linear, log, 0);
    }

    /// <summary>Every call the function makes to itself passes a child: root->left, node->right, head->next.</summary>
    private static bool OnChildren(CppFile file, CppFunction function)
    {
        string body = file.Text[file.Tokens[function.Body.First].Start..file.Tokens[function.Body.Last].End];
        var calls = System.Text.RegularExpressions.Regex.Matches(body, $@"\b{function.Name}\s*\(([^()]*(\([^()]*\))?[^()]*)\)");
        return calls.Count > 0 && calls.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.Groups[1].Value, @"(->|\.)(left|right|next)\b"));
    }

    /// <summary>
    /// A memo table: some name is both returned from — return memo[n] — and stored into —
    /// memo[n] = ... — in the function.
    /// </summary>
    private static bool Memoised(CppFile file, CppFunction function)
    {
        string body = file.Text[file.Tokens[function.Body.First].Start..file.Tokens[function.Body.Last].End];
        return System.Text.RegularExpressions.Regex.Matches(body, @"return\s+(\w+)\s*\[")
            .Select(m => m.Groups[1].Value)
            .Any(name => System.Text.RegularExpressions.Regex.IsMatch(body, $@"\b{name}\s*\[[^\]]*\]\s*="));
    }

    /// <summary>
    /// The deepest nesting of input-sized loops, and of halving loops, in a statement. A scan
    /// in the library — std::find, std::count — is a loop too, though it is not written as one.
    /// </summary>
    private static (int Linear, int Log) Depth(CppFile file, CppStatement statement)
    {
        var children = statement.Children.Concat(statement.Else is null ? Array.Empty<CppStatement>() : new[] { statement.Else });
        var (innerLinear, innerLog) = children.Select(c => Depth(file, c)).DefaultIfEmpty((0, 0)).MaxBy(d => d.Item1 * 10 + d.Item2);
        if (statement.Kind is StatementKind.Simple or StatementKind.Return or StatementKind.If && ScansInLibrary(file, statement))
            innerLinear = Math.Max(innerLinear, 1);
        if (statement.Kind is not (StatementKind.For or StatementKind.RangeFor or StatementKind.While or StatementKind.Do))
            return (innerLinear, innerLog);
        return Halves(file, statement) ? (innerLinear, innerLog + 1) : (innerLinear + 1, innerLog);
    }

    private static bool ScansInLibrary(CppFile file, CppStatement statement)
    {
        int last = statement.Kind == StatementKind.If && statement.Children.Count > 0 ? statement.Children[0].First - 1 : statement.Last;
        for (int i = statement.First; i < last; i++)
        {
            var token = file.Tokens[i];
            if (token.Kind == TokenKind.Identifier && file.Tokens[i + 1].Text == "(" &&
                CppInstrumenter.Algorithms.TryGetValue(token.Text, out var algorithm) && algorithm.Helper == "scanned" &&
                file.Tokens[Math.Max(0, i - 1)].Text is not "." and not "->")
                return true;
        }
        return false;
    }

    /// <summary>A loop whose variable is halved or doubled each time round: i /= 2, i *= 2, lo = mid + 1 with mid in the middle.</summary>
    private static bool Halves(CppFile file, CppStatement loop)
    {
        string text = file.Text[file.Tokens[loop.First].Start..file.Tokens[loop.Last].End];
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"(/=\s*2\b|\*=\s*2\b|>>=\s*1\b|<<=\s*1\b|\bmid\b)");
    }
}
