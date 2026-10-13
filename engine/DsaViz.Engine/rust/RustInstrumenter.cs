using System.Text;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Rust;

/// <summary>
/// A Rust program as a set of edits to the student's file, each of which can be left out:
/// a variable read the borrow checker rejects, a return value it cannot hold, a library
/// charge that does not type-check. <see cref="Render"/> writes the program without the
/// ones left out; the edits that are always needed — statement counts, frames — belong to
/// no group.
/// </summary>
public sealed class RustPlan
{
    public required string Student { get; init; }
    public required string Appended { get; init; }
    public required IReadOnlyList<RustEdit> Edits { get; init; }
    public required IReadOnlyList<StatementInfo> Statements { get; init; }
    public required IReadOnlyList<LibraryCallSite> Sites { get; init; }
    /// <summary>Each group's variable name, for a read; null for a return or a charge.</summary>
    public required IReadOnlyDictionary<int, (string Name, int Role)?> Groups { get; init; }

    /// <summary>Every step that reads variables, with where it sits in the code's structure.</summary>
    public required IReadOnlyList<RustStep> Steps { get; init; }
    public required RsFile File { get; init; }

    /// <summary>
    /// The reads of the same variable that will be rejected for the same reason as
    /// <paramref name="group"/>: the compiler reports a moved variable once per move, so each
    /// read after it would otherwise take another compile to find. They are the reads at the
    /// steps that run after this one — later in the same block, or inside what comes later —
    /// up to the first statement that assigns or declares the variable again.
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

    /// <summary>Whether <paramref name="to"/> runs after <paramref name="from"/> in the same run of the code, not in another branch.</summary>
    private static bool After(RustStep from, RustStep to)
    {
        int n = Math.Min(from.Path.Count, to.Path.Count);
        for (int k = 0; k < n; k++)
        {
            if (from.Path[k].Block != to.Path[k].Block) return false;      // different branches of one statement
            if (from.Path[k].Index != to.Path[k].Index) return to.Path[k].Index > from.Path[k].Index;
        }
        // One is inside the other's statement: what is inside runs after the step before it.
        return to.Path.Count > from.Path.Count;
    }

    /// <summary>
    /// Whether the student's own code uses the variable that read <paramref name="group"/>
    /// reads, at or after that step, in the same function: a borrow it holds is then still alive.
    /// </summary>
    public bool UsedLater(int group)
    {
        var step = Steps.FirstOrDefault(s => s.Groups.Values.Contains(group));
        if (step is null || Groups[group] is not { } variable) return true;
        var t = File.Tokens;
        // A field, self.items, is used where self . items is written.
        string name = variable.Name.StartsWith("self.", StringComparison.Ordinal) ? variable.Name[5..] : variable.Name;
        bool field = name != variable.Name;
        for (int i = step.FirstToken; i <= step.RootLast && i < t.Count; i++)
        {
            if (t[i].Kind != RsTokenKind.Identifier || t[i].Text != name) continue;
            if (field ? i >= 2 && t[i - 1].Text == "." && t[i - 2].Text == "self" : i == 0 || t[i - 1].Text is not ("." or "::")) return true;
        }
        return false;
    }

    private bool SetBetween(string name, int fromToken, int toToken)
    {
        var t = File.Tokens;
        for (int i = fromToken; i < toToken && i < t.Count; i++)
        {
            if (t[i].Text != name || t[i].Kind != RsTokenKind.Identifier) continue;
            string next = i + 1 < t.Count ? t[i + 1].Text : "";
            string previous = i > 0 ? t[i - 1].Text : "";
            if (next == "=" || previous is "let" or "mut" or "for" or "ref") return true;
        }
        return false;
    }

