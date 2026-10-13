using System.Text;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Java;

/// <summary>Insert <see cref="Text"/> at <see cref="Start"/>, or replace up to <see cref="End"/>. Ordered by offset, then rank.</summary>
public sealed record JavaEdit(int Start, int End, string Text, int Rank, int Sequence, int? Group);

/// <summary>A step: the method or lambda it is in, where it sits in the code's structure, and its reads.</summary>
public sealed record JavaStep(int Id, int Root, IReadOnlyList<(int Block, int Index)> Path, int FirstToken, IReadOnlyDictionary<string, int> Groups);

/// <summary>
/// A Java program as edits to the student's file, any group of which can be left out: a
/// variable read javac rejects ("might not have been initialized"), a return value it
/// cannot pass through, a library charge that does not type-check.
/// </summary>
public sealed class JavaPlan
{
    public required string Student { get; init; }
    public required string Appended { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<JavaEdit> Edits { get; init; }
    public required IReadOnlyList<StatementInfo> Statements { get; init; }
    public required IReadOnlyList<LibraryCallSite> Sites { get; init; }
    public required IReadOnlyDictionary<int, (string Name, int Role)?> Groups { get; init; }
    public required IReadOnlyList<JavaStep> Steps { get; init; }
    public required JvFile File { get; init; }

    public (string Source, Dictionary<int, List<(int Start, int End)>> Placed) Render(IReadOnlyDictionary<int, string> left)
    {
        var text = new StringBuilder(Student.Length + Edits.Count * 40 + Appended.Length);
        var placed = new Dictionary<int, List<(int, int)>>();
        int at = 0;
        foreach (var edit in Edits.OrderBy(e => e.Start).ThenBy(e => e.Rank).ThenBy(e => e.Sequence))
        {
            text.Append(Student, at, edit.Start - at);
            at = Math.Max(at, edit.End);
            string inserted = edit.Text;
            if (edit.Group is { } group && left.TryGetValue(group, out var why))
                inserted = Groups[group] is { } variable ? $".gone(\"{variable.Name}\", \"{why}\")" : "";
            int start = text.Length;
            text.Append(inserted);
            if (edit.Group is { } g)
            {
                if (!placed.TryGetValue(g, out var list)) placed[g] = list = new List<(int, int)>();
                list.Add((start, text.Length));
            }
        }
        text.Append(Student, at, Student.Length - at);
        text.Append(Appended);
        return (text.ToString(), placed);
    }

    /// <summary>
    /// The later reads of the same variable that javac will reject for the same reason: the
    /// ones at steps that run after this one on the same path, up to where the code assigns it.
    /// </summary>
    public IEnumerable<int> Following(int group)
    {
        var from = Steps.FirstOrDefault(s => s.Groups.Values.Contains(group));
        if (from is null || Groups[group] is not { } variable) yield break;
        foreach (var to in Steps)
        {
            if (to.Root != from.Root || !to.Groups.TryGetValue(variable.Name, out var other) || !After(from, to)) continue;
            if (!SetBetween(variable.Name, from.FirstToken, to.FirstToken)) yield return other;
        }
    }

    private static bool After(JavaStep from, JavaStep to)
    {
        int n = Math.Min(from.Path.Count, to.Path.Count);
        for (int k = 0; k < n; k++)
        {
            if (from.Path[k].Block != to.Path[k].Block) return false;
            if (from.Path[k].Index != to.Path[k].Index) return to.Path[k].Index > from.Path[k].Index;
        }
        return to.Path.Count > from.Path.Count;
    }

    private bool SetBetween(string name, int fromToken, int toToken)
    {
        var t = File.Tokens;
        for (int i = fromToken; i < toToken && i < t.Count; i++)
            if (t[i].Text == name && i + 1 < t.Count && t[i + 1].Text is "=" or "+=" or "-=" or "++" or "--") return true;
        return false;
    }
}

/// <summary>
/// Rewrites a Java solution so it reports itself, and adds a main class that reads the
/// inputs, calls the solution and writes what it returned. Every insertion goes inside an
/// existing line — LeetCode's imports included, on line 1 — so a compiler error, an
/// exception or a step names the line the student sees.
/// </summary>
public static class JavaInstrumenter
{
    public const string Imports = "import java.util.*; import java.util.function.*; import java.util.stream.*; ";

