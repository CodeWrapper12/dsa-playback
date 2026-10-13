using System.Text;
using System.Text.RegularExpressions;

namespace DsaViz.Engine.Cpp;

public enum TokenKind { Identifier, Number, Text, Punctuation }

/// <summary>One token of C++ source: where it starts and ends in the text, and its line.</summary>
public sealed record CppToken(TokenKind Kind, string Text, int Start, int End, int Line, int Column);

public enum StatementKind { Block, Simple, Return, If, For, RangeFor, While, Do, Switch, Try, Label, Empty }

/// <summary>A variable a statement declares, with the type as written ("" for auto).</summary>
public sealed record CppVariable(string Name, string Type);

/// <summary>
/// One statement in a function body. Control statements hold their parts; a simple
/// statement may hold lambdas, whose bodies are statements too.
/// </summary>
public sealed class CppStatement
{
    public required StatementKind Kind { get; init; }
    /// <summary>Token index of the statement's first token.</summary>
    public required int First { get; init; }
    /// <summary>Token index of its last token: the ';' or the closing '}'.</summary>
    public required int Last { get; init; }
    public List<CppStatement> Children { get; } = new();
    /// <summary>For if, the else branch; for a loop, its body is the only child.</summary>
    public CppStatement? Else { get; set; }
    /// <summary>Variables this statement declares, visible to the statements after it.</summary>
    public List<CppVariable> Declares { get; } = new();
    /// <summary>Variables a for-loop header declares, visible in its body only.</summary>
    public List<CppVariable> HeaderDeclares { get; } = new();
    public List<CppLambda> Lambdas { get; } = new();
}

/// <summary>A lambda inside a statement: what it captures and its own body.</summary>
public sealed class CppLambda
{
    /// <summary>Token index of the '[' that opens it.</summary>
    public required int Open { get; init; }
    public required int BodyOpen { get; init; }
    public required int BodyClose { get; init; }
    /// <summary>Captures everything by reference or by copy: [&amp;] or [=].</summary>
    public bool CapturesAll { get; init; }
    public IReadOnlyList<string> Captured { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CppVariable> Parameters { get; init; } = Array.Empty<CppVariable>();
    /// <summary>The name of the variable it is assigned to, when it has one: "dfs".</summary>
    public string? Name { get; set; }
    public string? ReturnType { get; init; }
    public CppStatement Body { get; set; } = null!;
}

public sealed record CppParameter(string Name, string Type);

public sealed class CppFunction
{
    public required string Name { get; init; }
    /// <summary>The class it is a member of; null for a free function.</summary>
    public string? Owner { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }
    public bool IsConstructor { get; init; }
    public required string ReturnType { get; init; }
    public required IReadOnlyList<CppParameter> Parameters { get; init; }
    /// <summary>1-based line of the function's name.</summary>
    public int Line { get; init; }
    public required CppStatement Body { get; init; }
}

public sealed class CppClass
{
    public required string Name { get; init; }
    public int Line { get; init; }
    public List<(string Name, string Type)> Fields { get; } = new();
    /// <summary>Parameter counts of its public constructors; empty when it declares none.</summary>
    public List<int> PublicConstructors { get; } = new();
    public bool HasConstructors { get; set; }
    public bool Constructible => !HasConstructors || PublicConstructors.Contains(0);
}

/// <summary>The file as read: its tokens, comments, classes and functions.</summary>
public sealed class CppFile
{
    public required string Text { get; init; }
    public required IReadOnlyList<CppToken> Tokens { get; init; }
    public required IReadOnlyList<(string Text, int Line)> Comments { get; init; }
    public List<CppClass> Classes { get; } = new();
    public List<CppFunction> Functions { get; } = new();
    /// <summary>Why the file could not be read, when it could not: a sentence with a line.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// Reads the C++ that LeetCode solutions are written in: classes and free functions,
/// statements, the variables each declares, and lambdas. Not a compiler — expressions
/// are not parsed, only their extent — so it needs nothing installed and never runs code.
/// The student's compiler is the authority on whether the code is valid; this only has
/// to find where statements are and what is in scope.
/// </summary>
public static class CppReader
{
    /// <summary>The type given to a variable-length array, which is never captured.</summary>
    public const string VariableLengthArray = "?vla";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue", "goto",
        "try", "catch", "throw", "new", "delete", "sizeof", "using", "typedef", "namespace", "template", "typename",
        "class", "struct", "union", "enum", "public", "private", "protected", "operator", "this", "true", "false",
        "nullptr", "static_assert", "co_return", "co_await", "co_yield", "and", "or", "not"
    };