    /// <summary>The program without the groups in <paramref name="left"/>, and where each group's text landed.</summary>
    public (string Source, Dictionary<int, List<(int Start, int End)>> Placed) Render(IReadOnlyDictionary<int, string> left)
    {
        var text = new StringBuilder(Student.Length + Edits.Count * 60 + Appended.Length);
        var placed = new Dictionary<int, List<(int, int)>>();
        int at = 0;
        foreach (var edit in Edits.OrderBy(e => e.Start).ThenBy(e => e.Rank).ThenBy(e => e.Sequence))
        {
            text.Append(Student, at, edit.Start - at);
            at = Math.Max(at, edit.End);
            string inserted = edit.Text;
            if (edit.Group is { } group && left.TryGetValue(group, out var why))
            {
                // A read left out still lists the variable, saying why it cannot be read.
                inserted = Groups[group] is { } variable
                    ? $"dsaviz::v(\"{variable.Name}\", {variable.Role}, dsaviz::Cap::Gone(\"{why}\"))"
                    : edit.Group is not null && edit.Start != edit.End ? Student[edit.Start..edit.End] : "";
            }
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
}

/// <summary>A step: the function or closure it is in, where it sits — each enclosing block and the statement's position in it — and its reads.</summary>
public sealed record RustStep(int Id, int Root, IReadOnlyList<(int Block, int Index)> Path, int FirstToken, IReadOnlyDictionary<string, int> Groups)
{
    /// <summary>The last token of the function or closure the step is in.</summary>
    public int RootLast { get; init; }
}

/// <summary>Insert <see cref="Text"/> at <see cref="Start"/>, or replace up to <see cref="End"/>. Ordered by offset, then rank.</summary>
public sealed record RustEdit(int Start, int End, string Text, int Rank, int Sequence, int? Group);

/// <summary>
/// Rewrites a Rust solution so it reports itself, and adds a main that reads the inputs,
/// calls the solution and writes what it returned. Every insertion goes inside an existing
/// line, so a compiler error, a panic or a step names the line the student sees. Two
/// builds, as for C# and C++: one counts statements and charges library calls, for
/// measuring; the other records every variable at every step, for stepping through.
/// </summary>
public static class RustInstrumenter
{
    public const string ProgramFile = "program.rs";

    private const string TreeNodeDefinition = """
        #[derive(Debug, PartialEq, Eq)]
        pub struct TreeNode { pub val: i32, pub left: Option<std::rc::Rc<std::cell::RefCell<TreeNode>>>, pub right: Option<std::rc::Rc<std::cell::RefCell<TreeNode>>> }
        impl TreeNode { #[inline] pub fn new(val: i32) -> Self { TreeNode { val, left: None, right: None } } }
        """;
    private const string ListNodeDefinition = """
        #[derive(PartialEq, Eq, Clone, Debug)]
        pub struct ListNode { pub val: i32, pub next: Option<Box<ListNode>> }
        impl ListNode { #[inline] fn new(val: i32) -> Self { ListNode { next: None, val } } }
        """;
    private const string TreeShape = """
        impl dsaviz::TreeShape for TreeNode {
            fn make(val: i32) -> Self { TreeNode { val, left: None, right: None } }
            fn val(&self) -> i32 { self.val }
            fn left(&self) -> &Option<std::rc::Rc<std::cell::RefCell<Self>>> { &self.left }
            fn right(&self) -> &Option<std::rc::Rc<std::cell::RefCell<Self>>> { &self.right }
            fn set_left(&mut self, node: Option<std::rc::Rc<std::cell::RefCell<Self>>>) { self.left = node; }
            fn set_right(&mut self, node: Option<std::rc::Rc<std::cell::RefCell<Self>>>) { self.right = node; }
        }
        """;
    private const string ListShape = """
        impl dsaviz::ListShape for ListNode {
            fn make(val: i32) -> Self { ListNode { val, next: None } }
            fn val(&self) -> i32 { self.val }
            fn next(&self) -> &Option<Box<Self>> { &self.next }
            fn set_next(&mut self, node: Option<Box<Self>>) { self.next = node; }
        }
        """;

    /// <summary>The program with nothing inserted: compiled to find the student's own errors.</summary>
    public static string Plain(RsFile file, RsFunction? entry, string? design = null)
    {
        var plan = Build(file, entry, stepping: false, instrument: false, design);
        return plan.Render(new Dictionary<int, string>()).Source;
    }

    /// <param name="design">For a design problem, its struct: the program then makes the calls its example lists.</param>
    public static RustPlan Build(RsFile file, RsFunction? entry, bool stepping, bool instrument = true, string? design = null)
    {
        var rewriter = new Rewriter(file, stepping);
        if (instrument) rewriter.Instrument();
        rewriter.RenameMain();
        return new RustPlan
        {
            Student = file.Text,
            Appended = Appended(file, entry, design, rewriter.StatementCount, rewriter.SiteCount),
            Edits = rewriter.Edits,
            Statements = rewriter.Statements,
            Sites = rewriter.Sites,
            Groups = rewriter.Groups,
            Steps = rewriter.Steps,
            File = file
        };
    }

    /// <summary>What goes after the student's code: imports, LeetCode's types where the file leaves them out, and main.</summary>
    private static string Appended(RsFile file, RsFunction? entry, string? design, int statements, int sites)
    {
        var a = new StringBuilder("\n\n// ---- added by DSA Playback ----\n");
        a.Append("#[allow(unused_imports)] use dsaviz::prelude::*;\n");
        // Globs: an import the student wrote takes precedence over them, so nothing clashes.
        a.Append("#[allow(unused_imports)] use std::collections::*;\n");
        a.Append("#[allow(unused_imports)] use std::rc::*;\n");
        a.Append("#[allow(unused_imports)] use std::cell::*;\n");

        var defined = file.Structs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var used = file.Tokens.Where(t => t.Kind == RsTokenKind.Identifier).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        if (!defined.Contains("Solution")) a.Append("pub struct Solution;\n");
        if (used.Contains("TreeNode") && !defined.Contains("TreeNode")) a.Append(TreeNodeDefinition).Append('\n');
        if (used.Contains("ListNode") && !defined.Contains("ListNode")) a.Append(ListNodeDefinition).Append('\n');
        bool HasFields(string name, params string[] fields)
            => !defined.Contains(name) || file.Structs.Any(s => s.Name == name && fields.All(s.Fields.Contains));
        if (used.Contains("TreeNode") && HasFields("TreeNode", "val", "left", "right")) a.Append(TreeShape).Append('\n');
        if (used.Contains("ListNode") && HasFields("ListNode", "val", "next")) a.Append(ListShape).Append('\n');

        a.Append(design is not null ? DesignDriver(file, design, statements, sites) : Driver(entry!, statements, sites));
        return a.ToString();
    }

    /// <summary>
    /// Reads every input, calls the entry and writes its result. Inputs are built before
    /// counting starts; a panic in the student's code is caught and reported with its line.
    /// </summary>
    private static string Driver(RsFunction entry, int statements, int sites)
    {
        var d = new StringBuilder();
        d.Append("fn main() {\n");
        d.Append($"    dsaviz::configure({statements}, {sites}, \"{ProgramFile}\");\n");
        d.Append("    let dsaviz_inputs = dsaviz::read_inputs();\n");
        d.Append("    dsaviz::deep(move || {\n");
        var arguments = new List<string>();
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            var (owned, pass) = Passing(entry.Parameters[i].Type);
            d.Append($"        #[allow(unused_mut)] let mut dsaviz_a{i}: {owned} = dsaviz::input(&dsaviz_inputs, {i});\n");
            arguments.Add(pass.Replace("ARG", $"dsaviz_a{i}"));
        }
        string call = $"{(entry.Owner is { } owner ? owner + "::" : "")}{entry.Name}({string.Join(", ", arguments)})";
        d.Append("        dsaviz::begin();\n");
        d.Append($"        match std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {call})) {{\n");
        d.Append(IsUnit(entry.ReturnType)
            ? "            Ok(_) => dsaviz::succeed(None),\n"
            : "            Ok(dsaviz_result) => dsaviz::succeed(Some((&&&dsaviz::W(&dsaviz_result)).result_text())),\n");
        d.Append("            Err(_) => dsaviz::panicked(),\n");
        d.Append("        }\n    });\n}\n");
        return d.ToString();
    }

    /// <summary>
    /// A design problem's calls. Rust cannot find a method by its name at run time, so this
    /// writes the matching out: `new` for the first operation, then each method taking self
    /// by reference, by name and argument count, each argument built as the type it declares.
    /// </summary>
    private static string DesignDriver(RsFile file, string type, int statements, int sites)
    {
        var functions = file.Functions.Where(f => f.Owner == type && !f.IsNested && !f.IsGeneric).ToList();
        var constructor = functions.FirstOrDefault(f => f.Name == "new" && !f.HasSelf);
        var methods = functions.Where(f => f.HasSelf && !f.SelfByValue).ToList();

        string Build(RsFunction f, string indent, StringBuilder into)
        {
            var passed = new List<string>();
            for (int k = 0; k < f.Parameters.Count; k++)
            {
                var (owned, pass) = Passing(f.Parameters[k].Type);
                into.Append($"{indent}#[allow(unused_mut)] let mut p{k}: {owned} = dsaviz::arg(&dsaviz_a[{k}]);\n");
                passed.Add(pass.Replace("ARG", $"p{k}"));
            }
            return string.Join(", ", passed);
        }

        var d = new StringBuilder();
        d.Append("fn main() {\n");
        d.Append($"    dsaviz::configure({statements}, {sites}, \"{ProgramFile}\");\n");
        d.Append("    let dsaviz_inputs = dsaviz::read_inputs();\n");
        d.Append("    dsaviz::deep(move || {\n");
        d.Append($"        let (dsaviz_ops, dsaviz_args) = dsaviz::design_inputs(&dsaviz_inputs, \"{type}\");\n");
        d.Append($"        dsaviz::design_validate(&dsaviz_ops, &dsaviz_args, \"{type}\", {constructor?.Parameters.Count ?? 0}, &[");
        d.Append(string.Join(", ", methods.Select(m => $"(\"{m.Name}\", {m.Parameters.Count})")));
        d.Append("]);\n");
        d.Append("        dsaviz::begin();\n");
        d.Append("        match std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {\n");
        d.Append($"            let mut dsaviz_target: Option<{type}> = None;\n");
        d.Append("            for (dsaviz_i, dsaviz_op) in dsaviz_ops.iter().enumerate() {\n");
        d.Append("                dsaviz::call_starting(dsaviz_i);\n");
        d.Append("                let dsaviz_a = &dsaviz_args[dsaviz_i];\n");
        d.Append("                if dsaviz_i == 0 {\n");
        int takes = constructor?.Parameters.Count ?? 0;
        d.Append($"                    if dsaviz_a.len() != {takes} {{ dsaviz::invalid_constructor(\"{type}\", dsaviz_a.len(), {takes}); }}\n");
        if (constructor is not null)
        {
            string args = Build(constructor, "                    ", d);
            d.Append($"                    dsaviz_target = Some({type}::new({args}));\n");
        }
        else d.Append($"                    dsaviz::invalid(\"{type} has no fn new to create it with.\");\n");
        d.Append("                    dsaviz::result_none();\n                    continue;\n                }\n");
        d.Append("                let dsaviz_o = dsaviz_target.as_mut().unwrap();\n");
        d.Append("                match (dsaviz_op.as_str(), dsaviz_a.len()) {\n");
        foreach (var m in methods)
        {
            d.Append($"                    (\"{m.Name}\", {m.Parameters.Count}) => {{\n");
            string args = Build(m, "                        ", d);
            d.Append(IsUnit(m.ReturnType)
                ? $"                        dsaviz_o.{m.Name}({args});\n                        dsaviz::result_none();\n"
                : $"                        let dsaviz_r = dsaviz_o.{m.Name}({args});\n                        dsaviz::result(|| (&&&dsaviz::W(&dsaviz_r)).result_text());\n");
            d.Append("                    }\n");
            d.Append($"                    (\"{m.Name}\", dsaviz_n) => dsaviz::invalid_arity(dsaviz_i, \"{m.Name}\", dsaviz_n, {m.Parameters.Count}),\n");
        }
        d.Append($"                    (dsaviz_other, _) => dsaviz::invalid_operation(dsaviz_i, dsaviz_other, \"{type}\"),\n");
        d.Append("                }\n            }\n        })) {\n");
        d.Append("            Ok(_) => dsaviz::succeed_results(),\n");
        d.Append("            Err(_) => dsaviz::panicked(),\n        }\n    });\n}\n");
        return d.ToString();
    }

    private static bool IsUnit(string? type) => type is null || type.Replace(" ", "") == "()";

    /// <summary>
    /// The type an argument is built as, and how it is passed: "&amp;mut Vec&lt;i32&gt;" is a
    /// Vec&lt;i32&gt; passed as &amp;mut; "&amp;[i32]" a Vec&lt;i32&gt; passed by reference; "&amp;str" a String.
    /// </summary>
    public static (string Owned, string Pass) Passing(string written)
    {
        string type = System.Text.RegularExpressions.Regex.Replace(written.Trim(), @"'\w+\s*", "").Trim();
        string pass = "ARG";
        if (type.StartsWith("&mut ", StringComparison.Ordinal)) { pass = "&mut ARG"; type = type[5..].Trim(); }
        else if (type.StartsWith("&", StringComparison.Ordinal)) { pass = "&ARG"; type = type[1..].Trim(); }
        if (type.StartsWith('[') && type.EndsWith(']') && !type.Contains(';')) type = $"Vec<{type[1..^1].Trim()}>";
        if (type == "str") type = "String";
        return (type, pass);
    }

    // ---- the rewriter ---------------------------------------------------------------------------

    private static readonly HashSet<string> IteratorSources = new(StringComparer.Ordinal)
    {
        "iter", "iter_mut", "into_iter", "chars", "bytes", "char_indices", "keys", "values", "values_mut", "windows", "chunks"
    };

    private static readonly HashSet<string> StackMethods = new(StringComparer.Ordinal)
    {
        "push", "pop", "last", "last_mut", "is_empty", "len", "clear"
    };

    private sealed class Rewriter
    {
        private readonly RsFile _file;
        private readonly IReadOnlyList<RsToken> _t;
        private readonly bool _stepping;
        public List<RustEdit> Edits { get; } = new();
        public List<StatementInfo> Statements { get; } = new();
        public List<LibraryCallSite> Sites { get; } = new();
        public Dictionary<int, (string Name, int Role)?> Groups { get; } = new();
        public List<RustStep> Steps { get; } = new();
        private string _method = "";
        private int _ids;
        private int _root;
        private int _rootLast;
        private List<(int Block, int Index)> _path = new();
        private HashSet<string> _stacks = new(StringComparer.Ordinal);

        public Rewriter(RsFile file, bool stepping)
        {
            _file = file;
            _t = file.Tokens;
            _stepping = stepping;
        }

        public int StatementCount => Statements.Count;
        public int SiteCount => Sites.Count;

        private string T(int i) => i >= 0 && i < _t.Count ? _t[i].Text : "";

        private void Insert(int offset, string text, int rank, int? group = null)
            => Edits.Add(new RustEdit(offset, offset, text, rank, Edits.Count, group));

        private int NewGroup((string, int)? variable = null)
        {
            int id = Groups.Count;
            Groups[id] = variable;
            return id;
        }

        /// <summary>The student's own main is renamed, so the driver's is the one that runs.</summary>
        public void RenameMain()
        {
            if (_file.Functions.FirstOrDefault(f => f.Name == "main" && f.Owner is null && !f.IsNested) is { } main)
            {
                var name = _t[main.NameToken];
                Edits.Add(new RustEdit(name.Start, name.End, "dsaviz_student_main", 0, Edits.Count, null));
            }
        }

        public void Instrument()
        {
            foreach (var function in _file.Functions)
            {
                // A const fn cannot call the counters; main is where examples are called from.
                if (function.IsConst || (function.Name == "main" && function.Owner is null && !function.IsNested)) continue;
                _method = function.Name;
                _stacks = StackLike(function);
                // A method taking self can see its fields: a design problem's state is there.
                var fields = function.HasSelf && function.Owner is { } owner
                    ? _file.Structs.Where(s => s.Name == owner).SelectMany(s => s.Fields).Select(f => $"self.{f}")
                    : Enumerable.Empty<string>();
                var visible = fields.Concat(function.Parameters.Select(p => p.Name)).ToList();
                Body(function.Body, visible, function.ReturnType is { } r && r.Replace(" ", "") != "()");
            }
        }

        // ---- statements --------------------------------------------------------------------------

        private void Body(RsStatement body, List<string> visible, bool returnsValue)
        {
            Insert(_t[body.First].End, " let _dsaviz_frame = dsaviz::Frame::enter();", -5);
            // A function or closure is a world of its own: what is moved in one is not moved in another.
            var (savedRoot, savedPath, savedLast) = (_root, _path, _rootLast);
            (_root, _path, _rootLast) = (++_ids, new List<(int, int)>(), body.Last);
            Block(body, visible);
            (_root, _path, _rootLast) = (savedRoot, savedPath, savedLast);
            if (_stepping && returnsValue && body.Children.LastOrDefault() is { IsTail: true } tail && T(SkipLabel(tail.First)) != "return")
                Returned(_t[tail.First].Start, _t[tail.Last].End);
        }

        private int SkipLabel(int i)
        {
            while (T(i) == "#") i = MatchBracket(i + 1) + 1;
            return i;
        }

        private int MatchBracket(int open)
        {
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                if (_t[i].Kind != RsTokenKind.Punctuation) continue;
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}" && --depth == 0) return i;
            }
            return _t.Count - 1;
        }