    private const string TreeNodeDefinition =
        "class TreeNode { int val; TreeNode left; TreeNode right; TreeNode() {} TreeNode(int val) { this.val = val; } " +
        "TreeNode(int val, TreeNode left, TreeNode right) { this.val = val; this.left = left; this.right = right; } }";
    private const string ListNodeDefinition =
        "class ListNode { int val; ListNode next; ListNode() {} ListNode(int val) { this.val = val; } " +
        "ListNode(int val, ListNode next) { this.val = val; this.next = next; } }";

    /// <summary>The file the program is compiled as: its public class's name, else Solution.java.</summary>
    public static string FileNameOf(JvFile file)
        => (file.Classes.FirstOrDefault(c => c.TopLevel && c.IsPublic)?.Name ?? "Solution") + ".java";

    public static JavaPlan Build(JvFile file, JvMethod? entry, string? design, bool stepping, bool instrument = true)
    {
        var rewriter = new Rewriter(file, stepping);
        rewriter.Prelude();
        if (instrument) rewriter.Instrument();
        string fileName = FileNameOf(file);
        return new JavaPlan
        {
            Student = file.Text,
            Appended = Appended(file, entry, design, rewriter.Statements.Count, rewriter.Sites.Count, fileName),
            FileName = fileName,
            Edits = rewriter.Edits,
            Statements = rewriter.Statements,
            Sites = rewriter.Sites,
            Groups = rewriter.Groups,
            Steps = rewriter.Steps,
            File = file
        };
    }

    private static string Appended(JvFile file, JvMethod? entry, string? design, int statements, int sites, string fileName)
    {
        var a = new StringBuilder("\n\n// ---- added by DSA Playback ----\n");
        var defined = file.Classes.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var used = file.Tokens.Where(t => t.Kind == JvTokenKind.Identifier).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        if (used.Contains("TreeNode") && !defined.Contains("TreeNode")) a.Append(TreeNodeDefinition).Append('\n');
        if (used.Contains("ListNode") && !defined.Contains("ListNode")) a.Append(ListNodeDefinition).Append('\n');

        a.Append("class DsavizMain {\n");
        a.Append("    public static void main(String[] args) throws Exception {\n");
        a.Append($"        Dsaviz.configure(args, {statements}, {sites}, \"{fileName}\");\n");
        a.Append("        final String[] in = Dsaviz.inputs();\n");
        a.Append("        Dsaviz.deep(() -> {\n");
        if (design is not null)
        {
            a.Append("            try {\n");
            a.Append("                Dsaviz.begin();\n");
            a.Append($"                Object r = Dsaviz.design({design}.class, in.length > 0 ? in[0] : \"\", in.length > 1 ? in[1] : \"\");\n");
            a.Append("                Dsaviz.succeed(r, false);\n");
            a.Append("            } catch (Throwable t) { Dsaviz.threw(t); }\n");
        }
        else if (entry is not null)
        {
            var arguments = new List<string>();
            for (int i = 0; i < entry.Parameters.Count; i++)
            {
                string type = entry.Parameters[i].Type;
                a.Append($"            final {type} a{i} = Dsaviz.read(in, {i}, \"{type}\");\n");
                arguments.Add($"a{i}");
            }
            a.Append("            try {\n");
            string call;
            if (entry.IsStatic) call = $"{entry.Owner}.{entry.Name}({string.Join(", ", arguments)})";
            else
            {
                a.Append($"                final {entry.Owner} target = new {entry.Owner}();\n");
                call = $"target.{entry.Name}({string.Join(", ", arguments)})";
            }
            a.Append("                Dsaviz.begin();\n");
            if (entry.ReturnType == "void") a.Append($"                {call};\n                Dsaviz.succeed(null, true);\n");
            else a.Append($"                Object r = {call};\n                Dsaviz.succeed(r, false);\n");
            a.Append("            } catch (Throwable t) { Dsaviz.threw(t); }\n");
        }
        a.Append("        });\n    }\n}\n");
        return a.ToString();
    }

