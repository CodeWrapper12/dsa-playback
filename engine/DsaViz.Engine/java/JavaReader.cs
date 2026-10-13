using System.Text;
using System.Text.RegularExpressions;

namespace DsaViz.Engine.Java;

public enum JvTokenKind { Identifier, Number, Text, Punctuation }

/// <summary>One token of Java source: where it starts and ends in the text, and its line.</summary>
public sealed record JvToken(JvTokenKind Kind, string Text, int Start, int End, int Line, int Column);

public enum JvStatementKind { Block, Local, Expression, Return, If, For, ForEach, While, Do, Switch, Try, Synchronized, Label, Item, Empty }

/// <summary>A local variable: its name and the type it was declared with ("var" when inferred).</summary>
public sealed record JvVariable(string Name, string Type);

/// <summary>
/// One statement. Control statements hold their parts — an if's branches, a loop's body, a
/// try's blocks, a switch's statements — and any statement may hold lambdas whose bodies
/// are blocks of statements too.
/// </summary>
public sealed class JvStatement
{
    public required JvStatementKind Kind { get; init; }
    /// <summary>Token index of its first token.</summary>
    public required int First { get; set; }
    /// <summary>Token index of its last token: the ';' or the closing '}'.</summary>
    public required int Last { get; set; }
    public List<JvStatement> Children { get; } = new();
    public JvStatement? Else { get; set; }
    /// <summary>Variables this statement declares, visible to the statements after it.</summary>
    public List<JvVariable> Declares { get; } = new();
    /// <summary>Variables a header declares for the body only: a for loop's, a try's resources.</summary>
    public List<JvVariable> BodyDeclares { get; } = new();
    /// <summary>For a try: each catch block's parameter, by the block's position in <see cref="Children"/>.</summary>
    public Dictionary<int, JvVariable> CatchParameters { get; } = new();
    public List<JvLambda> Lambdas { get; } = new();
    /// <summary>The last token of a control statement's header: the ')' before its body.</summary>
    public int HeaderEnd { get; set; } = -1;
    /// <summary>A statement that stands alone where Java allows one statement without braces.</summary>
    public bool Unbraced { get; set; }
}

/// <summary>A lambda: its parameters, its body when that is a block, and every name it mentions.</summary>
public sealed class JvLambda
{
    public required int Arrow { get; init; }
    public required IReadOnlyList<JvVariable> Parameters { get; init; }
    public JvStatement? Body { get; init; }
    public required int BodyLast { get; init; }
    public required IReadOnlySet<string> Mentioned { get; init; }
    public string? Name { get; set; }
}

public sealed record JvParameter(string Name, string Type);

public sealed class JvMethod
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }
    public bool IsConstructor { get; init; }
    /// <summary>The return type as written; "" for a constructor.</summary>
    public required string ReturnType { get; init; }
    public required IReadOnlyList<JvParameter> Parameters { get; init; }
    public int Line { get; init; }
    public int NameToken { get; init; }
    public required JvStatement Body { get; init; }
}

public sealed class JvClass
{
    public required string Name { get; init; }
    public int Line { get; init; }
    public bool IsPublic { get; init; }
    /// <summary>Directly inside the file, not inside another class.</summary>
    public bool TopLevel { get; init; }
    public List<JvVariable> Fields { get; } = new();
    /// <summary>Parameter counts of its constructors, with whether each is public.</summary>
    public List<(int Parameters, bool Public)> Constructors { get; } = new();
    public bool Constructible => Constructors.Count == 0 || Constructors.Any(c => c.Parameters == 0);
}

public sealed class JvFile
{
    public required string Text { get; init; }
    public required IReadOnlyList<JvToken> Tokens { get; init; }
    public required IReadOnlyList<(string Text, int Line)> Comments { get; init; }
    public List<JvClass> Classes { get; } = new();
    public List<JvMethod> Methods { get; } = new();
    /// <summary>Where the package declaration is, when the file has one: it is blanked out.</summary>
    public (int Start, int End)? Package { get; set; }
    public string? Problem { get; set; }
    public int LineCount => Text.Count(c => c == '\n') + 1;
}

