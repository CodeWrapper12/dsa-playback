using System.Text;
using System.Text.RegularExpressions;

namespace DsaViz.Engine.Rust;

public enum RsTokenKind { Identifier, Number, Text, Lifetime, Punctuation }

/// <summary>One token of Rust source: where it starts and ends in the text, and its line.</summary>
public sealed record RsToken(RsTokenKind Kind, string Text, int Start, int End, int Line, int Column);

public enum RsStatementKind { Block, Let, Expression, If, While, For, Loop, Match, Item, Empty }

/// <summary>
/// One statement in a block. Control statements hold their parts; any statement may hold
/// closures, whose block bodies are statements too.
/// </summary>
public sealed class RsStatement
{
    public required RsStatementKind Kind { get; init; }
    /// <summary>Token index of the statement's first token (its first attribute, if it has one).</summary>
    public required int First { get; set; }
    /// <summary>Token index of its last token: the ';', the closing '}', or the last token of a tail expression.</summary>
    public required int Last { get; set; }
    /// <summary>Blocks it contains: an if's then-block, a loop's body, a let-else's else.</summary>
    public List<RsStatement> Children { get; } = new();
    /// <summary>An if's else: a block, or another if.</summary>
    public RsStatement? Else { get; set; }
    /// <summary>Variables this statement declares, visible to the statements after it.</summary>
    public List<string> Declares { get; } = new();
    /// <summary>Variables bound for its body only: a for pattern, an if let or while let.</summary>
    public List<string> BodyDeclares { get; } = new();
    public List<RsArm> Arms { get; } = new();
    public List<RsClosure> Closures { get; } = new();
    /// <summary>A block's last expression, without a ';': the block's value.</summary>
    public bool IsTail { get; set; }
    /// <summary>The token just before the then-block or body: where the header ends.</summary>
    public int HeaderEnd { get; set; } = -1;
}

/// <summary>A match arm: its bindings, and its body — a block, or an expression up to the comma.</summary>
public sealed class RsArm
{
    public required IReadOnlyList<string> Bindings { get; init; }
    public required int BodyFirst { get; init; }
    public required int BodyLast { get; init; }
    public RsStatement? Block { get; init; }
    public List<RsClosure> Closures { get; } = new();
}

/// <summary>A closure: its parameters, its body when that is a block, and every name it mentions.</summary>
public sealed class RsClosure
{
    public required int Open { get; init; }
    public required int Close { get; init; }
    public required IReadOnlyList<string> Parameters { get; init; }
    public RsStatement? Body { get; init; }
    public required IReadOnlySet<string> Mentioned { get; init; }
    public string? Name { get; set; }
}

public sealed record RsParameter(string Name, string Type);

public sealed class RsFunction
{
    public required string Name { get; init; }
    /// <summary>The type of the impl block it is in; null for a free function.</summary>
    public string? Owner { get; init; }
    public bool IsPublic { get; init; }
    public bool HasSelf { get; init; }
    public bool IsGeneric { get; init; }
    public bool IsConst { get; init; }
    /// <summary>Declared inside another function's body.</summary>
    public bool IsNested { get; init; }
    public required IReadOnlyList<RsParameter> Parameters { get; init; }
    /// <summary>The return type as written, or null for none.</summary>
    public string? ReturnType { get; init; }
    public int Line { get; init; }
    /// <summary>Token index of the function's name.</summary>
    public int NameToken { get; init; }
    public required RsStatement Body { get; init; }
}

/// <summary>A struct the file defines, with its field names.</summary>
public sealed record RsStruct(string Name, IReadOnlyList<string> Fields, int Line);

public sealed class RsFile
{
    public required string Text { get; init; }
    public required IReadOnlyList<RsToken> Tokens { get; init; }
    public required IReadOnlyList<(string Text, int Line)> Comments { get; init; }
    public List<RsFunction> Functions { get; } = new();
    public List<RsStruct> Structs { get; } = new();
    /// <summary>Why the file could not be read, when it could not: a sentence with a line.</summary>
    public string? Problem { get; set; }
    /// <summary>Lines in the file.</summary>
    public int LineCount => Text.Count(c => c == '\n') + 1;
}