    // ---- the rewriter -----------------------------------------------------------------------------

    private static readonly HashSet<string> Scans = new(StringComparer.Ordinal) { "contains", "indexOf", "lastIndexOf", "containsValue" };
    private static readonly HashSet<string> Made = new(StringComparer.Ordinal)
        { "substring", "toCharArray", "toString", "split", "toArray", "copyOf", "copyOfRange", "join", "valueOf", "repeat" };
    private static readonly HashSet<string> CopyTypes = new(StringComparer.Ordinal)
        { "ArrayList", "LinkedList", "HashSet", "TreeSet", "LinkedHashSet", "HashMap", "TreeMap", "LinkedHashMap", "ArrayDeque", "PriorityQueue" };

    private sealed class Rewriter
    {
        private readonly JvFile _file;
        private readonly IReadOnlyList<JvToken> _t;
        private readonly bool _stepping;
        public List<JavaEdit> Edits { get; } = new();
        public List<StatementInfo> Statements { get; } = new();
        public List<LibraryCallSite> Sites { get; } = new();
        public Dictionary<int, (string Name, int Role)?> Groups { get; } = new();
        public List<JavaStep> Steps { get; } = new();
        private string _method = "";
        private Dictionary<string, int> _roles = new(StringComparer.Ordinal);
        private int _ids, _root;
        private List<(int Block, int Index)> _path = new();

        public Rewriter(JvFile file, bool stepping)
        {
            _file = file;
            _t = file.Tokens;
            _stepping = stepping;
        }

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";

        private void Insert(int offset, string text, int rank, int? group = null)
            => Edits.Add(new JavaEdit(offset, offset, text, rank, Edits.Count, group));

        private int NewGroup((string, int)? variable = null)
        {
            int id = Groups.Count;
            Groups[id] = variable;
            return id;
        }

        /// <summary>LeetCode's own imports, on line 1; the package line, if any, blanked so nothing moves.</summary>
        public void Prelude()
        {
            if (_file.Package is { } package)
            {
                Edits.Add(new JavaEdit(package.Start, package.End, new string(' ', package.End - package.Start), 0, Edits.Count, null));
                Insert(package.Start, Imports, -1);
            }
            else Insert(0, Imports, -1);
        }

        public void Instrument()
        {
            foreach (var method in _file.Methods)
            {
                if (method.Name == "main" && method.IsStatic) continue;           // where examples are called from
                var owner = _file.Classes.FirstOrDefault(c => c.Name == method.Owner);
                _method = method.IsConstructor ? method.Owner : method.Name;
                _roles = Roles(method);
                var visible = method.Parameters.Select(p => new JvVariable(p.Name, p.Type)).ToList();
                if (!method.IsStatic && owner is { Fields.Count: > 0 }) visible.Insert(0, new JvVariable("this", owner.Name));
                Body(method.Body, visible, method.IsConstructor);
            }
        }

        // ---- frames and statements ----------------------------------------------------------------

        private void Body(JvStatement body, List<JvVariable> visible, bool constructor)
        {
            // A constructor's this(...) or super(...) must come first.
            var first = body.Children.FirstOrDefault();
            bool delegates = constructor && first is { Kind: JvStatementKind.Expression } && T(first.First) is "this" or "super" && T(first.First + 1) == "(";
            int open = delegates ? _t[first!.Last].End : _t[body.First].End;
            Insert(open, " Dsaviz.enter(); try {", -5);
            Insert(_t[body.Last].Start, "} finally { Dsaviz.leave(); } ", 5);

            var (savedRoot, savedPath) = (_root, _path);
            (_root, _path) = (++_ids, new List<(int, int)>());
            Block(body, visible, skipFirst: delegates);
            (_root, _path) = (savedRoot, savedPath);
        }

