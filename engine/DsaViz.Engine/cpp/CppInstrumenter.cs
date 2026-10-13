using System.Text;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Cpp;

/// <summary>A C++ program ready to compile: the rewritten solution, its driver, and what its statements are.</summary>
public sealed record CppProgram(string Source, IReadOnlyList<StatementInfo> Statements, IReadOnlyList<LibraryCallSite> Sites);

/// <summary>
/// Rewrites a C++ solution so it reports itself, and adds a main that reads the inputs,
/// calls the solution and writes what it returned.
///
/// Every insertion goes inside an existing line — no line break is ever added before the
/// end of the student's code — so a compiler error, a crash or a step names the line the
/// student sees. Two builds, as for C#: one counts statements and charges library calls,
/// for measuring; the other records every variable at every step, for stepping through.
/// </summary>
public static class CppInstrumenter
{
    public const string SolutionFile = "solution.cpp";
    public const string DriverFile = "dsaviz-driver.cpp";

    // LeetCode's own definitions, as its templates show them in a comment.
    private const string TreeNodeDefinition =
        "struct TreeNode { int val; TreeNode *left; TreeNode *right; " +
        "TreeNode() : val(0), left(nullptr), right(nullptr) {} " +
        "TreeNode(int x) : val(x), left(nullptr), right(nullptr) {} " +
        "TreeNode(int x, TreeNode *left, TreeNode *right) : val(x), left(left), right(right) {} };";
    private const string ListNodeDefinition =
        "struct ListNode { int val; ListNode *next; ListNode() : val(0), next(nullptr) {} " +
        "ListNode(int x) : val(x), next(nullptr) {} ListNode(int x, ListNode *next) : val(x), next(next) {} };";

    public static readonly Dictionary<string, (string Member, string Cost, string Helper)> Algorithms = new(StringComparer.Ordinal)
    {
        ["find"] = ("std::find", "O(n)", "scanned"),
        ["find_if"] = ("std::find_if", "O(n)", "scanned"),
        ["count"] = ("std::count", "O(n)", "scanned"),
        ["count_if"] = ("std::count_if", "O(n)", "scanned"),
        ["accumulate"] = ("std::accumulate", "O(n)", "scanned"),
        ["min_element"] = ("std::min_element", "O(n)", "scanned"),
        ["max_element"] = ("std::max_element", "O(n)", "scanned"),
        ["reverse"] = ("std::reverse", "O(n)", "scanned"),
        ["copy"] = ("std::copy", "O(n)", "scanned"),
        ["sort"] = ("std::sort", "O(n log n)", "sorted"),
        ["stable_sort"] = ("std::stable_sort", "O(n log n)", "sorted")
    };

    /// <summary>The node classes the file uses without defining, which are supplied.</summary>
    public static IReadOnlyList<string> SuppliedNodes(CppFile file)
    {
        var defined = file.Classes.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var used = file.Tokens.Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        return new[] { "TreeNode", "ListNode" }.Where(n => used.Contains(n) && !defined.Contains(n)).ToList();
    }

    public static CppProgram Build(CppFile file, CppFunction entry, bool stepping)
        => new Rewriter(file, stepping).Build(entry);

    /// <summary>The solution with nothing inserted, and the same driver: compiled to find the student's own errors.</summary>
    public static string Plain(CppFile file, CppFunction entry)
        => Assemble(file, file.Text, entry, statements: 0, sites: 0);

    private static string Assemble(CppFile file, string solution, CppFunction entry, int statements, int sites)
    {
        var program = new StringBuilder();
        program.Append("#include \"dsaviz.hpp\"\n");
        foreach (var node in SuppliedNodes(file))
            program.Append(node == "TreeNode" ? TreeNodeDefinition : ListNodeDefinition).Append('\n');
        program.Append($"#line 1 \"{SolutionFile}\"\n");
        program.Append(solution);
        program.Append($"\n#line 1 \"{DriverFile}\"\n");
        program.Append(Driver(entry, statements, sites));
        return program.ToString();
    }