        private void Block(RsStatement block, List<string> outer)
        {
            var scope = new List<string>(outer);
            int id = ++_ids;
            for (int i = 0; i < block.Children.Count; i++)
            {
                var child = block.Children[i];
                _path.Add((id, i));
                Statement(child, scope);
                _path.RemoveAt(_path.Count - 1);
                foreach (var name in child.Declares) { scope.Remove(name); scope.Add(name); }
            }
        }

        /// <summary>Runs <paramref name="action"/> as the only statement of a block of its own.</summary>
        private void Alone(Action action)
        {
            _path.Add((++_ids, 0));
            action();
            _path.RemoveAt(_path.Count - 1);
        }

        private static List<string> With(List<string> visible, IEnumerable<string> added)
        {
            var scope = new List<string>(visible);
            foreach (var name in added) { scope.Remove(name); scope.Add(name); }
            return scope;
        }

        private void Statement(RsStatement statement, List<string> visible)
        {
            if (statement.Kind is RsStatementKind.Empty or RsStatementKind.Item) return;

            Step(_t[statement.First].Start, statement.First, Snippet(statement), visible);

            switch (statement.Kind)
            {
                case RsStatementKind.Block:
                    Block(statement, visible);
                    break;
                case RsStatementKind.Let:
                    Closures(statement.Closures, visible);
                    foreach (var child in statement.Children) Block(child, visible);
                    Charges(statement.First, statement.Last, statement);
                    break;
                case RsStatementKind.Expression:
                    Closures(statement.Closures, visible);
                    if (_stepping && T(SkipLabel(statement.First)) == "return") Return(SkipLabel(statement.First), statement.Last);
                    Charges(statement.First, statement.Last, statement);
                    break;
                case RsStatementKind.If:
                    If(statement, visible);
                    break;
                case RsStatementKind.While:
                case RsStatementKind.For:
                case RsStatementKind.Loop:
                    Closures(statement.Closures, visible);
                    Block(statement.Children[0], With(visible, statement.BodyDeclares));
                    if (statement.Kind == RsStatementKind.While) Charges(statement.First, statement.HeaderEnd, statement);
                    break;
                case RsStatementKind.Match:
                    Closures(statement.Closures, visible);
                    Charges(statement.First, statement.HeaderEnd, statement);
                    foreach (var arm in statement.Arms) Arm(arm, visible);
                    break;
            }
        }