        private void Block(JvStatement block, List<JvVariable> outer, bool skipFirst = false)
        {
            var scope = new List<JvVariable>(outer);
            int id = ++_ids;
            for (int i = 0; i < block.Children.Count; i++)
            {
                var child = block.Children[i];
                if (!(skipFirst && i == 0))
                {
                    _path.Add((id, i));
                    Statement(child, scope);
                    _path.RemoveAt(_path.Count - 1);
                }
                foreach (var v in child.Declares) { scope.RemoveAll(x => x.Name == v.Name); scope.Add(v); }
            }
        }

        private static List<JvVariable> With(List<JvVariable> visible, IEnumerable<JvVariable> added)
        {
            var scope = new List<JvVariable>(visible);
            foreach (var v in added) { scope.RemoveAll(x => x.Name == v.Name); scope.Add(v); }
            return scope;
        }

        private void Statement(JvStatement statement, List<JvVariable> visible, bool step = true)
        {
            if (statement.Kind is JvStatementKind.Empty or JvStatementKind.Item) return;
            if (statement.Kind == JvStatementKind.Label && statement.Children.Count == 0) return;      // a case label

            if (statement.Unbraced)
            {
                Insert(_t[statement.First].Start, "{ ", -20);
                Insert(_t[statement.Last].End, " }", 20);
            }
            if (step) Step(statement.First, Snippet(statement), visible);

            switch (statement.Kind)
            {
                case JvStatementKind.Block:
                    Block(statement, visible);
                    break;
                case JvStatementKind.Label:
                    // The label stays on its loop: the step goes before the label, not between them.
                    Statement(statement.Children[0], visible, step: false);
                    break;
                case JvStatementKind.Local:
                case JvStatementKind.Expression:
                    Lambdas(statement.Lambdas, visible);
                    Charges(statement.First, statement.Last, statement.Lambdas, visible);
                    break;
                case JvStatementKind.Return:
                    Lambdas(statement.Lambdas, visible);
                    if (_stepping && statement.Last > statement.First + 1) Returned(statement.First + 1, statement.Last);
                    Charges(statement.First, statement.Last, statement.Lambdas, visible);
                    break;
                case JvStatementKind.If:
                    Lambdas(statement.Lambdas, visible);
                    Charges(statement.First, statement.HeaderEnd, statement.Lambdas, visible);
                    Nested(statement.Children[0], visible);
                    if (statement.Else is { } otherwise) Nested(otherwise, visible);
                    break;
                case JvStatementKind.For:
                case JvStatementKind.ForEach:
                    Lambdas(statement.Lambdas, visible);
                    Nested(statement.Children[0], With(visible, statement.BodyDeclares));
                    break;
                case JvStatementKind.While:
                    Lambdas(statement.Lambdas, visible);
                    Charges(statement.First, statement.HeaderEnd, statement.Lambdas, visible);
                    Nested(statement.Children[0], visible);
                    break;
                case JvStatementKind.Do:
                case JvStatementKind.Synchronized:
                    Nested(statement.Children[0], visible);
                    break;
                case JvStatementKind.Switch:
                    Lambdas(statement.Lambdas, visible);
                    Block(statement, visible);
                    break;
                case JvStatementKind.Try:
                    for (int i = 0; i < statement.Children.Count; i++)
                    {
                        var scope = i == 0 ? With(visible, statement.BodyDeclares)
                            : statement.CatchParameters.TryGetValue(i, out var caught) ? With(visible, new[] { caught }) : visible;
                        Nested(statement.Children[i], scope);
                    }
                    break;
            }
        }