    /// <summary>
    /// Reads every input, calls the entry and writes its result. Inputs are read before
    /// counting starts, and an exception while reading them is the visualizer's, not the
    /// student's.
    /// </summary>
    private static string Driver(CppFunction entry, int statements, int sites)
    {
        var d = new StringBuilder();
        d.Append("static std::vector<std::string> dsaviz_inputs;\n");
        d.Append("static void dsaviz_run() {\n");
        var arguments = new List<string>();
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            string type = ValueType(entry.Parameters[i].Type);
            d.Append($"  {type} dsaviz_a{i};\n");
            arguments.Add($"dsaviz_a{i}");
        }
        d.Append("  try {\n");
        for (int i = 0; i < entry.Parameters.Count; i++)
            d.Append($"    dsaviz_a{i} = dsaviz::build<{ValueType(entry.Parameters[i].Type)}>(dsaviz::Parser{{dsaviz_inputs.at({i})}}.parse());\n");
        d.Append("  } catch (const std::exception& e) { dsaviz::finish(\"InputError\", e.what(), -1); }\n");

        string call = entry.Owner is null ? entry.Name
            : entry.IsStatic ? $"{entry.Owner}::{entry.Name}"
            : $"dsaviz_target.{entry.Name}";
        string args = string.Join(", ", arguments);
        d.Append("  try {\n");
        if (entry.Owner is not null && !entry.IsStatic) d.Append($"    {entry.Owner} dsaviz_target;\n");
        d.Append("    dsaviz::begin();\n");
        if (entry.ReturnType == "void")
            d.Append($"    {call}({args});\n    dsaviz::succeed<int>(nullptr);\n");
        else
            d.Append($"    auto dsaviz_result = {call}({args});\n    dsaviz::succeed(&dsaviz_result);\n");
        d.Append("  } catch (const std::exception& e) { dsaviz::threw(e); } catch (...) { dsaviz::threwSomething(); }\n");
        d.Append("}\n");
        d.Append("int main(int argc, char** argv) {\n");
        d.Append($"  dsaviz::configure(argc, argv, {statements}, {sites});\n");
        d.Append("  dsaviz_inputs = dsaviz::readInputs();\n");
        d.Append("  dsaviz::deep(dsaviz_run);\n");
        d.Append("  return 0;\n}\n");
        return d.ToString();
    }

    /// <summary>"const vector&lt;int&gt;&amp;" as the value it holds: "vector&lt;int&gt;".</summary>
    public static string ValueType(string written)
    {
        string type = written.Trim();
        while (type.EndsWith('&')) type = type[..^1].TrimEnd();
        if (type.StartsWith("const ", StringComparison.Ordinal)) type = type[6..];
        if (type.EndsWith(" const", StringComparison.Ordinal)) type = type[..^6];
        return type.Trim();
    }

    /// <summary>An edit to the text: insert at an offset, or replace a range. Ordered by offset, then rank.</summary>
    private sealed record Edit(int Start, int End, string Text, int Rank, int Sequence);

    private sealed class Rewriter
    {
        private readonly CppFile _file;
        private readonly IReadOnlyList<CppToken> _t;
        private readonly bool _stepping;
        private readonly List<Edit> _edits = new();
        private readonly List<StatementInfo> _statements = new();
        private readonly List<LibraryCallSite> _sites = new();
        private readonly HashSet<string> _userNames;
        private string _method = "";
        private string? _returnType;

        public Rewriter(CppFile file, bool stepping)
        {
            _file = file;
            _t = file.Tokens;
            _stepping = stepping;
            // Names the student defined: a call to one of them is theirs, never the library's.
            _userNames = file.Functions.Select(f => f.Name)
                .Concat(AllDeclared(file))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static IEnumerable<string> AllDeclared(CppFile file)
        {
            IEnumerable<string> Of(CppStatement s) => s.Declares.Concat(s.HeaderDeclares).Select(v => v.Name)
                .Concat(s.Children.SelectMany(Of))
                .Concat(s.Else is null ? Enumerable.Empty<string>() : Of(s.Else))
                .Concat(s.Lambdas.SelectMany(l => l.Parameters.Select(p => p.Name).Concat(Of(l.Body))));
            return file.Functions.SelectMany(f => f.Parameters.Select(p => p.Name).Concat(Of(f.Body)));
        }

        private void Insert(int offset, string text, int rank) => _edits.Add(new Edit(offset, offset, text, rank, _edits.Count));

        public CppProgram Build(CppFunction entry)
        {
            foreach (var function in _file.Functions)
            {
                _method = function.IsConstructor ? function.Owner ?? function.Name : function.Name;
                _returnType = function.ReturnType;
                var visible = function.Parameters
                    .Select(p => new CppVariable(p.Name, CppInstrumenter.ValueType(p.Type)))
                    .ToList();
                Body(function.Body, visible, function.Name == "main" && function.Owner is null);
            }

            // The student's own main, when there is one, is renamed so the driver's is the one that runs.
            var main = _file.Functions.FirstOrDefault(f => f.Name == "main" && f.Owner is null);
            if (main is not null)
            {
                var name = _t.First(t => t.Line == main.Line && t.Text == "main");
                _edits.Add(new Edit(name.Start, name.End, "dsaviz_student_main", 0, _edits.Count));
            }

            var text = new StringBuilder(_file.Text.Length + _edits.Count * 40);
            int at = 0;
            foreach (var edit in _edits.OrderBy(e => e.Start).ThenBy(e => e.Rank).ThenBy(e => e.Sequence))
            {
                text.Append(_file.Text, at, edit.Start - at);
                text.Append(edit.Text);
                at = Math.Max(at, edit.End);
            }
            text.Append(_file.Text, at, _file.Text.Length - at);

            return new CppProgram(Assemble(_file, text.ToString(), entry, _statements.Count, _sites.Count), _statements, _sites);
        }

        // ---- frames and statements -----------------------------------------------------------

        private void Body(CppStatement body, List<CppVariable> visible, bool isMain)
        {
            // A frame for every call: depth, and the recursion limit. The student's main is
            // not stepped — it is where examples are called from, not the solution.
            if (isMain) return;
            Insert(_t[body.First].End, " dsaviz::Frame dsaviz_frame_;", -5);
            Block(body, visible);
        }

        private void Block(CppStatement block, List<CppVariable> outer)
        {
            var scope = new List<CppVariable>(outer);
            foreach (var child in block.Children)
            {
                Statement(child, scope, braced: true);
                foreach (var declared in child.Declares)
                {
                    scope.RemoveAll(v => v.Name == declared.Name);
                    scope.Add(declared);
                }
            }
        }

        /// <summary>
        /// One statement. <paramref name="braced"/> is false for the body of an if, a loop or an
        /// else written without braces, which gets them: the step call has to run with it.
        /// </summary>
        private void Statement(CppStatement statement, List<CppVariable> visible, bool braced)
        {
            switch (statement.Kind)
            {
                case StatementKind.Block:
                    Block(statement, visible);
                    return;
                case StatementKind.Label:
                case StatementKind.Empty:
                    return;
            }

            if (!braced)
            {
                Insert(_t[statement.First].Start, "{ ", -20);
                Insert(_t[statement.Last].End, " }", 20);
            }

            Step(statement, visible);

            switch (statement.Kind)
            {
                case StatementKind.If:
                    Lambdas(statement, visible);
                    Statement(statement.Children[0], visible, statement.Children[0].Kind == StatementKind.Block);
                    if (statement.Else is { } otherwise) Statement(otherwise, visible, otherwise.Kind == StatementKind.Block);
                    break;
                case StatementKind.For:
                case StatementKind.RangeFor:
                {
                    Lambdas(statement, visible);
                    var inside = Shadow(visible, statement.HeaderDeclares);
                    Statement(statement.Children[0], inside, statement.Children[0].Kind == StatementKind.Block);
                    break;
                }
                case StatementKind.While:
                case StatementKind.Do:
                    Lambdas(statement, visible);
                    Statement(statement.Children[0], visible, statement.Children[0].Kind == StatementKind.Block);
                    break;
                case StatementKind.Switch:
                case StatementKind.Try:
                    foreach (var child in statement.Children) Statement(child, visible, true);
                    break;
                case StatementKind.Return:
                    Lambdas(statement, visible);
                    if (_stepping) Returned(statement);
                    Charges(statement.First, statement.Last, statement.Lambdas);
                    break;
                default:
                    Lambdas(statement, Shadow(visible, statement.Declares));
                    Charges(statement.First, statement.Last, statement.Lambdas);
                    break;
            }

            if (statement.Kind is StatementKind.If or StatementKind.For or StatementKind.RangeFor or StatementKind.While)
                Charges(statement.First, HeaderEnd(statement), statement.Lambdas);
        }

        /// <summary>The last token of a control statement's header: the ')' before its body.</summary>
        private int HeaderEnd(CppStatement statement) => statement.Children.Count > 0 ? statement.Children[0].First - 1 : statement.Last;

        private static List<CppVariable> Shadow(List<CppVariable> visible, IEnumerable<CppVariable> added)
        {
            var scope = new List<CppVariable>(visible);
            foreach (var v in added) { scope.RemoveAll(x => x.Name == v.Name); scope.Add(v); }
            return scope;
        }

        private void Step(CppStatement statement, List<CppVariable> visible)
        {
            int id = _statements.Count;
            var first = _t[statement.First];
            var captured = visible.Where(v => v.Type != CppReader.VariableLengthArray).ToList();
            string snippet = Snippet(statement);
            _statements.Add(new StatementInfo(id, first.Line, first.Column, snippet.Length, snippet)
            {
                Method = _method,
                Variables = _stepping ? captured.Select(v => v.Name).ToList() : Array.Empty<string>()
            });

            string call = _stepping
                ? $"dsaviz::step({id}{string.Concat(captured.Select(v => $", dsaviz::var(\"{v.Name}\", \"{Escape(v.Type)}\", {v.Name})"))}); "
                : $"dsaviz::hit({id}); ";
            Insert(first.Start, call, -10);
        }

        private string Snippet(CppStatement statement)
        {
            var first = _t[statement.First];
            int end = statement.Kind is StatementKind.Simple or StatementKind.Return ? _t[statement.Last].End : first.End;
            if (statement.Kind is StatementKind.If or StatementKind.For or StatementKind.RangeFor or StatementKind.While or StatementKind.Switch)
                end = _t[HeaderEnd(statement)].End;
            string text = _file.Text[first.Start..Math.Max(first.Start, end)];
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline].TrimEnd();
            return text.Length > 80 ? text[..80] + "…" : text;
        }

        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>return x; → return dsaviz::returned(x);, so the recursion tree can show what each call returned.</summary>
        private void Returned(CppStatement statement)
        {
            int expression = statement.First + 1;
            if (expression >= statement.Last) return;                // return;
            string open;
            if (_t[expression].Text == "{")
            {
                // return {a, b}; takes its type from the function, which has to be written out.
                if (string.IsNullOrEmpty(_returnType) || _returnType.Contains("auto")) return;
                open = $"dsaviz::returned<{_returnType}>(";
            }
            else open = "dsaviz::returned(";
            Insert(_t[expression].Start, open, 0);
            Insert(_t[statement.Last].Start, ")", 10);
        }

        // ---- lambdas ----------------------------------------------------------------------------

        private void Lambdas(CppStatement statement, List<CppVariable> visible)
        {
            foreach (var lambda in statement.Lambdas)
            {
                var outer = lambda.CapturesAll ? visible : visible.Where(v => lambda.Captured.Contains(v.Name)).ToList();
                var inside = Shadow(outer, lambda.Parameters);
                string saved = _method;
                string? savedReturn = _returnType;
                _method = lambda.Name ?? $"lambda in {saved}";
                _returnType = lambda.ReturnType;
                Body(lambda.Body, inside, isMain: false);
                _method = saved;
                _returnType = savedReturn;
            }
        }

        // ---- library costs ---------------------------------------------------------------------

        /// <summary>
        /// Charges the library calls between two tokens whose cost grows with the input: a
        /// scan, a sort, a shift, a string search or a substring. Measuring only: stepping
        /// shows what ran, not what it cost.
        /// </summary>
        private void Charges(int from, int to, IReadOnlyList<CppLambda> lambdas)
        {
            if (_stepping) return;
            for (int i = from; i <= to && i < _t.Count; i++)
            {
                // A lambda's body is charged on its own, as statements of the lambda.
                if (lambdas.FirstOrDefault(l => i >= l.BodyOpen && i <= l.BodyClose) is { } inside) { i = inside.BodyClose; continue; }
                var token = _t[i];
                if (token.Kind != TokenKind.Identifier || T(i + 1) != "(") continue;
                bool member = T(i - 1) is "." or "->";

                if (!member && Algorithms.TryGetValue(token.Text, out var algorithm) && !_userNames.Contains(token.Text)
                    && (T(i - 1) != "::" || T(i - 2) == "std"))
                {
                    var arguments = Arguments(i + 1);
                    if (arguments.Count < 2) continue;
                    // find(a, b, x) → dsaviz::scanned(0, a, b, [&](auto&& f, auto&& l) { return find(f, l, x); }):
                    // the range is evaluated once, by the helper, and the rest once, in the call.
                    int start = T(i - 1) == "::" ? i - 2 : i;
                    int afterSecond = ArgumentEnd(i + 1, 2);
                    int site = Site(token, algorithm.Member, algorithm.Cost);
                    string name = _file.Text[_t[start].Start..token.End];
                    Replace(_t[start].Start, _t[afterSecond].End,
                        $"dsaviz::{algorithm.Helper}({site}, {arguments[0]}, {arguments[1]}, " +
                        $"[&](auto&& dsaviz_first, auto&& dsaviz_last) {{ return {name}(dsaviz_first, dsaviz_last", 1);
                    Insert(_t[Close(i + 1)].End, "; })", 5);
                    i = afterSecond;
                    continue;
                }

                if (!member || T(i - 1) != ".") continue;
                int objectStart = ExpressionStart(i - 2);
                if (objectStart < 0) continue;
                switch (token.Text)
                {
                    case "find":
                    {
                        int site = Site(token, "string::find", "O(n)");
                        Insert(_t[objectStart].Start, $"dsaviz::searching({site}, ", 1);
                        Insert(_t[i - 1].Start, ")", 5);
                        break;
                    }
                    case "substr":
                    {
                        int site = Site(token, "string::substr", "O(length)");
                        Insert(_t[objectStart].Start, $"dsaviz::copied({site}, ", 1);
                        Insert(_t[Close(i + 1)].End, ")", 5);
                        break;
                    }
                    case "insert":
                    case "erase":
                    {
                        // v.erase(it) → dsaviz::erased(0, v, it): the object and every argument
                        // evaluated once, as written.
                        if (Arguments(i + 1).Count == 0) break;
                        int site = Site(token, $"vector::{token.Text}", "O(n)");
                        Insert(_t[objectStart].Start, $"dsaviz::{(token.Text == "insert" ? "inserted" : "erased")}({site}, ", 1);
                        Replace(_t[i - 1].Start, _t[i + 1].End, ", ", 1);
                        break;
                    }
                }
            }
        }

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";

        private void Replace(int start, int end, string text, int rank) => _edits.Add(new Edit(start, end, text, rank, _edits.Count));

        /// <summary>The last token of argument number <paramref name="count"/> (1-based) in the call whose '(' is at <paramref name="open"/>.</summary>
        private int ArgumentEnd(int open, int count)
        {
            int depth = 0, seen = 0;
            for (int i = open + 1; i < _t.Count; i++)
            {
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}") { if (depth == 0) return i - 1; depth--; }
                else if (depth == 0 && T(i) == "," && ++seen == count) return i - 1;
            }
            return open;
        }

        private int Site(CppToken token, string member, string cost)
        {
            int site = _sites.Count;
            _sites.Add(new LibraryCallSite(site, token.Line, token.Column, member, cost));
            return site;
        }

        private int Close(int open)
        {
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}" && --depth == 0) return i;
            }
            return _t.Count - 1;
        }

        /// <summary>The text of each top-level argument in the call whose '(' is at <paramref name="open"/>.</summary>
        private List<string> Arguments(int open)
        {
            int close = Close(open);
            var arguments = new List<string>();
            int start = open + 1, depth = 0;
            for (int i = open + 1; i <= close; i++)
            {
                if (i < close && T(i) is "(" or "[" or "{") depth++;
                else if (i < close && T(i) is ")" or "]" or "}") depth--;
                else if (i == close || (depth == 0 && T(i) == ","))
                {
                    if (i > start) arguments.Add(_file.Text[_t[start].Start.._t[i - 1].End]);
                    start = i + 1;
                }
            }
            return arguments;
        }

        /// <summary>Where the object expression ending at <paramref name="end"/> starts: s, grid[i], this->s, get().</summary>
        private int ExpressionStart(int end)
        {
            int i = end;
            while (i >= 0)
            {
                if (T(i) is ")" or "]")
                {
                    int depth = 0;
                    for (; i >= 0; i--)
                    {
                        if (T(i) is ")" or "]") depth++;
                        else if (T(i) is "(" or "[" && --depth == 0) break;
                    }
                    i--;
                    continue;
                }
                if (_t[i].Kind != TokenKind.Identifier) return -1;
                if (T(i - 1) is "." or "->" or "::") { i -= 2; continue; }
                return i;
            }
            return -1;
        }
    }
}