    private static readonly HashSet<string> Specifiers = new(StringComparer.Ordinal)
    {
        "static", "const", "constexpr", "inline", "register", "volatile", "mutable", "thread_local", "extern",
        "virtual", "explicit", "friend", "consteval", "constinit"
    };

    private static readonly string[] MultiPunctuation =
    {
        "<<=", "...", "->*", "::", "->", "++", "--", "&&", "||", "==", "!=", "<=", "<<", "+=", "-=", "*=", "/=",
        "%=", "&=", "|=", "^=", ">=", ".*"
    };

    public static CppFile Read(string text)
    {
        var (tokens, comments) = Tokenize(text);
        var file = new CppFile { Text = text, Tokens = tokens, Comments = comments };
        try
        {
            new Parser(file).TopLevel();
        }
        catch (CppReadException ex)
        {
            file.Problem = ex.Message;
        }
        return file;
    }

    // ---- tokens -----------------------------------------------------------------------------

    private static (List<CppToken>, List<(string, int)>) Tokenize(string text)
    {
        var tokens = new List<CppToken>();
        var comments = new List<(string, int)>();
        int line = 1, lineStart = 0, i = 0;
        bool atLineStart = true;

        void NewLines(int from, int to)
        {
            for (int k = from; k < to; k++)
                if (text[k] == '\n') { line++; lineStart = k + 1; }
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { line++; lineStart = i + 1; i++; atLineStart = true; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            int start = i, startLine = line, column = i - lineStart + 1;

            // Preprocessor lines are skipped whole, with their continuations.
            if (c == '#' && atLineStart)
            {
                while (i < text.Length && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\n') { i += 2; line++; lineStart = i; continue; }
                    i++;
                }
                continue;
            }
            atLineStart = false;

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
                for (int k = 0; k < parts.Length; k++) comments.Add((CleanComment(parts[k]), startLine + k));
                NewLines(i, end);
                i = end;
                continue;
            }

            // Raw strings: R"delimiter( ... )delimiter", with any prefix.
            var raw = Regex.Match(text[i..Math.Min(text.Length, i + 24)], @"^(?:u8|u|U|L)?R""([^ ()\\]{0,16})\(");
            if (raw.Success)
            {
                string close = ")" + raw.Groups[1].Value + "\"";
                int end = text.IndexOf(close, i + raw.Length, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + close.Length;
                tokens.Add(new CppToken(TokenKind.Text, text[i..end], i, end, startLine, column));
                NewLines(i, end);
                i = end;
                continue;
            }

            if (c is '"' or '\'' || (IsPrefix(text, i) is int prefix && prefix > 0 && i + prefix < text.Length && text[i + prefix] is '"' or '\''))
            {
                int q = i + (c is '"' or '\'' ? 0 : IsPrefix(text, i));
                char quote = text[q];
                int k = q + 1;
                while (k < text.Length && text[k] != quote && text[k] != '\n')
                {
                    if (text[k] == '\\') k++;
                    k++;
                }
                int end = Math.Min(text.Length, k + 1);
                tokens.Add(new CppToken(TokenKind.Text, text[i..end], i, end, startLine, column));
                i = end;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int k = i + 1;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] is '.' or '\'' or '_' ||
                                           (text[k] is '+' or '-' && text[k - 1] is 'e' or 'E' or 'p' or 'P')))
                    k++;
                tokens.Add(new CppToken(TokenKind.Number, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int k = i + 1;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                tokens.Add(new CppToken(TokenKind.Identifier, text[i..k], i, k, startLine, column));
                i = k;
                continue;
            }

            // '>' always stands alone, so the two in vector<vector<int>> close two templates.
            string symbol = MultiPunctuation.FirstOrDefault(p => string.CompareOrdinal(text, i, p, 0, p.Length) == 0) ?? c.ToString();
            tokens.Add(new CppToken(TokenKind.Punctuation, symbol, i, i + symbol.Length, startLine, column));
            i += symbol.Length;
        }
        return (tokens, comments);
    }