        /// <summary>A branch or a body: a block of its own in the code's structure.</summary>
        private void Nested(JvStatement statement, List<JvVariable> visible)
        {
            if (statement.Kind == JvStatementKind.Block) { Block(statement, visible); return; }
            _path.Add((++_ids, 0));
            Statement(statement, visible);
            _path.RemoveAt(_path.Count - 1);
        }

        private void Lambdas(IEnumerable<JvLambda> lambdas, List<JvVariable> visible)
        {
            foreach (var lambda in lambdas)
            {
                if (lambda.Body is null) continue;
                // Only what the lambda itself mentions: a lambda may only read effectively final
                // variables, and those it already reads are.
                var inside = With(visible.Where(v => lambda.Mentioned.Contains(v.Name) || v.Name == "this").ToList(), lambda.Parameters);
                string saved = _method;
                _method = lambda.Name ?? $"lambda in {saved}";
                Body(lambda.Body, inside, constructor: false);
                _method = saved;
            }
        }

        // ---- steps -------------------------------------------------------------------------------

        private void Step(int firstToken, string snippet, List<JvVariable> visible)
        {
            int id = Statements.Count;
            var first = _t[firstToken];
            var names = visible.GroupBy(v => v.Name).Select(g => g.Last()).ToList();
            Statements.Add(new StatementInfo(id, first.Line, first.Column, snippet.Length, snippet)
            {
                Method = _method,
                Variables = _stepping ? names.Select(v => v.Name).ToList() : Array.Empty<string>()
            });

            if (!_stepping)
            {
                Insert(first.Start, $"Dsaviz.hit({id}); ", -10);
                return;
            }
            Insert(first.Start, $"Dsaviz.s({id})", -10);
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var variable in names)
            {
                int role = _roles.GetValueOrDefault(variable.Name);
                int group = NewGroup((variable.Name, role));
                groups[variable.Name] = group;
                Insert(first.Start, role > 0 ? $".v(\"{variable.Name}\", {variable.Name}, {role})" : $".v(\"{variable.Name}\", {variable.Name})", -10, group);
            }
            Insert(first.Start, ".go(); ", -10);
            Steps.Add(new JavaStep(id, _root, _path.ToList(), firstToken, groups));
        }

        private string Snippet(JvStatement statement)
        {
            int last = statement.Kind switch
            {
                JvStatementKind.If or JvStatementKind.While or JvStatementKind.For or JvStatementKind.ForEach or JvStatementKind.Switch
                    or JvStatementKind.Synchronized => statement.HeaderEnd,
                JvStatementKind.Block or JvStatementKind.Do or JvStatementKind.Try or JvStatementKind.Label => statement.First,
                _ => statement.Last
            };
            string text = _file.Text[_t[statement.First].Start.._t[Math.Max(statement.First, last)].End];
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline].TrimEnd();
            return text.Length > 80 ? text[..80] + "…" : text;
        }

        /// <summary>return x; → return Dsaviz.ret(x); — what each call returned, for the recursion tree.</summary>
        private void Returned(int from, int semicolon)
        {
            int group = NewGroup();
            Insert(_t[from].Start, "Dsaviz.ret(", 0, group);
            Insert(_t[semicolon].Start, ")", 10, group);
        }

        // ---- stacks and queues ----------------------------------------------------------------------