/// <summary>
/// Reads the Rust that LeetCode solutions are written in: impl blocks and free functions,
/// statements, the variables each binds, match arms and closures. Not a compiler — types
/// are not worked out, and expressions are read only as far as their extent — so it needs
/// nothing installed. The student's compiler is the authority on whether the code is valid;
/// this only has to find where statements are and what is in scope.
/// </summary>
public static class RustReader
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super",
        "trait", "true", "type", "unsafe", "use", "where", "while", "async", "await", "dyn", "box", "yield", "macro_rules"
    };

    private static readonly string[] MultiPunctuation =
    {
        "<<=", ">>=", "...", "..=", "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=",
        "^=", "&=", "|=", "<<", ".."
    };

    public static RsFile Read(string text)
    {
        var (tokens, comments) = Tokenize(text);
        var file = new RsFile { Text = text, Tokens = tokens, Comments = comments };
        try
        {
            new Parser(file).Items(0, tokens.Count, owner: null, nested: false);
        }
        catch (RsReadException ex)
        {
            file.Problem = ex.Message;
        }
        return file;
    }

    public static bool IsKeyword(string word) => Keywords.Contains(word);

    // ---- tokens -----------------------------------------------------------------------------

    private static (List<RsToken>, List<(string, int)>) Tokenize(string text)
    {
        var tokens = new List<RsToken>();
        var comments = new List<(string, int)>();
        int line = 1, lineStart = 0, i = 0;

        void Lines(int from, int to)
        {
            for (int k = from; k < to && k < text.Length; k++)
                if (text[k] == '\n') { line++; lineStart = k + 1; }
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { line++; lineStart = i + 1; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i, startLine = line, column = i - lineStart + 1;

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                comments.Add((CleanComment(text[i..end]), startLine));
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                // Block comments nest in Rust.
                int depth = 0, k = i;
                while (k < text.Length)
                {
                    if (string.CompareOrdinal(text, k, "/*", 0, 2) == 0) { depth++; k += 2; }
                    else if (string.CompareOrdinal(text, k, "*/", 0, 2) == 0) { depth--; k += 2; if (depth == 0) break; }
                    else k++;
                }
                var parts = text[i..k].Split('\n');
                for (int p = 0; p < parts.Length; p++) comments.Add((CleanComment(parts[p]), startLine + p));
                Lines(i, k);
                i = k;
                continue;
            }

            // Raw strings: r"..." r#"..."#, with an optional b.
            var raw = Regex.Match(text.Substring(i, Math.Min(12, text.Length - i)), @"^b?r(#*)""");
            if (raw.Success)
            {
                string close = "\"" + raw.Groups[1].Value;
                int end = text.IndexOf(close, i + raw.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                tokens.Add(new RsToken(RsTokenKind.Text, text[i..end], i, end, startLine, column));
                Lines(i, end);
                i = end;
                continue;
            }

            if (c == '"' || (c == 'b' && i + 1 < text.Length && text[i + 1] == '"'))
            {
                int k = i + (c == 'b' ? 2 : 1);
                while (k < text.Length && text[k] != '"') { if (text[k] == '\\') k++; k++; }
                int end = Math.Min(text.Length, k + 1);
                tokens.Add(new RsToken(RsTokenKind.Text, text[i..end], i, end, startLine, column));
                Lines(i, end);
                i = end;
                continue;
            }

            // A quote is a character literal — 'a', '\n', '\u{1F600}', b'x' — or a lifetime or label: 'a, 'outer.
            if (c == '\'' || (c == 'b' && i + 1 < text.Length && text[i + 1] == '\''))
            {
                int q = c == 'b' ? i + 1 : i;
                var character = Regex.Match(text.Substring(q, Math.Min(14, text.Length - q)), @"^'(\\u\{[0-9a-fA-F]+\}|\\.|[^\\'])'");
                if (character.Success)
                {
                    int end = q + character.Length;
                    tokens.Add(new RsToken(RsTokenKind.Text, text[i..end], i, end, startLine, column));
                    i = end;
                    continue;
                }
                if (c == '\'')
                {
                    int k = i + 1;
                    while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                    tokens.Add(new RsToken(RsTokenKind.Lifetime, text[i..k], i, k, startLine, column));
                    i = k;
                    continue;
                }
            }

            if (char.IsDigit(c))
            {
                int k = i + 1;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_' ||
                       (text[k] == '.' && k + 1 < text.Length && char.IsDigit(text[k + 1]) && !text[i..k].Contains('.')) ||
                       (text[k] is '+' or '-' && text[k - 1] is 'e' or 'E' && !text[i..k].StartsWith("0x"))))
                    k++;
                tokens.Add(new RsToken(RsTokenKind.Number, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int k = i + 1;
                // r#name: a raw identifier.
                if (c == 'r' && k + 1 < text.Length && text[k] == '#' && (char.IsLetter(text[k + 1]) || text[k + 1] == '_')) k += 2;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                tokens.Add(new RsToken(RsTokenKind.Identifier, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }

            // '>' and '<' stand alone, so the two in Vec<Vec<i32>> close two generics.
            string symbol = MultiPunctuation.FirstOrDefault(p => string.CompareOrdinal(text, i, p, 0, p.Length) == 0) ?? c.ToString();
            tokens.Add(new RsToken(RsTokenKind.Punctuation, symbol, i, i + symbol.Length, startLine, column));
            i += symbol.Length;
        }
        return (tokens, comments);
    }

    private static string CleanComment(string text)
    {
        string trimmed = text.TrimEnd('\r').Trim();
        trimmed = Regex.Replace(trimmed, @"^(///|//!|//|/\*\*|/\*!|/\*|\*/|\*)\s?", "");
        if (trimmed.EndsWith("*/")) trimmed = trimmed[..^2];
        return trimmed.TrimEnd();
    }

    /// <summary>Joins type tokens as a person writes them: Vec&lt;Vec&lt;i32&gt;&gt;, HashMap&lt;i32, i32&gt;, &amp;mut [i32].</summary>
    public static string TypeText(IReadOnlyList<RsToken> tokens, int from, int to)
    {
        var text = new StringBuilder();
        for (int k = from; k <= to; k++)
        {
            string t = tokens[k].Text;
            if (text.Length > 0)
            {
                char last = text[^1];
                bool word = tokens[k].Kind is RsTokenKind.Identifier or RsTokenKind.Number or RsTokenKind.Lifetime;
                bool lastWord = char.IsLetterOrDigit(last) || last == '_';
                if ((word && lastWord) || last == ',' || (word && tokens[k - 1].Kind == RsTokenKind.Lifetime)) text.Append(' ');
            }
            text.Append(t);
        }
        return text.ToString();
    }

    // ---- parsing -----------------------------------------------------------------------------

    private sealed class RsReadException : Exception
    {
        public RsReadException(string message) : base(message) { }
    }

    private sealed class Parser
    {
        private readonly RsFile _file;
        private readonly IReadOnlyList<RsToken> _t;

        public Parser(RsFile file)
        {
            _file = file;
            _t = file.Tokens;
        }

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";
        private bool Is(int i, RsTokenKind kind) => i >= 0 && i < _t.Count && _t[i].Kind == kind;
        private bool IsName(int i) => Is(i, RsTokenKind.Identifier) && !Keywords.Contains(_t[i].Text);

        private RsReadException Problem(int i, string what)
            => new($"line {(i < _t.Count ? _t[i].Line : _t.Count > 0 ? _t[^1].Line : 1)}: {what}");

        /// <summary>The index of the bracket that closes the one at <paramref name="open"/>.</summary>
        public int Match(int open)
        {
            string o = T(open);
            string c = o switch { "(" => ")", "[" => "]", "{" => "}", _ => throw Problem(open, $"expected a bracket, found '{o}'") };
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                string s = T(i);
                if (_t[i].Kind != RsTokenKind.Punctuation) continue;
                if (s is "(" or "[" or "{") depth++;
                else if (s is ")" or "]" or "}")
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (s != c) throw Problem(i, $"'{s}' does not match the '{o}' on line {_t[open].Line}");
                        return i;
                    }
                }
            }
            throw Problem(open, $"the '{o}' here is never closed");
        }

        /// <summary>The '&gt;' closing the generic arguments opened at <paramref name="open"/>; -1 if it is a comparison.</summary>
        private int MatchAngle(int open, int limit)
        {
            int depth = 0;
            for (int i = open; i < limit; i++)
            {
                string s = T(i);
                if (s == "<") depth++;
                else if (s == ">") { if (--depth == 0) return i; }
                else if (s is "(" or "[") i = Match(i);
                else if (s is ";" or "{" or "}" or "&&" or "||") return -1;
            }
            return -1;
        }

        private int SkipAttributes(int i, int limit)
        {
            while (i < limit && T(i) == "#" && (T(i + 1) == "[" || (T(i + 1) == "!" && T(i + 2) == "[")))
                i = Match(T(i + 1) == "[" ? i + 1 : i + 2) + 1;
            return i;
        }

        /// <summary>From <paramref name="i"/> to the next ';' outside brackets, or the limit.</summary>
        private int SkipTo(int i, string what, int limit)
        {
            while (i < limit && T(i) != what)
            {
                if (T(i) is "(" or "[" or "{" && _t[i].Kind == RsTokenKind.Punctuation) i = Match(i);
                i++;
            }
            return i;
        }

        // ---- items --------------------------------------------------------------------------------

        public void Items(int from, int to, string? owner, bool nested)
        {
            int i = from;
            while (i < to)
            {
                i = SkipAttributes(i, to);
                if (i >= to) break;
                i = Item(i, to, owner, nested, out _);
            }
        }

        /// <summary>One item starting at <paramref name="i"/>; returns the index after it.</summary>
        private int Item(int i, int to, string? owner, bool nested, out bool wasItem)
        {
            wasItem = true;
            bool isPublic = false;
            if (T(i) == "pub")
            {
                isPublic = true;
                i++;
                if (T(i) == "(") i = Match(i) + 1;      // pub(crate)
            }
            while (T(i) is "const" or "unsafe" or "async" or "extern" or "default" &&
                   !(T(i) == "const" && T(i + 1) != "fn" && T(i + 1) != "unsafe"))
            {
                if (T(i) == "extern" && Is(i + 1, RsTokenKind.Text)) i++;
                i++;
            }
            bool isConst = i > 0 && T(i - 1) == "const";

            switch (T(i))
            {
                case ";":
                    return i + 1;
                case "fn":
                    return Function(i, to, owner, isPublic, isConst, nested);
                case "impl":
                {
                    int open = i + 1;
                    while (open < to && T(open) != "{") { if (T(open) == "<") { int a = MatchAngle(open, to); open = a < 0 ? open + 1 : a + 1; } else open++; }
                    if (open >= to) throw Problem(i, "an impl block without a body");
                    // impl Solution, impl<T> Foo<T>, impl Trait for Foo: the type is the last name before the body or a 'for'.
                    int forAt = -1;
                    for (int k = i + 1; k < open; k++) if (T(k) == "for") forAt = k;
                    int nameFrom = forAt >= 0 ? forAt + 1 : i + 1;
                    if (T(nameFrom) == "<") nameFrom = MatchAngle(nameFrom, open) + 1;
                    string? type = null;
                    for (int k = nameFrom; k < open && T(k) != "where"; k++)
                    {
                        if (T(k) == "<") break;
                        if (IsName(k)) type = T(k);
                    }
                    int close = Match(open);
                    Items(open + 1, close, type ?? "?", nested);
                    return close + 1;
                }
                case "struct":
                case "union":
                {
                    string name = T(i + 1);
                    int k = i + 2;
                    if (T(k) == "<") k = Math.Max(k + 1, MatchAngle(k, to) + 1);
                    while (k < to && T(k) is not "{" and not ";" and not "(") k++;
                    var fields = new List<string>();
                    if (T(k) == "{")
                    {
                        int close = Match(k);
                        int depth = 0;
                        for (int f = k + 1; f < close; f++)
                        {
                            if (T(f) is "(" or "[" or "{" or "<") depth++;
                            else if (T(f) is ")" or "]" or "}" or ">") depth--;
                            else if (depth == 0 && IsName(f) && T(f + 1) == ":" && T(f + 2) != ":") fields.Add(T(f));
                        }
                        k = close + 1;
                    }
                    else if (T(k) == "(") k = SkipTo(Match(k), ";", to) + 1;
                    else k++;
                    _file.Structs.Add(new RsStruct(name, fields, _t[Math.Min(i + 1, _t.Count - 1)].Line));
                    return k;
                }
                case "mod":
                case "trait":
                case "enum":
                {
                    int k = i + 1;
                    while (k < to && T(k) is not "{" and not ";") k++;
                    if (T(k) == "{")
                    {
                        int close = Match(k);
                        if (T(i) == "mod") Items(k + 1, close, null, nested);
                        return close + 1;
                    }
                    return k + 1;
                }
                case "use":
                case "type":
                case "static":
                case "const":
                case "extern":
                    return SkipTo(i, ";", to) + 1;
                case "macro_rules":
                {
                    int k = i + 2;
                    while (k < to && IsName(k)) k++;
                    if (T(k) is "{" or "(" or "[") { int close = Match(k); return T(close + 1) == ";" ? close + 2 : close + 1; }
                    return k + 1;
                }
            }

            wasItem = false;
            if (owner is null && !nested)
            {
                // Something not understood at the top level: a macro invocation, most likely.
                int k = SkipTo(i, ";", to);
                for (int b = i; b < k; b++) if (T(b) == "{") { k = Match(b); break; }
                return k + 1;
            }
            return SkipTo(i, ";", to) + 1;
        }

        private int Function(int fnAt, int to, string? owner, bool isPublic, bool isConst, bool nested)
        {
            int nameAt = fnAt + 1;
            string name = T(nameAt);
            int k = nameAt + 1;
            bool generic = false;
            if (T(k) == "<")
            {
                int close = MatchAngle(k, to);
                // Lifetimes alone are not generic in a way that matters: fn f<'a>(s: &'a str).
                generic = Enumerable.Range(k + 1, Math.Max(0, close - k - 1)).Any(x => IsName(x) && T(x - 1) is "<" or ",");
                k = close + 1;
            }
            if (T(k) != "(") throw Problem(k, $"expected '(' after fn {name}");
            int paramClose = Match(k);
            var (parameters, hasSelf) = Parameters(k, paramClose);
            k = paramClose + 1;

            string? returnType = null;
            if (T(k) == "->")
            {
                int r = k + 1;
                while (r < to && T(r) is not "{" and not ";" and not "where") { if (T(r) == "<") { int a = MatchAngle(r, to); r = a < 0 ? r + 1 : a + 1; } else if (T(r) is "(" or "[") r = Match(r) + 1; else r++; }
                returnType = TypeText(_t, k + 1, r - 1);
                k = r;
            }
            while (k < to && T(k) is not "{" and not ";") k++;
            if (T(k) == ";") return k + 1;                    // a declaration in a trait

            var body = Block(k);
            _file.Functions.Add(new RsFunction
            {
                Name = name,
                Owner = owner,
                IsPublic = isPublic,
                HasSelf = hasSelf,
                IsGeneric = generic,
                IsConst = isConst,
                IsNested = nested,
                Parameters = parameters,
                ReturnType = returnType,
                Line = _t[nameAt].Line,
                NameToken = nameAt,
                Body = body
            });
            return body.Last + 1;
        }

        private (List<RsParameter>, bool) Parameters(int open, int close)
        {
            var parameters = new List<RsParameter>();
            bool hasSelf = false;
            foreach (var (from, to) in Split(open + 1, close))
            {
                int colon = -1;
                for (int i = from; i <= to; i++)
                {
                    if (T(i) is "(" or "[") { i = Match(i); continue; }
                    if (T(i) == ":") { colon = i; break; }
                }
                if (colon < 0)
                {
                    if (Enumerable.Range(from, to - from + 1).Any(i => T(i) == "self")) hasSelf = true;
                    continue;
                }
                if (Enumerable.Range(from, colon - from).Any(i => T(i) == "self")) { hasSelf = true; continue; }
                var names = Bindings(from, colon - 1);
                string name = names.Count == 1 ? names[0] : $"arg{parameters.Count}";
                parameters.Add(new RsParameter(name, TypeText(_t, colon + 1, to)));
            }
            return (parameters, hasSelf);
        }

        /// <summary>The comma-separated parts between two tokens, ignoring commas inside brackets and generics.</summary>
        private List<(int From, int To)> Split(int from, int end)
        {
            var parts = new List<(int, int)>();
            int start = from, angle = 0;
            for (int i = from; i <= end; i++)
            {
                if (i < end && T(i) is "(" or "[" or "{" && _t[i].Kind == RsTokenKind.Punctuation) { i = Match(i); continue; }
                if (i < end && T(i) == "<") angle++;
                else if (i < end && T(i) == ">" && angle > 0) angle--;
                else if (i == end || (T(i) == "," && angle == 0))
                {
                    if (i > start) parts.Add((start, i - 1));
                    start = i + 1;
                }
            }
            return parts;
        }

        /// <summary>
        /// The names a pattern binds: x in `mut x`, a and b in `(a, b)`, n in `Some(n)`, k in
        /// `Point { k, .. }`. Paths, variants, constants and field names are not bindings.
        /// </summary>
        public List<string> Bindings(int from, int to)
        {
            var names = new List<string>();
            for (int i = from; i <= to && i < _t.Count; i++)
            {
                if (!IsName(i)) continue;
                string word = T(i);
                if (char.IsUpper(word[0]) || word == "_") continue;
                string next = T(i + 1), previous = T(i - 1);
                if (next is "(" or "{" or "::" or "!" || previous is "::" or "." or "'") continue;
                if (next == ":" && i + 1 <= to) continue;          // a field name: Point { x: a }
                if (!names.Contains(word)) names.Add(word);
            }
            return names;
        }

        // ---- statements ---------------------------------------------------------------------------

        private RsStatement Block(int open)
        {
            if (T(open) != "{") throw Problem(open, $"expected '{{', found '{T(open)}'");
            int close = Match(open);
            var block = new RsStatement { Kind = RsStatementKind.Block, First = open, Last = close };
            int i = open + 1;
            while (i < close)
            {
                var statement = Statement(ref i, close);
                block.Children.Add(statement);
            }
            // The last expression without a ';' is the block's value.
            if (block.Children.LastOrDefault() is { } last && last.Kind is not (RsStatementKind.Let or RsStatementKind.Item or RsStatementKind.Empty)
                && T(last.Last) != ";")
                last.IsTail = true;
            return block;
        }

        /// <summary>
        /// A statement that starts with a block — if, match, loop, while, for, { } — ends at its
        /// closing brace, as Rust parses it: `while c { } -1` is a loop, then the expression -1.
        /// A ';' after it belongs to it.
        /// </summary>
        private RsStatement Continue(RsStatement statement, ref int i, int limit)
        {
            if (i < limit && T(i) == ";") { statement.Last = i; i++; }
            return statement;
        }

        private RsStatement Statement(ref int i, int limit)
        {
            int start = i;
            i = SkipAttributes(i, limit);
            int head = i;

            // A label: 'outer: loop { }
            if (Is(i, RsTokenKind.Lifetime) && T(i + 1) == ":") i += 2;
            string s = T(i);

            if (s == ";") { i++; return new RsStatement { Kind = RsStatementKind.Empty, First = start, Last = start }; }
            if (s == "{" || (s == "unsafe" && T(i + 1) == "{"))
            {
                var block = Block(s == "{" ? i : i + 1);
                block.First = start;
                i = block.Last + 1;
                return Continue(block, ref i, limit);
            }

            switch (s)
            {
                case "let": return Let(start, ref i, limit);
                case "if":
                {
                    var statement = If(start, ref i);
                    return Continue(statement, ref i, limit);
                }
                case "while":
                case "for":
                {
                    int open = HeaderBlock(i + 1, limit);
                    var statement = new RsStatement
                    {
                        Kind = s == "while" ? RsStatementKind.While : RsStatementKind.For,
                        First = start, Last = open, HeaderEnd = open - 1
                    };
                    if (s == "for")
                    {
                        int inAt = i + 1;
                        while (inAt < open && T(inAt) != "in") inAt++;
                        statement.BodyDeclares.AddRange(Bindings(i + 1, inAt - 1));
                        FindClosures(statement.Closures, inAt + 1, open - 1, null);
                    }
                    else if (T(i + 1) == "let")
                    {
                        int equals = TopLevel(i + 2, open, "=");
                        statement.BodyDeclares.AddRange(Bindings(i + 2, equals - 1));
                        FindClosures(statement.Closures, equals + 1, open - 1, null);
                    }
                    else FindClosures(statement.Closures, i + 1, open - 1, null);
                    var body = Block(open);
                    statement.Children.Add(body);
                    statement.Last = body.Last;
                    i = body.Last + 1;
                    return Continue(statement, ref i, limit);
                }
                case "loop":
                {
                    var body = Block(i + 1);
                    var statement = new RsStatement { Kind = RsStatementKind.Loop, First = start, Last = body.Last, HeaderEnd = i };
                    statement.Children.Add(body);
                    i = body.Last + 1;
                    return Continue(statement, ref i, limit);
                }
                case "match":
                {
                    var statement = MatchStatement(start, ref i, limit);
                    return Continue(statement, ref i, limit);
                }
                case "fn":
                case "struct":
                case "enum":
                case "impl":
                case "use":
                case "mod":
                case "trait":
                case "type":
                case "static":
                case "macro_rules":
                case "pub":
                {
                    i = Item(head, limit, null, nested: true, out _);
                    return new RsStatement { Kind = RsStatementKind.Item, First = start, Last = i - 1 };
                }
                case "const" when T(i + 1) != "{":
                {
                    i = Item(head, limit, null, nested: true, out _);
                    return new RsStatement { Kind = RsStatementKind.Item, First = start, Last = i - 1 };
                }
            }

            // An expression: up to its ';', or, as the block's last, to the block's end.
            int end = SkipTo(i, ";", limit);
            var expression = new RsStatement { Kind = RsStatementKind.Expression, First = start, Last = end < limit ? end : end - 1 };
            FindClosures(expression.Closures, i, Math.Min(end, limit) - 1, null);
            i = end < limit ? end + 1 : end;
            return expression;
        }

        /// <summary>The '{' that opens a control statement's body: the first one outside brackets.</summary>
        private int HeaderBlock(int from, int limit)
        {
            for (int k = from; k < limit; k++)
            {
                if (T(k) == "{" && _t[k].Kind == RsTokenKind.Punctuation) return k;
                if (T(k) is "(" or "[") k = Match(k);
                // A closure in the header, |x| { ... }, has a braced body before the real one.
                else if (T(k) is "|" or "||" && ClosureStart(k)) k = ClosureEnd(k, limit);
            }
            throw Problem(from, "expected '{' to open a block");
        }

        private int TopLevel(int from, int to, string what)
        {
            for (int k = from; k < to; k++)
            {
                if (T(k) is "(" or "[" or "{" && _t[k].Kind == RsTokenKind.Punctuation) { k = Match(k); continue; }
                if (T(k) == what) return k;
            }
            return to;
        }

        private RsStatement Let(int start, ref int i, int limit)
        {
            int letAt = i;
            int end = SkipTo(i, ";", limit);
            // The pattern runs to the type's ':' or the '=' — whichever comes first outside brackets.
            int patternEnd = letAt + 1;
            while (patternEnd < end && T(patternEnd) is not "=" and not ":" and not ";")
            {
                if (T(patternEnd) is "(" or "[" or "{") patternEnd = Match(patternEnd);
                patternEnd++;
            }
            var statement = new RsStatement { Kind = RsStatementKind.Let, First = start, Last = Math.Min(end, limit - 1) };
            statement.Declares.AddRange(Bindings(letAt + 1, patternEnd - 1));

            int equals = TopLevel(letAt + 1, end, "=");
            if (equals < end)
            {
                // let ... else { ... }: the else block diverges, and is stepped.
                int elseAt = -1;
                for (int k = equals + 1; k < end; k++)
                {
                    if (T(k) is "(" or "[") { k = Match(k); continue; }
                    if (T(k) == "{") { k = Match(k); continue; }
                    if (T(k) == "else" && T(k + 1) == "{") { elseAt = k; break; }
                    if (T(k) is "|" or "||" && ClosureStart(k)) { k = ClosureEnd(k, end) - 1; continue; }
                }
                FindClosures(statement.Closures, equals + 1, (elseAt < 0 ? end : elseAt) - 1, statement.Declares.Count == 1 ? statement.Declares[0] : null);
                if (elseAt >= 0) statement.Children.Add(Block(elseAt + 1));
            }
            i = end < limit ? end + 1 : end;
            return statement;
        }

        private RsStatement If(int start, ref int i)
        {
            int ifAt = i;
            int open = HeaderBlock(ifAt + 1, _t.Count);
            var statement = new RsStatement { Kind = RsStatementKind.If, First = start, Last = open, HeaderEnd = open - 1 };
            if (T(ifAt + 1) == "let")
            {
                int equals = TopLevel(ifAt + 2, open, "=");
                statement.BodyDeclares.AddRange(Bindings(ifAt + 2, equals - 1));
                FindClosures(statement.Closures, equals + 1, open - 1, null);
            }
            else FindClosures(statement.Closures, ifAt + 1, open - 1, null);

            var then = Block(open);
            statement.Children.Add(then);
            statement.Last = then.Last;
            i = then.Last + 1;
            if (T(i) == "else")
            {
                i++;
                if (T(i) == "if")
                {
                    int at = i;
                    statement.Else = If(at, ref i);
                }
                else
                {
                    statement.Else = Block(i);
                    i = statement.Else.Last + 1;
                }
                statement.Last = statement.Else.Last;
            }
            return statement;
        }

        private RsStatement MatchStatement(int start, ref int i, int limit)
        {
            int open = HeaderBlock(i + 1, limit);
            var statement = new RsStatement { Kind = RsStatementKind.Match, First = start, Last = open, HeaderEnd = open - 1 };
            FindClosures(statement.Closures, i + 1, open - 1, null);
            int close = Match(open);
            int k = open + 1;
            while (k < close)
            {
                k = SkipAttributes(k, close);
                if (k >= close) break;
                int arrow = k;
                while (arrow < close && T(arrow) != "=>") { if (T(arrow) is "(" or "[" or "{") arrow = Match(arrow); arrow++; }
                if (arrow >= close) throw Problem(k, "a match arm without '=>'");
                int guard = TopLevel(k, arrow, "if");
                var bindings = Bindings(k, Math.Min(guard, arrow) - 1);
                int bodyFirst = arrow + 1;
                RsArm arm;
                if (T(bodyFirst) == "{")
                {
                    var block = Block(bodyFirst);
                    arm = new RsArm { Bindings = bindings, BodyFirst = bodyFirst, BodyLast = block.Last, Block = block };
                    k = block.Last + 1;
                    // A block body may still go on as an expression — { .. }.len() — up to the comma.
                    if (T(k) is not "," and not "}" && k < close)
                    {
                        int end = TopLevel(k, close, ",");
                        arm = new RsArm { Bindings = bindings, BodyFirst = bodyFirst, BodyLast = end - 1 };
                        k = end;
                    }
                    if (T(k) == ",") k++;
                }
                else
                {
                    int end = k = bodyFirst;
                    for (; end < close; end++)
                    {
                        if (T(end) is "(" or "[" or "{" && _t[end].Kind == RsTokenKind.Punctuation) { end = Match(end); continue; }
                        if (T(end) is "|" or "||" && ClosureStart(end)) { end = ClosureEnd(end, close) - 1; continue; }
                        if (T(end) == ",") break;
                    }
                    arm = new RsArm { Bindings = bindings, BodyFirst = bodyFirst, BodyLast = end - 1 };
                    FindClosures(arm.Closures, bodyFirst, end - 1, null);
                    k = end < close ? end + 1 : end;
                }
                FindClosures(arm.Closures, Math.Min(guard, arrow), arrow - 1, null);
                statement.Arms.Add(arm);
            }
            statement.Last = close;
            i = close + 1;
            return statement;
        }

        // ---- closures -----------------------------------------------------------------------------

        /// <summary>
        /// Whether the '|' or '||' at <paramref name="k"/> opens a closure rather than being
        /// an or: it does where an expression starts, not after a value.
        /// </summary>
        public bool ClosureStart(int k)
        {
            int p = k - 1;
            if (p < 0) return true;
            var previous = _t[p];
            if (previous.Text == "move") return true;
            if (previous.Kind is RsTokenKind.Number or RsTokenKind.Text) return false;
            if (previous.Kind == RsTokenKind.Identifier) return Keywords.Contains(previous.Text) && previous.Text is not ("self" or "Self" or "true" or "false");
            return previous.Text is not (")" or "]" or "}" or "?");
        }

        /// <summary>The index just after the closure starting at <paramref name="k"/>.</summary>
        public int ClosureEnd(int k, int limit)
        {
            int bodyStart = ParamsEnd(k) + 1;
            if (T(bodyStart) == "->")
            {
                while (bodyStart < limit && T(bodyStart) != "{") bodyStart++;
            }
            if (T(bodyStart) == "{") return Match(bodyStart) + 1;
            for (int e = bodyStart; e < limit; e++)
            {
                if (T(e) is "(" or "[" or "{" && _t[e].Kind == RsTokenKind.Punctuation) { e = Match(e); continue; }
                if (T(e) is "|" or "||" && ClosureStart(e)) { e = ClosureEnd(e, limit) - 1; continue; }
                if (T(e) is "," or ";" or ")" or "]" or "}") return e;
            }
            return limit;
        }

        private int ParamsEnd(int k)
        {
            if (T(k) == "||") return k;
            for (int e = k + 1; e < _t.Count; e++)
            {
                if (T(e) is "(" or "[") { e = Match(e); continue; }
                if (T(e) == "|") return e;
            }
            return k;
        }

        private void FindClosures(List<RsClosure> into, int from, int to, string? assignedTo)
        {
            for (int k = from; k <= to && k < _t.Count; k++)
            {
                if (T(k) is "(" or "[" or "{" && _t[k].Kind == RsTokenKind.Punctuation)
                {
                    // Closures inside brackets are still this statement's: f(|x| { .. }).
                    continue;
                }
                if (T(k) is not ("|" or "||") || !ClosureStart(k)) continue;

                int paramsEnd = ParamsEnd(k);
                var parameters = new List<string>();
                if (T(k) == "|")
                    foreach (var (pf, pt) in Split(k + 1, paramsEnd))
                    {
                        int colon = TopLevel(pf, pt + 1, ":");
                        parameters.AddRange(Bindings(pf, Math.Min(colon, pt + 1) - 1));
                    }
                int bodyStart = paramsEnd + 1;
                if (T(bodyStart) == "->") { while (bodyStart <= to && T(bodyStart) != "{") bodyStart++; }
                int end = ClosureEnd(k, to + 1);
                RsStatement? body = T(bodyStart) == "{" ? Block(bodyStart) : null;
                var mentioned = new HashSet<string>(StringComparer.Ordinal);
                for (int m = bodyStart; m < end; m++) if (IsName(m)) mentioned.Add(T(m));
                int open = k > 0 && T(k - 1) == "move" ? k - 1 : k;
                into.Add(new RsClosure
                {
                    Open = open, Close = end - 1, Parameters = parameters, Body = body, Mentioned = mentioned,
                    Name = assignedTo
                });
                assignedTo = null;
                k = end - 1;
            }
        }
    }
}