/// <summary>
/// Reads the Java that LeetCode solutions are written in: classes and their methods,
/// statements, the variables each declares with their types, lambdas. Not a compiler —
/// expressions are read only as far as their extent — so it needs nothing installed; the
/// student's javac is the authority on whether the code is valid.
/// </summary>
public static class JavaReader
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default",
        "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import",
        "instanceof", "int", "interface", "long", "native", "new", "package", "private", "protected", "public", "return",
        "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try",
        "void", "volatile", "while", "true", "false", "null"
    };

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
        { "int", "long", "short", "byte", "char", "boolean", "double", "float", "var" };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "static", "final", "abstract", "synchronized", "native", "transient", "volatile",
        "strictfp", "default", "sealed", "non-sealed"
    };

    private static readonly string[] MultiPunctuation =
    {
        ">>>=", ">>=", "<<=", "...", "->", "::", "++", "--", "&&", "||", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=",
        "%=", "&=", "|=", "^=", "<<"
    };

    public static JvFile Read(string text)
    {
        var (tokens, comments) = Tokenize(text);
        var file = new JvFile { Text = text, Tokens = tokens, Comments = comments };
        try
        {
            new Parser(file).TopLevel();
        }
        catch (JvReadException ex)
        {
            file.Problem = ex.Message;
        }
        return file;
    }

    public static bool IsKeyword(string word) => Keywords.Contains(word);

    // ---- tokens -------------------------------------------------------------------------------

    private static (List<JvToken>, List<(string, int)>) Tokenize(string text)
    {
        var tokens = new List<JvToken>();
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
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                var parts = text[i..end].Split('\n');
                for (int p = 0; p < parts.Length; p++) comments.Add((CleanComment(parts[p]), startLine + p));
                Lines(i, end);
                i = end;
                continue;
            }
            // A text block: """ to """.
            if (string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
            {
                int end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                while (end > 0 && text[end - 1] == '\\') end = text.IndexOf("\"\"\"", end + 1, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                tokens.Add(new JvToken(JvTokenKind.Text, text[i..end], i, end, startLine, column));
                Lines(i, end);
                i = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                int k = i + 1;
                while (k < text.Length && text[k] != c && text[k] != '\n') { if (text[k] == '\\') k++; k++; }
                int end = Math.Min(text.Length, k + 1);
                tokens.Add(new JvToken(JvTokenKind.Text, text[i..end], i, end, startLine, column));
                i = end;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int k = i + 1;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] is '_' or '.' ||
                       (text[k] is '+' or '-' && text[k - 1] is 'e' or 'E' or 'p' or 'P' && !text[i..k].StartsWith("0x"))))
                    k++;
                tokens.Add(new JvToken(JvTokenKind.Number, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int k = i + 1;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] is '_' or '$')) k++;
                // non-sealed is one modifier.
                if (text[i..k] == "non" && string.CompareOrdinal(text, k, "-sealed", 0, 7) == 0) k += 7;
                tokens.Add(new JvToken(JvTokenKind.Identifier, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }
            // '>' stands alone, so the two in List<List<Integer>> close two generics.
            string symbol = MultiPunctuation.FirstOrDefault(p => string.CompareOrdinal(text, i, p, 0, p.Length) == 0) ?? c.ToString();
            tokens.Add(new JvToken(JvTokenKind.Punctuation, symbol, i, i + symbol.Length, startLine, column));
            i += symbol.Length;
        }
        return (tokens, comments);
    }

    private static string CleanComment(string text)
    {
        string trimmed = text.TrimEnd('\r').Trim();
        trimmed = Regex.Replace(trimmed, @"^(///|//|/\*\*|/\*|\*/|\*)\s?", "");
        if (trimmed.EndsWith("*/")) trimmed = trimmed[..^2];
        return trimmed.TrimEnd();
    }

    /// <summary>Joins type tokens as a person writes them: List&lt;List&lt;Integer&gt;&gt;, Map&lt;Integer, String&gt;, int[].</summary>
    public static string TypeText(IReadOnlyList<JvToken> tokens, int from, int to)
    {
        var text = new StringBuilder();
        for (int k = from; k <= to; k++)
        {
            string t = tokens[k].Text;
            if (text.Length > 0)
            {
                char last = text[^1];
                bool word = tokens[k].Kind is JvTokenKind.Identifier or JvTokenKind.Number;
                if ((word && (char.IsLetterOrDigit(last) || last is '_' or '$')) || last == ',') text.Append(' ');
            }
            text.Append(t);
        }
        return text.ToString();
    }

    // ---- parsing -------------------------------------------------------------------------------

    private sealed class JvReadException : Exception
    {
        public JvReadException(string message) : base(message) { }
    }

    private sealed class Parser
    {
        private readonly JvFile _file;
        private readonly IReadOnlyList<JvToken> _t;

        public Parser(JvFile file)
        {
            _file = file;
            _t = file.Tokens;
        }

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";
        private bool IsPunct(int i, string s) => i >= 0 && i < _t.Count && _t[i].Kind == JvTokenKind.Punctuation && _t[i].Text == s;
        private bool IsName(int i) => i >= 0 && i < _t.Count && _t[i].Kind == JvTokenKind.Identifier && !Keywords.Contains(_t[i].Text);

        private JvReadException Problem(int i, string what)
            => new($"line {(i < _t.Count ? _t[i].Line : _t.Count > 0 ? _t[^1].Line : 1)}: {what}");

        public int Match(int open)
        {
            string o = T(open);
            string c = o switch { "(" => ")", "[" => "]", "{" => "}", _ => throw Problem(open, $"expected a bracket, found '{o}'") };
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                if (_t[i].Kind != JvTokenKind.Punctuation) continue;
                string s = _t[i].Text;
                if (s is "(" or "[" or "{") depth++;
                else if (s is ")" or "]" or "}" && --depth == 0)
                {
                    if (s != c) throw Problem(i, $"'{s}' does not match the '{o}' on line {_t[open].Line}");
                    return i;
                }
            }
            throw Problem(open, $"the '{o}' here is never closed");
        }

        /// <summary>The '&gt;' closing the generic arguments at <paramref name="open"/>; -1 when it is a comparison.</summary>
        private int MatchAngle(int open, int limit)
        {
            int depth = 0;
            for (int i = open; i < limit; i++)
            {
                string s = T(i);
                if (s == "<") depth++;
                else if (s == ">") { if (--depth == 0) return i; }
                else if (s is "(" or "[") i = Match(i);
                else if (s is ";" or "{" or "}" or "&&" or "||" or "=" or "->") return -1;
            }
            return -1;
        }

        private int SkipAnnotations(int i, int limit)
        {
            while (i < limit && T(i) == "@" && T(i + 1) != "interface")
            {
                i += 2;
                while (T(i) == "." && IsName(i + 1)) i += 2;
                if (T(i) == "(") i = Match(i) + 1;
            }
            return i;
        }

        private int SkipTo(int i, string what, int limit)
        {
            while (i < limit && T(i) != what)
            {
                if (_t[i].Kind == JvTokenKind.Punctuation && T(i) is "(" or "[" or "{") i = Match(i);
                i++;
            }
            return i;
        }

        // ---- types and members ----------------------------------------------------------------

        public void TopLevel()
        {
            int i = 0;
            while (i < _t.Count)
            {
                i = SkipAnnotations(i, _t.Count);
                if (i >= _t.Count) break;
                if (T(i) == "package")
                {
                    int end = SkipTo(i, ";", _t.Count);
                    _file.Package = (_t[i].Start, _t[Math.Min(end, _t.Count - 1)].End);
                    i = end + 1;
                    continue;
                }
                if (T(i) == "import") { i = SkipTo(i, ";", _t.Count) + 1; continue; }
                if (T(i) == ";") { i++; continue; }
                i = TypeDeclaration(i, _t.Count, topLevel: true) ?? SkipTo(i, ";", _t.Count) + 1;
            }
        }

        /// <summary>A class, interface, enum or record starting at <paramref name="i"/>; the index after it, or null if there is none.</summary>
        private int? TypeDeclaration(int i, int limit, bool topLevel)
        {
            bool isPublic = false;
            int k = i;
            while (k < limit && (Modifiers.Contains(T(k)) || T(k) == "@"))
            {
                if (T(k) == "@") { k = SkipAnnotations(k, limit); continue; }
                if (T(k) == "public") isPublic = true;
                k++;
            }
            string keyword = T(k);
            if (keyword == "@" && T(k + 1) == "interface") { k++; keyword = "interface"; }
            if (keyword is not ("class" or "interface" or "enum" or "record")) return null;

            var type = new JvClass { Name = T(k + 1), Line = _t[Math.Min(k + 1, _t.Count - 1)].Line, IsPublic = isPublic, TopLevel = topLevel };
            _file.Classes.Add(type);
            int open = k + 2;
            while (open < limit && T(open) != "{")
            {
                if (T(open) == "<") { int a = MatchAngle(open, limit); open = a < 0 ? open + 1 : a + 1; continue; }
                if (T(open) == "(")
                {
                    // A record's components are its fields.
                    int close = Match(open);
                    foreach (var p in Parameters(open, close)) type.Fields.Add(new JvVariable(p.Name, p.Type));
                    open = close + 1;
                    continue;
                }
                open++;
            }
            if (open >= limit) throw Problem(k, $"{keyword} {type.Name} has no body");
            int end = Match(open);
            Members(open + 1, end, type, keyword == "enum");
            return end + 1;
        }

        private void Members(int from, int to, JvClass owner, bool isEnum)
        {
            int i = from;
            if (isEnum)
            {
                // The constants come first, up to a ';'.
                while (i < to && T(i) != ";") { if (T(i) is "(" or "{") i = Match(i); i++; }
                i++;
            }
            while (i < to)
            {
                i = SkipAnnotations(i, to);
                if (i >= to) break;
                if (T(i) == ";") { i++; continue; }
                if (TypeDeclaration(i, to, topLevel: false) is int after) { i = after; continue; }

                bool isPublic = false, isStatic = false;
                int k = i;
                while (k < to && (Modifiers.Contains(T(k)) || T(k) == "@"))
                {
                    if (T(k) == "@") { k = SkipAnnotations(k, to); continue; }
                    if (T(k) == "public") isPublic = true;
                    if (T(k) == "static") isStatic = true;
                    k++;
                }
                if (T(k) == "{") { i = Match(k) + 1; continue; }                // an initializer block

                // Up to the first '(' , '=' , ';' or '{' outside generics.
                int typeStart = k;
                if (T(k) == "<") { int a = MatchAngle(k, to); k = a < 0 ? k + 1 : a + 1; typeStart = k; }
                int scan = k;
                while (scan < to && T(scan) is not "(" and not "=" and not ";" and not "{")
                {
                    if (T(scan) == "<") { int a = MatchAngle(scan, to); scan = a < 0 ? scan + 1 : a + 1; continue; }
                    if (T(scan) == "[") { scan = Match(scan) + 1; continue; }
                    scan++;
                }

                if (T(scan) == "(" && IsName(scan - 1))
                {
                    int nameAt = scan - 1;
                    int close = Match(scan);
                    var parameters = Parameters(scan, close);
                    int body = close + 1;
                    while (body < to && T(body) is not "{" and not ";") body++;
                    bool constructor = T(nameAt) == owner.Name && nameAt == typeStart;
                    if (constructor) owner.Constructors.Add((parameters.Count, isPublic));
                    if (T(body) == "{")
                    {
                        var block = Block(body);
                        _file.Methods.Add(new JvMethod
                        {
                            Name = T(nameAt),
                            Owner = owner.Name,
                            IsPublic = isPublic,
                            IsStatic = isStatic,
                            IsConstructor = constructor,
                            ReturnType = constructor ? "" : TypeText(_t, typeStart, nameAt - 1),
                            Parameters = parameters,
                            Line = _t[nameAt].Line,
                            NameToken = nameAt,
                            Body = block
                        });
                        i = block.Last + 1;
                    }
                    else i = body + 1;
                    continue;
                }

                if (T(scan) == "{" && T(scan - 1) == owner.Name)
                {
                    // A record's compact constructor: Name { ... }.
                    i = Match(scan) + 1;
                    continue;
                }

                // A field: Type a = ..., b;
                int end = SkipTo(k, ";", to);
                foreach (var v in Declaration(k, end) ?? new List<JvVariable>()) owner.Fields.Add(v);
                i = end + 1;
            }
        }

        private List<JvParameter> Parameters(int open, int close)
        {
            var parameters = new List<JvParameter>();
            foreach (var (from, to) in Split(open + 1, close))
            {
                int start = from;
                while (start <= to && (T(start) is "final" || T(start) == "@"))
                    start = T(start) == "@" ? SkipAnnotations(start, to + 1) : start + 1;
                if (start > to || !IsName(to) && !Primitives.Contains(T(to)) && T(to) != "this") continue;
                int nameAt = to;
                int dims = 0;
                while (T(nameAt) == "]") { nameAt -= 2; dims++; }        // int a[]
                string type = TypeText(_t, start, nameAt - 1).Replace("...", "[]") + string.Concat(Enumerable.Repeat("[]", dims));
                if (T(nameAt) != "this") parameters.Add(new JvParameter(T(nameAt), type));
            }
            return parameters;
        }

        private List<(int From, int To)> Split(int from, int end)
        {
            var parts = new List<(int, int)>();
            int start = from, angle = 0;
            for (int i = from; i <= end; i++)
            {
                if (i < end && _t[i].Kind == JvTokenKind.Punctuation && T(i) is "(" or "[" or "{") { i = Match(i); continue; }
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

        // ---- declarations ------------------------------------------------------------------------

        /// <summary>
        /// The variables a declaration declares — "int[] a = .., b[];" or "Map&lt;K, V&gt; m = .." —
        /// or null when the tokens are not a declaration.
        /// </summary>
        private List<JvVariable>? Declaration(int from, int to, bool forEach = false)
        {
            int i = from;
            while (i < to && (T(i) == "final" || T(i) == "@")) i = T(i) == "@" ? SkipAnnotations(i, to) : i + 1;
            int typeStart = i;
            if (!(IsName(i) || Primitives.Contains(T(i)))) return null;
            i++;
            while (i < to)
            {
                if (T(i) == "." && IsName(i + 1)) { i += 2; continue; }
                if (T(i) == "<")
                {
                    int close = MatchAngle(i, to);
                    if (close < 0) return null;
                    i = close + 1;
                    continue;
                }
                if (T(i) == "[" && T(i + 1) == "]") { i += 2; continue; }
                break;
            }
            int typeEnd = i - 1;
            if (!IsName(i)) return null;
            string type = TypeText(_t, typeStart, typeEnd);

            var declared = new List<JvVariable>();
            while (i < to && IsName(i))
            {
                string name = T(i);
                int dims = 0;
                i++;
                while (T(i) == "[" && T(i + 1) == "]") { dims++; i += 2; }
                string next = T(i);
                if (!(next is "=" or ";" or "," || (forEach && next == ":") || i >= to)) return declared.Count > 0 ? declared : null;
                declared.Add(new JvVariable(name, type + string.Concat(Enumerable.Repeat("[]", dims))));
                if (forEach) break;
                while (i < to && T(i) != ",")
                {
                    if (_t[i].Kind == JvTokenKind.Punctuation && T(i) is "(" or "[" or "{") i = Match(i);
                    else if (T(i) == "<" && MatchAngle(i, to) is int a && a > 0) i = a;
                    i++;
                }
                if (T(i) == ",") i++;
            }
            return declared.Count > 0 ? declared : null;
        }

        // ---- statements --------------------------------------------------------------------------

        public JvStatement Block(int open)
        {
            if (T(open) != "{") throw Problem(open, $"expected '{{', found '{T(open)}'");
            int close = Match(open);
            var block = new JvStatement { Kind = JvStatementKind.Block, First = open, Last = close };
            int i = open + 1;
            while (i < close) block.Children.Add(Statement(ref i, close));
            return block;
        }

        private JvStatement Unbraced(ref int i, int limit)
        {
            var statement = Statement(ref i, limit);
            statement.Unbraced = statement.Kind != JvStatementKind.Block;
            return statement;
        }

        private JvStatement Statement(ref int i, int limit)
        {
            int start = i;
            i = SkipAnnotations(i, limit);
            string s = T(i);

            if (s == ";") { i++; return new JvStatement { Kind = JvStatementKind.Empty, First = start, Last = start }; }
            if (s == "{")
            {
                var block = Block(i);
                block.First = start;
                i = block.Last + 1;
                return block;
            }
            // A label: outer: for (...)
            if (IsName(i) && T(i + 1) == ":" )
            {
                int at = i;
                i += 2;
                var inner = Statement(ref i, limit);
                var labeled = new JvStatement { Kind = JvStatementKind.Label, First = start, Last = inner.Last };
                labeled.Children.Add(inner);
                _ = at;
                return labeled;
            }

            switch (s)
            {
                case "if":
                {
                    int close = Match(i + 1);
                    var statement = new JvStatement { Kind = JvStatementKind.If, First = start, Last = close, HeaderEnd = close };
                    FindLambdas(statement.Lambdas, i + 2, close - 1, null);
                    i = close + 1;
                    statement.Children.Add(Unbraced(ref i, limit));
                    if (T(i) == "else")
                    {
                        i++;
                        statement.Else = Unbraced(ref i, limit);
                        statement.Last = statement.Else.Last;
                    }
                    else statement.Last = statement.Children[0].Last;
                    return statement;
                }
                case "for":
                {
                    int open = i + 1, close = Match(open);
                    int colon = TopLevel(open + 1, close, ":");
                    int semicolon = TopLevel(open + 1, close, ";");
                    var statement = new JvStatement
                    {
                        Kind = colon < close && semicolon >= close ? JvStatementKind.ForEach : JvStatementKind.For,
                        First = start, Last = close, HeaderEnd = close
                    };
                    if (statement.Kind == JvStatementKind.ForEach)
                        statement.BodyDeclares.AddRange(Declaration(open + 1, colon, forEach: true) ?? new List<JvVariable>());
                    else if (Declaration(open + 1, semicolon) is { } declared)
                        statement.BodyDeclares.AddRange(declared);
                    FindLambdas(statement.Lambdas, open + 1, close - 1, null);
                    i = close + 1;
                    var body = Unbraced(ref i, limit);
                    statement.Children.Add(body);
                    statement.Last = body.Last;
                    return statement;
                }
                case "while":
                {
                    int close = Match(i + 1);
                    var statement = new JvStatement { Kind = JvStatementKind.While, First = start, Last = close, HeaderEnd = close };
                    FindLambdas(statement.Lambdas, i + 2, close - 1, null);
                    i = close + 1;
                    var body = Unbraced(ref i, limit);
                    statement.Children.Add(body);
                    statement.Last = body.Last;
                    return statement;
                }
                case "do":
                {
                    i++;
                    var body = Unbraced(ref i, limit);
                    if (T(i) != "while") throw Problem(i, "expected 'while' after a do loop's body");
                    int close = Match(i + 1);
                    int semicolon = SkipTo(close, ";", limit);
                    var statement = new JvStatement { Kind = JvStatementKind.Do, First = start, Last = semicolon, HeaderEnd = -1 };
                    statement.Children.Add(body);
                    i = semicolon + 1;
                    return statement;
                }
                case "switch":
                {
                    int close = Match(i + 1);
                    if (T(close + 1) != "{") throw Problem(i, "expected '{' after switch (...)");
                    var statement = SwitchBody(start, close + 1);
                    statement.HeaderEnd = close;
                    FindLambdas(statement.Lambdas, i + 2, close - 1, null);
                    i = statement.Last + 1;
                    if (T(i) == ";") i++;
                    return statement;
                }
                case "try":
                {
                    var statement = new JvStatement { Kind = JvStatementKind.Try, First = start, Last = i };
                    int k = i + 1;
                    if (T(k) == "(")
                    {
                        int close = Match(k);
                        foreach (var (from, to) in SplitOn(k + 1, close, ";"))
                            if (Declaration(from, to + 1) is { } resources) statement.BodyDeclares.AddRange(resources);
                        k = close + 1;
                    }
                    var body = Block(k);
                    statement.Children.Add(body);
                    k = body.Last + 1;
                    while (T(k) == "catch")
                    {
                        int close = Match(k + 1);
                        if (IsName(close - 1))
                            statement.CatchParameters[statement.Children.Count] = new JvVariable(T(close - 1), TypeText(_t, k + 2, close - 2));
                        var handler = Block(close + 1);
                        statement.Children.Add(handler);
                        k = handler.Last + 1;
                    }
                    if (T(k) == "finally")
                    {
                        var final = Block(k + 1);
                        statement.Children.Add(final);
                        k = final.Last + 1;
                    }
                    statement.Last = k - 1;
                    i = k;
                    return statement;
                }
                case "synchronized" when T(i + 1) == "(":
                {
                    int close = Match(i + 1);
                    var body = Block(close + 1);
                    var statement = new JvStatement { Kind = JvStatementKind.Synchronized, First = start, Last = body.Last, HeaderEnd = close };
                    statement.Children.Add(body);
                    i = body.Last + 1;
                    return statement;
                }
                case "class":
                case "interface":
                case "enum":
                case "record" when IsName(i + 1) && T(i + 2) is "(" or "<":
                case "abstract":
                case "static":
                {
                    int? after = TypeDeclaration(i, limit, topLevel: false);
                    if (after is int a)
                    {
                        i = a;
                        return new JvStatement { Kind = JvStatementKind.Item, First = start, Last = a - 1 };
                    }
                    break;
                }
                case "final" when T(i + 1) is "class" or "record":
                {
                    int? after = TypeDeclaration(i, limit, topLevel: false);
                    if (after is int a) { i = a; return new JvStatement { Kind = JvStatementKind.Item, First = start, Last = a - 1 }; }
                    break;
                }
            }

            int end = SkipTo(i, ";", limit);
            if (end >= limit) throw Problem(start, "a statement here is missing its ';'");
            var kind = s == "return" ? JvStatementKind.Return : JvStatementKind.Expression;
            var simple = new JvStatement { Kind = kind, First = start, Last = end };
            if (kind == JvStatementKind.Expression && s is not ("throw" or "break" or "continue" or "yield" or "assert") &&
                Declaration(i, end) is { } names)
            {
                simple = new JvStatement { Kind = JvStatementKind.Local, First = start, Last = end };
                simple.Declares.AddRange(names);
            }
            FindLambdas(simple.Lambdas, i, end - 1, simple.Declares.Count == 1 ? simple.Declares[0].Name : null);
            i = end + 1;
            return simple;
        }

        private List<(int From, int To)> SplitOn(int from, int end, string separator)
        {
            var parts = new List<(int, int)>();
            int start = from;
            for (int i = from; i <= end; i++)
            {
                if (i < end && _t[i].Kind == JvTokenKind.Punctuation && T(i) is "(" or "[" or "{") { i = Match(i); continue; }
                if (i == end || T(i) == separator)
                {
                    if (i > start) parts.Add((start, i - 1));
                    start = i + 1;
                }
            }
            return parts;
        }

        private int TopLevel(int from, int to, string what)
        {
            for (int k = from; k < to; k++)
            {
                if (_t[k].Kind == JvTokenKind.Punctuation && T(k) is "(" or "[" or "{") { k = Match(k); continue; }
                if (T(k) == what) return k;
            }
            return to;
        }

        /// <summary>
        /// A switch statement's body: in the colon form, its statements in order with the case
        /// labels between them; in the arrow form, each rule's body.
        /// </summary>
        private JvStatement SwitchBody(int start, int open)
        {
            int close = Match(open);
            var statement = new JvStatement { Kind = JvStatementKind.Switch, First = start, Last = close };
            int i = open + 1;
            while (i < close)
            {
                if (T(i) is "case" or "default")
                {
                    int labelStart = i;
                    int k = i + 1;
                    while (k < close && T(k) is not ":" and not "->")
                    {
                        if (_t[k].Kind == JvTokenKind.Punctuation && T(k) is "(" or "[" or "{") k = Match(k);
                        k++;
                    }
                    statement.Children.Add(new JvStatement { Kind = JvStatementKind.Label, First = labelStart, Last = k });
                    i = k + 1;
                    if (T(k) == "->")
                    {
                        // An arrow rule's body: a block, a throw, or one expression.
                        var body = T(i) == "{" ? Block(i) : Statement(ref i, close);
                        if (body.Kind == JvStatementKind.Block) i = body.Last + 1;
                        else body.Unbraced = true;
                        statement.Children.Add(body);
                    }
                    continue;
                }
                statement.Children.Add(Statement(ref i, close));
            }
            return statement;
        }

        // ---- lambdas -----------------------------------------------------------------------------

        private void FindLambdas(List<JvLambda> into, int from, int to, string? assignedTo)
        {
            for (int k = from; k <= to && k < _t.Count; k++)
            {
                // An anonymous class's body is its own: its methods are not this statement's.
                if (T(k) == "{" && _t[k].Kind == JvTokenKind.Punctuation && T(k - 1) == ")" && AnonymousClass(k))
                {
                    k = Match(k);
                    continue;
                }
                if (T(k) != "->" || SwitchRule(k)) continue;

                var parameters = new List<JvVariable>();
                if (T(k - 1) == ")")
                {
                    int open = k - 1;
                    for (int depth = 0; open >= 0; open--)
                    {
                        if (T(open) == ")") depth++;
                        else if (T(open) == "(" && --depth == 0) break;
                    }
                    foreach (var (pf, pt) in Split(open + 1, k - 1))
                        if (IsName(pt)) parameters.Add(new JvVariable(T(pt), pt > pf ? TypeText(_t, pf, pt - 1) : "var"));
                }
                else if (IsName(k - 1)) parameters.Add(new JvVariable(T(k - 1), "var"));

                JvStatement? body = T(k + 1) == "{" ? Block(k + 1) : null;
                int last = body?.Last ?? ExpressionEnd(k + 1, to + 1);
                var mentioned = new HashSet<string>(StringComparer.Ordinal);
                for (int m = k + 1; m <= last; m++) if (IsName(m)) mentioned.Add(T(m));
                into.Add(new JvLambda { Arrow = k, Parameters = parameters, Body = body, BodyLast = last, Mentioned = mentioned, Name = assignedTo });
                assignedTo = null;
                k = last;
            }
        }

        /// <summary>The '{' at <paramref name="k"/>, after a ')', opens the body of `new Type(...) { }`.</summary>
        private bool AnonymousClass(int k)
        {
            int open = k - 1;
            for (int depth = 0; open >= 0; open--)
            {
                if (T(open) == ")") depth++;
                else if (T(open) == "(" && --depth == 0) break;
            }
            int p = open - 1;
            if (T(p) == ">") { int depth = 0; for (; p >= 0; p--) { if (T(p) == ">") depth++; else if (T(p) == "<" && --depth == 0) break; } p--; }
            while (IsName(p) && T(p - 1) == ".") p -= 2;
            return IsName(p) && T(p - 1) == "new";
        }

        /// <summary>Whether the '->' at <paramref name="k"/> belongs to a switch rule — case A, B -> — rather than a lambda.</summary>
        private bool SwitchRule(int k)
        {
            int p = k - 1;
            if (T(p) == "default") return true;
            while (p >= 0 && (_t[p].Kind is JvTokenKind.Identifier or JvTokenKind.Number or JvTokenKind.Text || T(p) is "." or "," or "-"))
            {
                if (T(p) == "case") return true;
                if (_t[p].Kind == JvTokenKind.Identifier && Keywords.Contains(T(p)) && T(p) is not ("true" or "false" or "null")) return false;
                p--;
            }
            return T(p) == "case";
        }

        private int ExpressionEnd(int from, int limit)
        {
            for (int e = from; e < limit; e++)
            {
                if (_t[e].Kind == JvTokenKind.Punctuation && T(e) is "(" or "[" or "{") { e = Match(e); continue; }
                if (T(e) is "," or ";" or ")" or "]" or "}") return e - 1;
            }
            return limit - 1;
        }
    }
}