        /// <summary>Deques this method uses with push and pop (stacks, 1) or with offer and poll (queues, 2).</summary>
        private Dictionary<string, int> Roles(JvMethod method)
        {
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            void Collect(JvStatement s)
            {
                foreach (var v in s.Declares.Concat(s.BodyDeclares)) declared[v.Name] = v.Type;
                foreach (var c in s.Children) Collect(c);
                if (s.Else is not null) Collect(s.Else);
                foreach (var l in s.Lambdas) if (l.Body is not null) Collect(l.Body);
            }
            Collect(method.Body);
            foreach (var p in method.Parameters) declared.TryAdd(p.Name, p.Type);

            var roles = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, type) in declared)
            {
                string raw = type.Split('<')[0];
                if (raw is not ("Deque" or "ArrayDeque" or "LinkedList" or "Queue")) continue;
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (int i = method.Body.First; i < method.Body.Last; i++)
                    if (T(i) == name && T(i + 1) == "." && T(i + 3) == "(") used.Add(T(i + 2));
                if (used.Contains("push") || (used.Contains("pop") && !used.Contains("offer") && !used.Contains("poll"))) roles[name] = 1;
                else if (raw == "Queue" || used.Overlaps(new[] { "offer", "poll", "offerLast", "pollFirst", "add", "remove" })) roles[name] = 2;
            }
            return roles;
        }

        // ---- library costs ---------------------------------------------------------------------------

        /// <summary>
        /// Charges the library calls between two tokens whose cost grows with the input — by the
        /// object's real type at run time, so a set's contains is never charged. Measuring only.
        /// </summary>
        private void Charges(int from, int to, IReadOnlyList<JvLambda> lambdas, List<JvVariable> visible)
        {
            if (_stepping || to < from) return;
            var strings = visible.Where(v => v.Type == "String").Select(v => v.Name).ToHashSet(StringComparer.Ordinal);

            // s += x on a String: a new string as long as both.
            if (_t[from].Kind == JvTokenKind.Identifier && strings.Contains(T(from)) && T(from + 1) == "+=" && T(to) == ";")
            {
                int group = NewGroup();
                int site = Site(_t[from + 1], "String +=", "O(length)");
                Insert(_t[from + 2].Start, $"Dsaviz.plus({site}, {T(from)}, ", 1, group);
                Insert(_t[to].Start, ")", 5, group);
            }

            for (int i = from; i <= to && i < _t.Count; i++)
            {
                if (lambdas.FirstOrDefault(l => l.Body is not null && i > l.Arrow && i <= l.BodyLast) is { } inside) { i = inside.BodyLast; continue; }
                if (T(i) == "{" && _t[i].Kind == JvTokenKind.Punctuation) { i = MatchBrace(i); continue; }       // an anonymous class
                var token = _t[i];

                // new ArrayList<>(other): a copy of the whole collection.
                if (T(i) == "new" && CopyTypes.Contains(T(i + 1)))
                {
                    int open = i + 2;
                    if (T(open) == "<") { while (open < to && T(open) != "(") open++; }
                    if (T(open) == "(" && Arguments(open) is [var only] && !only.Text.Contains("->") && !only.Text.Contains("::") &&
                        !char.IsDigit(only.Text[0]))
                        Wrap(only.Start, only.End, "copied", Site(_t[i + 1], $"new {T(i + 1)}(copy)", "O(n)"));
                    continue;
                }

                if (token.Kind != JvTokenKind.Identifier || T(i + 1) != "(") continue;
                string method = T(i);
                bool member = T(i - 1) == ".";
                int close = MatchParen(i + 1);
                if (close > to) continue;

                // Arrays.sort(a), Collections.sort(list): n log n.
                if (member && method == "sort" && T(i - 2) is "Arrays" or "Collections" && Arguments(i + 1) is [var array, ..])
                {
                    Wrap(array.Start, array.End, "sorted", Site(token, $"{T(i - 2)}.sort", "O(n log n)"));
                    continue;
                }
                if (!member) continue;
                int receiverEnd = i - 2;
                int receiverStart = ReceiverStart(receiverEnd);
                if (receiverStart < 0 || T(receiverStart) is "super") continue;
                var receiver = (_t[receiverStart].Start, _t[receiverEnd].End);
                string receiverText = _file.Text[receiver.Start..receiver.End];
                int count = Arguments(i + 1).Count;

                if (Scans.Contains(method) && count == 1)
                    Wrap(receiver.Start, receiver.End, "scan", Site(token, method, "O(n)"));
                else if (method == "sort" && T(receiverStart) is not ("Arrays" or "Collections"))
                    Wrap(receiver.Start, receiver.End, "sorted", Site(token, "List.sort", "O(n log n)"));
                else if (method is "stream" or "clone" && count == 0)
                    Wrap(receiver.Start, receiver.End, "copied", Site(token, method, "O(n)"));
                else if (Made.Contains(method) && T(receiverStart) is not ("Integer" or "Long" or "Character" or "Double"))
                    Wrap(receiver.Start, _t[close].End, "made", Site(token, method, "O(length)"));
                else if (method is "remove" or "deleteCharAt" && count == 1 && IsPlace(receiverStart, receiverEnd) ||
                         method is "add" or "insert" && count == 2 && IsPlace(receiverStart, receiverEnd))
                {
                    var first = Arguments(i + 1)[0];
                    int group = NewGroup();
                    int site = Site(token, $"{method} at an index", "O(n)");
                    Insert(first.Start, $"Dsaviz.at({site}, {receiverText}, ", 1, group);
                    Insert(first.End, ")", 5, group);
                }
            }
        }

        private void Wrap(int start, int end, string helper, int site)
        {
            int group = NewGroup();
            Insert(start, $"Dsaviz.{helper}({site}, ", 1, group);
            Insert(end, ")", 5, group);
        }

        private int Site(JvToken token, string member, string cost)
        {
            int site = Sites.Count;
            Sites.Add(new LibraryCallSite(site, token.Line, token.Column, member, cost));
            return site;
        }

        private int MatchParen(int open) => MatchBracket(open);

        private int MatchBrace(int open) => MatchBracket(open);

        private int MatchBracket(int open)
        {
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                if (_t[i].Kind != JvTokenKind.Punctuation) continue;
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}" && --depth == 0) return i;
            }
            return _t.Count - 1;
        }

        /// <summary>The arguments of the call whose '(' is at <paramref name="open"/>: each one's text and extent.</summary>
        private List<(int Start, int End, string Text)> Arguments(int open)
        {
            int close = MatchBracket(open);
            var arguments = new List<(int, int, string)>();
            int start = open + 1, depth = 0;
            for (int i = open + 1; i <= close; i++)
            {
                if (i < close && _t[i].Kind == JvTokenKind.Punctuation && T(i) is "(" or "[" or "{") depth++;
                else if (i < close && _t[i].Kind == JvTokenKind.Punctuation && T(i) is ")" or "]" or "}") depth--;
                else if (i == close || (depth == 0 && T(i) == ","))
                {
                    if (i > start)
                    {
                        int s = _t[start].Start, e = _t[i - 1].End;
                        arguments.Add((s, e, _file.Text[s..e]));
                    }
                    start = i + 1;
                }
            }
            return arguments;
        }

        private int ReceiverStart(int end)
        {
            int i = end;
            while (i >= 0)
            {
                if (T(i) is ")" or "]")
                {
                    int depth = 0;
                    for (; i >= 0; i--)
                    {
                        if (_t[i].Kind != JvTokenKind.Punctuation) continue;
                        if (T(i) is ")" or "]") depth++;
                        else if (T(i) is "(" or "[" && --depth == 0) break;
                    }
                    if (i > 0 && T(i) == "(" && _t[i - 1].Kind != JvTokenKind.Identifier) return i;
                    i--;
                    continue;
                }
                if (_t[i].Kind is JvTokenKind.Identifier or JvTokenKind.Text)
                {
                    if (T(i - 1) == ".") { i -= 2; continue; }
                    if (T(i - 1) == "new") return -1;
                    return i;
                }
                return -1;
            }
            return -1;
        }

        /// <summary>A receiver that can be read twice without changing anything: names, fields and plain indexes.</summary>
        private bool IsPlace(int from, int to)
        {
            for (int i = from; i <= to; i++)
                if (T(i) is "(" or ")" or "++" or "--" or "=") return false;
            return true;
        }
    }
}