        private void If(RsStatement statement, List<string> visible)
        {
            Closures(statement.Closures, visible);
            Charges(statement.First, statement.HeaderEnd, statement);
            Block(statement.Children[0], With(visible, statement.BodyDeclares));
            if (statement.Else is { Kind: RsStatementKind.If } otherwise)
            {
                // else if: the next condition is a step of its own, so it gets a block to stand in.
                Insert(_t[otherwise.First].Start, "{ ", -20);
                Insert(_t[otherwise.Last].End, " }", 20);
                Alone(() =>
                {
                    Step(_t[otherwise.First].Start, otherwise.First, Snippet(otherwise), visible);
                    If(otherwise, visible);
                });
            }
            else if (statement.Else is { } block) Block(block, visible);
        }

        private void Arm(RsArm arm, List<string> visible)
        {
            var inside = With(visible, arm.Bindings);
            Closures(arm.Closures, inside);
            if (arm.Block is { } block)
            {
                Block(block, inside);
                return;
            }
            // An arm whose body is an expression: a block is made for its step to stand in.
            Insert(_t[arm.BodyFirst].Start, "{ ", -20);
            Insert(_t[arm.BodyLast].End, " }", 20);
            Alone(() => Step(_t[arm.BodyFirst].Start, arm.BodyFirst, SnippetOf(arm.BodyFirst, arm.BodyLast), inside));
            if (_stepping && T(arm.BodyFirst) == "return") Return(arm.BodyFirst, arm.BodyLast + 1);
            Charges(arm.BodyFirst, arm.BodyLast, null);
        }