    private static int IsPrefix(string text, int i)
    {
        foreach (var p in new[] { "u8", "u", "U", "L" })
            if (string.CompareOrdinal(text, i, p, 0, p.Length) == 0) return p.Length;
        return 0;
    }

    private static string CleanComment(string text)
    {
        string trimmed = text.TrimEnd('\r').Trim();
        trimmed = Regex.Replace(trimmed, @"^(///|//|/\*\*|/\*|\*/|\*)\s?", "");
        if (trimmed.EndsWith("*/")) trimmed = trimmed[..^2];
        return trimmed.TrimEnd();
    }

    /// <summary>Joins type tokens as a person writes them: vector&lt;vector&lt;int&gt;&gt;, unordered_map&lt;int, int&gt;.</summary>
    public static string TypeText(IReadOnlyList<CppToken> tokens, int from, int to)
    {
        var text = new StringBuilder();
        for (int k = from; k <= to; k++)
        {
            string t = tokens[k].Text;
            if (text.Length > 0)
            {
                char last = text[^1];
                bool word = tokens[k].Kind is TokenKind.Identifier or TokenKind.Number;
                bool lastWord = char.IsLetterOrDigit(last) || last == '_';
                if ((word && lastWord) || last == ',') text.Append(' ');
            }
            text.Append(t);
        }
        return text.ToString();
    }

    // ---- parsing -----------------------------------------------------------------------------

    private sealed class CppReadException : Exception
    {
        public CppReadException(string message) : base(message) { }
    }

    private sealed class Parser
    {
        private readonly CppFile _file;
        private readonly IReadOnlyList<CppToken> _t;

        public Parser(CppFile file)
        {
            _file = file;
            _t = file.Tokens;
        }

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";
        private bool IsIdentifier(int i) => i >= 0 && i < _t.Count && _t[i].Kind == TokenKind.Identifier && !Keywords.Contains(_t[i].Text);

        private CppReadException Problem(int i, string what)
            => new($"line {(i < _t.Count ? _t[i].Line : _t.Count > 0 ? _t[^1].Line : 1)}: {what}");

        /// <summary>The index of the bracket that closes the one at <paramref name="open"/>.</summary>
        private int Match(int open)
        {
            string o = T(open);
            string c = o switch { "(" => ")", "[" => "]", "{" => "}", _ => throw Problem(open, $"expected a bracket, found '{o}'") };
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                string s = T(i);
                if (s is "(" or "[" or "{") depth++;
                else if (s is ")" or "]" or "}") { depth--; if (depth == 0) { if (s != c) throw Problem(i, $"'{s}' does not match the '{o}' on line {_t[open].Line}"); return i; } }
            }
            throw Problem(open, $"the '{o}' here is never closed");
        }

        /// <summary>The '&gt;' that closes the '&lt;' at <paramref name="open"/>, counting nested brackets.</summary>
        private int MatchAngle(int open)
        {
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                string s = T(i);
                if (s == "<") depth++;
                else if (s == ">") { if (--depth == 0) return i; }
                else if (s is "(" or "[" or "{") i = Match(i);
                else if (s is ";" or "{" or "}") return -1;
            }
            return -1;
        }

        // ---- top level: namespaces, classes, functions ----------------------------------------

        public void TopLevel() => Declarations(0, _t.Count, owner: null, isStruct: true);