        private void Closures(IEnumerable<RsClosure> closures, List<string> visible)
        {
            foreach (var closure in closures)
            {
                if (closure.Body is null) continue;
                // Only what the closure itself mentions: reading anything else inside it would make
                // the closure borrow it, which the student's code never asked for.
                var inside = With(visible.Where(closure.Mentioned.Contains).ToList(), closure.Parameters);
                string saved = _method;
                _method = closure.Name ?? $"closure in {saved}";
                Body(closure.Body, inside, returnsValue: false);
                _method = saved;
            }
        }

        // ---- steps -----------------------------------------------------------------------------

        private void Step(int offset, int firstToken, string snippet, List<string> visible)
        {
            int id = Statements.Count;
            var first = _t[firstToken];
            var names = visible.Distinct().ToList();
            Statements.Add(new StatementInfo(id, first.Line, first.Column, snippet.Length, snippet)
            {
                Method = _method,
                Variables = _stepping ? names : Array.Empty<string>()
            });

            if (!_stepping)
            {
                Insert(offset, $"dsaviz::hit({id}); ", -10);
                return;
            }
            Insert(offset, $"dsaviz::step({id}, &[", -10);
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                int role = _stacks.Contains(names[i]) ? 1 : 0;
                int group = NewGroup((names[i], role));
                groups[names[i]] = group;
                Insert(offset, $"{(i > 0 ? ", " : "")}", -10);
                Insert(offset, $"dsaviz::v(\"{names[i]}\", {role}, (&&&dsaviz::W(&{names[i]})).cap())", -10, group);
            }
            Insert(offset, "]); ", -10);
            Steps.Add(new RustStep(id, _root, _path.ToList(), firstToken, groups) { RootLast = _rootLast });
        }

        private string Snippet(RsStatement statement)
        {
            int last = statement.Kind switch
            {
                RsStatementKind.If or RsStatementKind.While or RsStatementKind.For or RsStatementKind.Match => statement.HeaderEnd,
                RsStatementKind.Loop or RsStatementKind.Block => statement.First,
                _ => statement.Last
            };
            return SnippetOf(statement.First, Math.Max(statement.First, last));
        }

        private string SnippetOf(int from, int to)
        {
            string text = _file.Text[_t[from].Start.._t[to].End];
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text[..newline].TrimEnd();
            return text.Length > 80 ? text[..80] + "…" : text;
        }

        /// <summary>return x; → return { let dsaviz_r = x; dsaviz::returned(..); dsaviz_r };</summary>
        private void Return(int returnToken, int endToken)
        {
            int expression = returnToken + 1;
            if (expression >= endToken || T(expression) is ";" or "," or "}") return;
            int end = T(endToken) == ";" || endToken >= _t.Count ? (endToken < _t.Count ? _t[endToken].Start : _file.Text.Length) : _t[endToken - 1].End;
            Returned(_t[expression].Start, end);
        }

        private void Returned(int start, int end)
        {
            int group = NewGroup();
            Insert(start, "{ let dsaviz_r = ", 0, group);
            Insert(end, "; dsaviz::returned((&&&dsaviz::W(&dsaviz_r)).cap()); dsaviz_r }", 10, group);
        }

        // ---- stacks -----------------------------------------------------------------------------

        /// <summary>Vecs this function only pushes onto and pops from: drawn as stacks.</summary>
        private HashSet<string> StackLike(RsFunction function)
        {
            var methods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var other = new HashSet<string>(StringComparer.Ordinal);
            var created = new HashSet<string>(StringComparer.Ordinal);
            for (int i = function.Body.First; i <= function.Body.Last; i++)
            {
                if (_t[i].Kind != RsTokenKind.Identifier) continue;
                string name = T(i);
                if (T(i - 1) == "let" || (T(i - 1) == "mut" && T(i - 2) == "let"))
                {
                    int k = i + 1;
                    while (k < _t.Count && T(k) is not "=" and not ";") k++;
                    if (T(k) == "=" && (T(k + 1) == "vec" && T(k + 2) == "!" || T(k + 1) == "Vec" && T(k + 2) == "::")) created.Add(name);
                    continue;
                }
                if (T(i - 1) is "." or "::") continue;
                if (T(i + 1) == "." && i + 2 < _t.Count && _t[i + 2].Kind == RsTokenKind.Identifier && T(i + 3) == "(")
                {
                    if (!methods.TryGetValue(name, out var set)) methods[name] = set = new HashSet<string>();
                    set.Add(T(i + 2));
                }
                else if (T(i + 1) is "[" or ".") other.Add(name);
            }
            return created.Where(n => methods.TryGetValue(n, out var used) && used.Contains("push") && used.Contains("pop") &&
                                      used.All(StackMethods.Contains) && !other.Contains(n)).ToHashSet(StringComparer.Ordinal);
        }

        // ---- library costs -----------------------------------------------------------------------

        /// <summary>
        /// Charges the library calls between two tokens whose cost grows with the input.
        /// Measuring only: stepping shows what ran, not what it cost. Everything about the
        /// type is decided when the program is compiled, so a set's contains is never charged.
        /// </summary>
        private void Charges(int from, int to, RsStatement? statement)
        {
            if (_stepping || to < from) return;
            var skip = (statement?.Closures ?? new List<RsClosure>()).Where(c => c.Body is not null).ToList();
            foreach (var child in statement?.Children ?? new List<RsStatement>()) skip.Add(new RsClosure
                { Open = child.First, Close = child.Last, Parameters = Array.Empty<string>(), Mentioned = new HashSet<string>() });

            for (int i = from; i <= to && i < _t.Count; i++)
            {
                if (skip.FirstOrDefault(c => i >= c.Open && i <= c.Close) is { } inside) { i = inside.Close; continue; }
                if (_t[i].Kind != RsTokenKind.Identifier || T(i - 1) != "." || T(i + 1) != "(") continue;
                string method = T(i);
                int close = MatchBracket(i + 1);
                int receiverEnd = i - 2;
                int receiverStart = ReceiverStart(receiverEnd);
                if (receiverStart < 0 || close > to) continue;
                string receiver = _file.Text[_t[receiverStart].Start.._t[receiverEnd].End];
                bool place = IsPlace(receiverStart, receiverEnd);
                int arguments = ArgumentCount(i + 1, close);
                var token = _t[i];

                switch (method)
                {
                    case "contains" or "find" when place && arguments == 1:
                        After(receiverStart, close, Site(token, method, "O(n)"), receiver, "scan_cost");
                        break;
                    case "sort" or "sort_unstable" or "sort_by" or "sort_by_key" or "sort_unstable_by" or "sort_unstable_by_key" when place:
                        After(receiverStart, close, Site(token, method, "O(n log n)"), receiver, "sort_cost");
                        break;
                    case "clone" or "to_vec" or "to_owned" when place && arguments == 0:
                        After(receiverStart, close, Site(token, method, "O(n)"), receiver, "copy_cost");
                        break;
                    case "reverse" or "retain" or "dedup" or "join" or "concat" when place:
                        After(receiverStart, close, Site(token, method, "O(n)"), receiver, "copy_cost");
                        break;
                    case "insert" when place && arguments == 2 && !receiver.Contains('['):
                    case "remove" when place && arguments == 1 && !receiver.Contains('['):
                    {
                        int site = Site(token, $"Vec::{method}", "O(n)");
                        int group = NewGroup();
                        int firstEnd = ArgumentEnd(i + 1, close);
                        Insert(_t[i + 1].End, $"(&&&dsaviz::W(&{receiver})).shifter().at({site}, ", 1, group);
                        Insert(_t[firstEnd].End, ")", 5, group);
                        break;
                    }
                    case var source when IteratorSources.Contains(source) && T(close + 1) == "." && statement?.Kind != RsStatementKind.For:
                    {
                        int site = Site(token, $".{method}() chain", "O(n)");
                        int group = NewGroup();
                        Insert(_t[receiverStart].Start, $"dsaviz::counted({site}, ", 1, group);
                        Insert(_t[close].End, ")", 5, group);
                        break;
                    }
                }
            }
        }

        private void After(int receiverStart, int close, int site, string receiver, string cost)
        {
            int group = NewGroup();
            Insert(_t[receiverStart].Start, $"dsaviz::after(dsaviz::charge({site}, (&&&dsaviz::W(&{receiver})).{cost}()), ", 1, group);
            Insert(_t[close].End, ")", 5, group);
        }

        private int Site(RsToken token, string member, string cost)
        {
            int site = Sites.Count;
            Sites.Add(new LibraryCallSite(site, token.Line, token.Column, member, cost));
            return site;
        }

        /// <summary>Where the receiver ending at <paramref name="end"/> starts: v, self.v, grid[i], s.chars().</summary>
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
                        if (_t[i].Kind != RsTokenKind.Punctuation) continue;
                        if (T(i) is ")" or "]") depth++;
                        else if (T(i) is "(" or "[" && --depth == 0) break;
                    }
                    // A parenthesised receiver — (a + b).abs() — has nothing before it to join.
                    if (i > 0 && T(i) == "(" && _t[i - 1].Kind != RsTokenKind.Identifier) return i;
                    i--;
                    continue;
                }
                if (T(i) == "?") { i--; continue; }
                if (_t[i].Kind is RsTokenKind.Identifier or RsTokenKind.Number or RsTokenKind.Text)
                {
                    if (T(i - 1) is "." or "::") { i -= 2; continue; }
                    return _t[i].Kind == RsTokenKind.Number && T(i - 1) != "." ? -1 : i;
                }
                return -1;
            }
            return -1;
        }

        /// <summary>A receiver that can be read twice without changing anything: names, fields, and plain indexes.</summary>
        private bool IsPlace(int from, int to)
        {
            for (int i = from; i <= to; i++)
            {
                if (_t[i].Kind == RsTokenKind.Identifier && RustReader.IsKeyword(T(i)) && T(i) != "self") return false;
                if (T(i) is "(" or ")" or "?" or "!" or "::") return false;
            }
            return true;
        }

        private int ArgumentCount(int open, int close)
        {
            if (close == open + 1) return 0;
            int count = 1, depth = 0;
            for (int i = open + 1; i < close; i++)
            {
                if (_t[i].Kind != RsTokenKind.Punctuation) continue;
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}") depth--;
                else if (depth == 0 && T(i) == ",") count++;
            }
            return count;
        }

        private int ArgumentEnd(int open, int close)
        {
            int depth = 0;
            for (int i = open + 1; i < close; i++)
            {
                if (_t[i].Kind != RsTokenKind.Punctuation) continue;
                if (T(i) is "(" or "[" or "{") depth++;
                else if (T(i) is ")" or "]" or "}") depth--;
                else if (depth == 0 && T(i) == ",") return i - 1;
            }
            return close - 1;
        }
    }
}