        private void Declarations(int from, int to, CppClass? owner, bool isStruct)
        {
            bool isPublic = isStruct;
            int i = from;
            while (i < to)
            {
                string s = T(i);
                if (s == ";") { i++; continue; }
                if (s is "public" or "private" or "protected" && T(i + 1) == ":") { isPublic = s == "public"; i += 2; continue; }
                if (s == "namespace")
                {
                    int open = i + 1;
                    while (open < to && T(open) is not "{" and not ";") open++;
                    if (T(open) == "{") { int close = Match(open); Declarations(open + 1, close, null, true); i = close + 1; }
                    else i = open + 1;
                    continue;
                }
                if (s == "template" && T(i + 1) == "<")
                {
                    int close = MatchAngle(i + 1);
                    i = close < 0 ? i + 2 : close + 1;
                    continue;
                }
                if (s is "using" or "typedef" or "friend" or "static_assert") { i = SkipTo(i, ";", to) + 1; continue; }
                if (s is "class" or "struct" or "union" && IsIdentifier(i + 1) && ClassBody(i) is int classOpen)
                {
                    var type = new CppClass { Name = T(i + 1), Line = _t[i + 1].Line };
                    _file.Classes.Add(type);
                    int close = Match(classOpen);
                    Declarations(classOpen + 1, close, type, s != "class");
                    i = close + 1;
                    continue;
                }
                if (s == "enum")
                {
                    i = SkipTo(i, ";", to) + 1;
                    continue;
                }

                // A function definition, a declaration, or a field.
                int end = i;
                int brace = -1;
                while (end < to && T(end) is not ";" and not "{")
                {
                    if (T(end) is "(" or "[") end = Match(end);
                    end++;
                }
                if (end < to && T(end) == "{") brace = end;

                if (brace >= 0 && FunctionHeader(i, brace, owner) is { } header)
                {
                    int close = Match(brace);
                    var body = Block(brace);
                    _file.Functions.Add(new CppFunction
                    {
                        Name = header.Name,
                        Owner = owner?.Name,
                        IsPublic = owner is null || isPublic,
                        IsStatic = header.IsStatic,
                        IsConstructor = header.IsConstructor,
                        ReturnType = header.ReturnType,
                        Parameters = header.Parameters,
                        Line = header.Line,
                        Body = body
                    });
                    if (header.IsConstructor && owner is not null)
                    {
                        owner.HasConstructors = true;
                        if (isPublic) owner.PublicConstructors.Add(header.Parameters.Count);
                    }
                    i = close + 1;
                    continue;
                }

                if (brace >= 0)
                {
                    // An initializer in braces — int x{5}; — or something not understood: skipped whole.
                    int close = Match(brace);
                    i = SkipTo(close, ";", to) + 1;
                    continue;
                }

                if (owner is not null) Field(i, end, owner, isPublic);
                i = end + 1;
            }
        }

        private int SkipTo(int i, string what, int to)
        {
            while (i < to && T(i) != what)
            {
                if (T(i) is "(" or "[" or "{") i = Match(i);
                i++;
            }
            return i;
        }

        /// <summary>The '{' opening a class body that starts at <paramref name="keyword"/>, or null for a declaration only.</summary>
        private int? ClassBody(int keyword)
        {
            for (int i = keyword + 2; i < _t.Count; i++)
            {
                if (T(i) == "{") return i;
                if (T(i) is ";" or "(" or "=") return null;
            }
            return null;
        }

        private void Field(int from, int end, CppClass owner, bool isPublic)
        {
            // A constructor declared without a body: still a constructor.
            if (T(from) == owner.Name && T(from + 1) == "(")
            {
                owner.HasConstructors = true;
                if (isPublic) owner.PublicConstructors.Add(CountParameters(from + 1));
                return;
            }
            if (Declaration(from, end) is { } declared)
                foreach (var v in declared) owner.Fields.Add((v.Name, v.Type));
        }

        private int CountParameters(int open)
        {
            int close = Match(open);
            if (close == open + 1 || (close == open + 2 && T(open + 1) == "void")) return 0;
            int count = 1;
            for (int i = open + 1; i < close; i++)
            {
                if (T(i) is "(" or "[" or "{") i = Match(i);
                else if (T(i) == "<" && MatchAngle(i) is int a && a > 0) i = a;
                else if (T(i) == ",") count++;
            }
            return count;
        }

        private sealed record Header(string Name, string ReturnType, IReadOnlyList<CppParameter> Parameters, int Line, bool IsStatic, bool IsConstructor);

        /// <summary>
        /// Reads "int Solution::twoSum(vector&lt;int&gt;&amp; nums, int target) const" and the like:
        /// everything from <paramref name="from"/> up to the body's '{'. Null when it is not a function.
        /// </summary>
        private Header? FunctionHeader(int from, int brace, CppClass? owner)
        {
            // The parameter list is the last top-level (...) whose ')' is followed only by
            // qualifiers, a trailing return type, or a constructor's initializer list.
            int open = -1;
            for (int i = from; i < brace; i++)
            {
                if (T(i) == "(")
                {
                    if (IsNameBefore(i)) open = i;
                    i = Match(i);
                }
                else if (T(i) == ":" && open >= 0) break;     // a constructor's initializer list follows
                else if (T(i) == "=") return null;             // an initializer, not a function
            }
            if (open < 0) return null;

            int nameAt = open - 1;
            string name = T(nameAt);
            if (T(nameAt - 1) == "operator") return null;     // operators are not offered or stepped
            if (name.StartsWith('~')) return null;
            if (T(nameAt - 1) == "~") return null;

            // Qualified definitions outside the class: int Solution::twoSum(...)
            string? qualifiedOwner = T(nameAt - 1) == "::" && IsIdentifier(nameAt - 2) ? T(nameAt - 2) : null;
            int typeEnd = qualifiedOwner is null ? nameAt - 1 : nameAt - 3;

            bool isStatic = false;
            int typeStart = from;
            while (typeStart <= typeEnd && Specifiers.Contains(T(typeStart)))
            {
                if (T(typeStart) == "static") isStatic = true;
                typeStart++;
            }

            string ownerName = owner?.Name ?? qualifiedOwner ?? "";
            bool constructor = name == ownerName && typeStart > typeEnd;
            if (!constructor && typeStart > typeEnd) return null;          // a call, not a definition
            string returnType = constructor ? "" : TypeText(_t, typeStart, typeEnd);

            // A trailing return type: auto f(...) -> vector<int>
            int afterParameters = Match(open) + 1;
            for (int i = afterParameters; i < brace; i++)
                if (T(i) == "->") { returnType = TypeText(_t, i + 1, brace - 1); break; }

            return new Header(name, returnType, Parameters(open), _t[nameAt].Line, isStatic, constructor);
        }

        private bool IsNameBefore(int open) => open > 0 && _t[open - 1].Kind == TokenKind.Identifier && !Keywords.Contains(T(open - 1));

        private List<CppParameter> Parameters(int open)
        {
            int close = Match(open);
            var parameters = new List<CppParameter>();
            int start = open + 1;
            for (int i = open + 1; i <= close; i++)
            {
                if (i < close && T(i) is "(" or "[" or "{") { i = Match(i); continue; }
                if (i < close && T(i) == "<" && MatchAngle(i) is int a && a > 0) { i = a; continue; }
                if (i == close || T(i) == ",")
                {
                    int end = i - 1;
                    // A default value: int k = 0
                    for (int k = start; k <= end; k++) if (T(k) == "=") { end = k - 1; break; }
                    if (end >= start && !(end == start && T(start) == "void"))
                    {
                        int nameAt = end;
                        while (nameAt > start && T(nameAt) == "]") nameAt = BackToOpen(nameAt) - 1;
                        string name = IsIdentifier(nameAt) && nameAt > start ? T(nameAt) : $"arg{parameters.Count}";
                        int typeEnd = IsIdentifier(nameAt) && nameAt > start ? nameAt - 1 : end;
                        parameters.Add(new CppParameter(name, TypeText(_t, start, typeEnd)));
                    }
                    start = i + 1;
                }
            }
            return parameters;
        }

        private int BackToOpen(int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (T(i) is ")" or "]" or "}") depth++;
                else if (T(i) is "(" or "[" or "{") { if (--depth == 0) return i; }
            }
            return 0;
        }

        // ---- statements ---------------------------------------------------------------------------

        private CppStatement Block(int open)
        {
            int close = Match(open);
            var block = new CppStatement { Kind = StatementKind.Block, First = open, Last = close };
            int i = open + 1;
            while (i < close) block.Children.Add(Statement(ref i, close));
            return block;
        }

        private CppStatement Statement(ref int i, int limit)
        {
            int start = i;
            string s = T(i);

            switch (s)
            {
                case "{":
                {
                    var block = Block(i);
                    i = block.Last + 1;
                    return block;
                }
                case ";":
                    i++;
                    return new CppStatement { Kind = StatementKind.Empty, First = start, Last = start };
                case "if":
                {
                    int open = i + 1;
                    if (T(open) == "constexpr") open++;
                    if (T(open) != "(") throw Problem(i, "expected '(' after if");
                    int close = Match(open);
                    var statement = new CppStatement { Kind = StatementKind.If, First = start, Last = close };
                    FindLambdas(statement, open + 1, close - 1, Array.Empty<CppVariable>());
                    i = close + 1;
                    var then = Statement(ref i, limit);
                    statement.Children.Add(then);
                    int last = then.Last;
                    if (T(i) == "else")
                    {
                        i++;
                        statement.Else = Statement(ref i, limit);
                        last = statement.Else.Last;
                    }
                    return Copy(statement, last);
                }
                case "for":
                {
                    int open = i + 1;
                    if (T(open) != "(") throw Problem(i, "expected '(' after for");
                    int close = Match(open);
                    int colon = TopLevel(open + 1, close, ":");
                    int semicolon = TopLevel(open + 1, close, ";");
                    var kind = colon >= 0 && semicolon < 0 ? StatementKind.RangeFor : StatementKind.For;
                    var statement = new CppStatement { Kind = kind, First = start, Last = close };
                    if (kind == StatementKind.RangeFor)
                    {
                        if (Declaration(open + 1, colon, rangeFor: true) is { } declared) statement.HeaderDeclares.AddRange(declared);
                        FindLambdas(statement, colon + 1, close - 1, Array.Empty<CppVariable>());
                    }
                    else if (semicolon >= 0)
                    {
                        if (Declaration(open + 1, semicolon) is { } declared) statement.HeaderDeclares.AddRange(declared);
                        FindLambdas(statement, open + 1, close - 1, Array.Empty<CppVariable>());
                    }
                    i = close + 1;
                    var body = Statement(ref i, limit);
                    statement.Children.Add(body);
                    return Copy(statement, body.Last);
                }
                case "while":
                {
                    int open = i + 1;
                    int close = Match(open);
                    var statement = new CppStatement { Kind = StatementKind.While, First = start, Last = close };
                    FindLambdas(statement, open + 1, close - 1, Array.Empty<CppVariable>());
                    i = close + 1;
                    var body = Statement(ref i, limit);
                    statement.Children.Add(body);
                    return Copy(statement, body.Last);
                }
                case "do":
                {
                    i++;
                    var body = Statement(ref i, limit);
                    if (T(i) != "while") throw Problem(i, "expected 'while' after a do loop's body");
                    int close = Match(i + 1);
                    int end = SkipTo(close, ";", limit);
                    var statement = new CppStatement { Kind = StatementKind.Do, First = start, Last = end };
                    statement.Children.Add(body);
                    i = end + 1;
                    return statement;
                }
                case "switch":
                {
                    int close = Match(i + 1);
                    if (T(close + 1) != "{") throw Problem(i, "expected '{' after switch (...)");
                    var body = Block(close + 1);
                    var statement = new CppStatement { Kind = StatementKind.Switch, First = start, Last = body.Last };
                    statement.Children.Add(body);
                    i = body.Last + 1;
                    return statement;
                }
                case "try":
                {
                    var body = Block(i + 1);
                    var statement = new CppStatement { Kind = StatementKind.Try, First = start, Last = body.Last };
                    statement.Children.Add(body);
                    i = body.Last + 1;
                    while (T(i) == "catch")
                    {
                        int close = Match(i + 1);
                        var handler = Block(close + 1);
                        statement.Children.Add(handler);
                        i = handler.Last + 1;
                    }
                    return Copy(statement, i - 1);
                }
                case "case":
                case "default":
                {
                    int colon = i + 1;
                    while (colon < limit && T(colon) != ":") colon++;
                    i = colon + 1;
                    return new CppStatement { Kind = StatementKind.Label, First = start, Last = colon };
                }
            }

            // A goto label: name:
            if (IsIdentifier(i) && T(i + 1) == ":" && T(i + 2) != ":")
            {
                i += 2;
                return new CppStatement { Kind = StatementKind.Label, First = start, Last = start + 1 };
            }

            int semicolonAt = SkipTo(i, ";", limit);
            if (semicolonAt >= limit) throw Problem(start, "a statement here is missing its ';'");
            var simple = new CppStatement
            {
                Kind = s == "return" ? StatementKind.Return : StatementKind.Simple,
                First = start,
                Last = semicolonAt
            };
            if (s != "return" && Declaration(start, semicolonAt) is { } names) simple.Declares.AddRange(names);
            FindLambdas(simple, start, semicolonAt - 1, simple.Declares);
            i = semicolonAt + 1;
            return simple;
        }

        private static CppStatement Copy(CppStatement statement, int last)
        {
            var copy = new CppStatement { Kind = statement.Kind, First = statement.First, Last = last, Else = statement.Else };
            copy.Children.AddRange(statement.Children);
            copy.Declares.AddRange(statement.Declares);
            copy.HeaderDeclares.AddRange(statement.HeaderDeclares);
            copy.Lambdas.AddRange(statement.Lambdas);
            return copy;
        }

        /// <summary>The first <paramref name="what"/> between the brackets, outside any nested ones; -1 if none.</summary>
        private int TopLevel(int from, int to, string what)
        {
            for (int i = from; i < to; i++)
            {
                if (T(i) is "(" or "[" or "{") { i = Match(i); continue; }
                if (T(i) == what) return i;
            }
            return -1;
        }

        // ---- declarations ---------------------------------------------------------------------------

        private static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
        {
            "int", "long", "short", "char", "bool", "double", "float", "void", "unsigned", "signed", "auto", "size_t",
            "wchar_t", "char16_t", "char32_t", "int64_t", "int32_t", "uint64_t", "uint32_t"
        };

        /// <summary>
        /// The variables a statement declares, when it is a declaration: "vector&lt;int&gt; seen(n), other;"
        /// declares seen and other. Null for anything else — an assignment, a call, an expression.
        /// </summary>
        private List<CppVariable>? Declaration(int from, int to, bool rangeFor = false)
        {
            int i = from;
            while (i < to && Specifiers.Contains(T(i))) i++;
            int typeStart = i;

            // The type: one or more words, qualified names and template arguments.
            int words = 0;
            while (i < to)
            {
                if (T(i) == "typename") { i++; continue; }
                if (T(i) == "::") { i++; continue; }
                if (_t[i].Kind == TokenKind.Identifier && (!Keywords.Contains(T(i)) || BuiltinTypes.Contains(T(i))))
                {
                    // Another word is part of the type only if it is a builtin word (long long)
                    // or follows ::; otherwise it is the variable's name.
                    if (words > 0 && T(i - 1) != "::" && !(BuiltinTypes.Contains(T(i)) && BuiltinTypes.Contains(T(i - 1))))
                        break;
                    words++;
                    i++;
                    if (T(i) == "<")
                    {
                        int close = MatchAngle(i);
                        if (close < 0 || close >= to) return null;
                        i = close + 1;
                    }
                    continue;
                }
                break;
            }
            if (words == 0) return null;
            int typeEnd = i - 1;

            var declared = new List<CppVariable>();
            string baseType = TypeText(_t, typeStart, typeEnd);
            if (baseType == "auto") baseType = "";

            while (true)
            {
                int modifiersStart = i;
                while (i < to && T(i) is "*" or "&" or "&&" or "const") i++;
                string modifiers = string.Concat(Enumerable.Range(modifiersStart, i - modifiersStart).Select(k => T(k) == "const" ? " const" : T(k)));

                // Structured bindings: auto& [key, value] = ...
                if (T(i) == "[" && baseType.Length == 0)
                {
                    int close = Match(i);
                    for (int k = i + 1; k < close; k++) if (IsIdentifier(k)) declared.Add(new CppVariable(T(k), ""));
                    i = close + 1;
                }
                else if (IsIdentifier(i))
                {
                    string after = T(i + 1);
                    bool ends = after is "=" or "(" or "{" or ";" or "," or "[" || (rangeFor && after == ":") || i + 1 == to;
                    if (!ends) return declared.Count > 0 ? declared : null;
                    // A function's own name followed by ( is a call, unless a type came first.
                    string type = baseType.Length == 0 ? "" : (baseType + modifiers).Trim();
                    // An array whose size is not a number — int dp[n + 1] — has a type no
                    // template can take, so it is never captured.
                    if (after == "[" && Enumerable.Range(i + 2, Math.Max(0, Match(i + 1) - i - 2)).Any(k => _t[k].Kind != TokenKind.Number))
                        type = VariableLengthArray;
                    declared.Add(new CppVariable(T(i), StripReference(type)));
                    i++;
                }
                else return declared.Count > 0 ? declared : null;

                // On to the next declarator, past any initializer.
                while (i < to && T(i) != ",")
                {
                    if (T(i) is "(" or "[" or "{") i = Match(i);
                    else if (T(i) == "<" && MatchAngle(i) is int a && a > 0 && a < to) i = a;
                    i++;
                }
                if (i >= to || rangeFor) break;
                i++;
            }
            return declared.Count > 0 ? declared : null;
        }

        private static string StripReference(string type) => type.TrimEnd('&').Trim();

        // ---- lambdas ------------------------------------------------------------------------------

        private void FindLambdas(CppStatement owner, int from, int to, IReadOnlyList<CppVariable> declares)
        {
            for (int i = from; i <= to && i < _t.Count; i++)
            {
                if (T(i) != "[") continue;
                // After a value, [ is a subscript — a[i], f()[0]; anywhere else it opens a lambda.
                int before = i - 1;
                bool afterValue = before >= 0 && _t[before].Kind is TokenKind.Identifier or TokenKind.Number or TokenKind.Text
                                  && T(before) != "return";
                bool subscript = afterValue || T(before) is ")" or "]";
                if (subscript) { i = Match(i); continue; }

                int captureClose = Match(i);
                int k = captureClose + 1;
                var parameters = new List<CppVariable>();
                if (T(k) == "(")
                {
                    parameters.AddRange(Parameters(k).Select(p => new CppVariable(p.Name, StripReference(p.Type))));
                    k = Match(k) + 1;
                }
                string? returnType = null;
                while (k <= to && T(k) != "{")
                {
                    if (T(k) == "->")
                    {
                        int r = k + 1;
                        while (r <= to && T(r) != "{") r++;
                        returnType = TypeText(_t, k + 1, r - 1);
                        k = r;
                        break;
                    }
                    if (T(k) is "mutable" or "noexcept" or "constexpr") { k++; continue; }
                    break;
                }
                if (T(k) != "{") { i = captureClose; continue; }

                var captured = new List<string>();
                bool all = false;
                for (int c = i + 1; c < captureClose; c++)
                {
                    if ((T(c) is "&" or "=") && (T(c + 1) is "," or "]")) all = true;
                    else if (IsIdentifier(c)) captured.Add(T(c));
                }

                var lambda = new CppLambda
                {
                    Open = i,
                    BodyOpen = k,
                    BodyClose = Match(k),
                    CapturesAll = all,
                    Captured = captured,
                    Parameters = parameters,
                    ReturnType = returnType,
                    // auto dfs = [&](...) {...}: the lambda is called dfs.
                    Name = declares.Count == 1 ? declares[0].Name : null
                };
                lambda.Body = Block(k);
                owner.Lambdas.Add(lambda);
                i = lambda.BodyClose;
            }
        }
    }
}
