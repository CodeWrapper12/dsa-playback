using DsaViz.Engine.Contracts;
using DsaViz.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Reads a complexity class from the shape of the code, without running it.
///
/// This is a heuristic and is treated as one. Deciding the complexity of an arbitrary
/// program is not possible in general, so the design goal is honesty about the boundary:
/// anything not recognised is reported as Unknown, with the reason, rather than resolved
/// to the nearest plausible label. A confidently wrong Big-O in a tool someone is
/// learning from is worse than an admission of ignorance, because it teaches the wrong
/// lesson with authority.
///
/// Calls are resolved through the semantic model, never by name, so overloads, local
/// functions, and same-named methods on different types are told apart.
/// </summary>
public static class StaticEstimator
{
    private sealed record Shape(int Linear, int Log);

    private sealed class Context
    {
        public required SemanticModel Model { get; init; }
        public required CostCallAnalysis Costs { get; init; }
        public required Dictionary<IMethodSymbol, SyntaxNode> Bodies { get; init; }
    }

    public static StaticEstimate Estimate(string source, string entryMethod)
        => Estimate(SourceAnalysis.Create(source), entryMethod);

    public static StaticEstimate Estimate(SourceAnalysis analysis, string entryMethod)
    {
        var model = analysis.Model;
        var root = analysis.Tree.GetRoot();
        var bodies = new Dictionary<IMethodSymbol, SyntaxNode>(SymbolEqualityComparer.Default);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            if (model.GetDeclaredSymbol(method) is { } symbol && BodyOf(method.Body, method.ExpressionBody) is { } body)
                bodies[symbol] = body;

        foreach (var function in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            if (model.GetDeclaredSymbol(function) is IMethodSymbol symbol && BodyOf(function.Body, function.ExpressionBody) is { } body)
                bodies[symbol] = body;

        string? typeName = null;
        string methodName = entryMethod;
        int dot = entryMethod.LastIndexOf('.');
        if (dot > 0) { typeName = entryMethod[..dot]; methodName = entryMethod[(dot + 1)..]; }

        var entries = bodies.Keys
            .Where(s => s.MethodKind == MethodKind.Ordinary && s.Name == methodName &&
                        (typeName is null || s.ContainingType?.Name == typeName))
            .ToList();

        if (entries.Count == 0)
            return new StaticEstimate(ComplexityClass.Unknown, $"No method named '{entryMethod}' was found to analyse.", 0, 0, 0);
        if (entries.Count > 1)
            return new StaticEstimate(ComplexityClass.Unknown, $"'{entryMethod}' is ambiguous; name it as Type.Method.", 0, 0, 0);

        var context = new Context { Model = model, Costs = analysis.CostCalls, Bodies = bodies };
        var entry = entries[0];

        // Recursion is looked for across everything the entry reaches, not just the entry
        // itself. MergeSort.Sort does not call itself — it calls SortRange, which does —
        // and an estimator that only inspected the entry saw a plain loop.
        var recursion = FindRecursion(entry, context);
        var amortisation = new Amortisation();
        var (shape, because) = Analyse(entry, context, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { entry }, amortisation);

        var estimate = Classify(shape, because, recursion);

        if (amortisation.Pointers.Count > 0 && estimate.Class != ComplexityClass.Unknown)
            estimate = estimate with
            {
                Reasoning = estimate.Reasoning + " Amortised: " + string.Join("; ", amortisation.Pointers.Distinct()) + "."
            };

        // A traversal's nested loops are bounded together, not multiplied. Loop nesting
        // would call a BFS O(n³); reporting that would be confidently wrong, so the depth
        // reading is withheld rather than trusted.
        if (amortisation.Worklist is { } worklist && shape.Linear >= 2 && recursion.Kind == RecursionKind.None)
            estimate = new StaticEstimate(ComplexityClass.Unknown,
                $"Contains a worklist loop, {worklist}: a BFS or DFS-style traversal. Each node goes through the " +
                "worklist once in total, so the loops around and inside it are amortised — usually O(V + E) — " +
                $"rather than multiplied. Loop nesting alone would have said {loopsText(shape)}, which overstates it.",
                shape.Linear, shape.Log, 0);

        return estimate;

        static string loopsText(Shape s) => $"depth {s.Linear}";
    }

    private static SyntaxNode? BodyOf(BlockSyntax? block, ArrowExpressionClauseSyntax? arrow)
        => (SyntaxNode?)block ?? arrow?.Expression;

    // ---- recursion ----------------------------------------------------------------

    private enum RecursionKind { None, Structural, Memoised, VisitedMarking, DivideAndConquer, Branching, Single }

    private sealed record Recursion(RecursionKind Kind, int Branches, string MethodName, string Detail);

    private static Recursion FindRecursion(IMethodSymbol entry, Context context)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { entry };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(entry);
        Recursion strongest = new(RecursionKind.None, 0, "", "");

        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            var body = context.Bodies[method];
            var selfCalls = OwnInvocations(body)
                .Where(inv => Resolve(inv, context) is { } target && SymbolEqualityComparer.Default.Equals(target, method))
                .ToList();

            if (selfCalls.Count > strongest.Branches)
                strongest = Describe(method, body, selfCalls, context);

            foreach (var invocation in OwnInvocations(body))
                if (Resolve(invocation, context) is { } target && context.Bodies.ContainsKey(target) && seen.Add(target))
                    queue.Enqueue(target);
        }

        return strongest;
    }

    /// <summary>
    /// Two self-calls look identical in Fibonacci and in a tree traversal, but one is
    /// exponential and the other linear. What separates them is what the calls are
    /// given, so each recursive method is classified by its arguments and by what it does
    /// around the calls before its call count is allowed to mean "exponential".
    /// </summary>
    private static Recursion Describe(IMethodSymbol method, SyntaxNode body, List<InvocationExpressionSyntax> selfCalls, Context context)
    {
        string name = method.Name;
        int branches = selfCalls.Count;

        if (IsStructural(method, selfCalls, context.Model))
            return new(RecursionKind.Structural, branches, name,
                $"'{name}' recurses into child nodes ({ChildArguments(method, selfCalls, context.Model)}), so each node " +
                "is visited once: O(n) in the number of nodes.");

        if (MemoTable(body) is { } memo)
            return new(RecursionKind.Memoised, branches, name,
                $"'{name}' calls itself {Times(branches)} but caches results in '{memo}', so each distinct subproblem " +
                "is solved only once. The cost is the number of distinct subproblems times the work per subproblem — " +
                "the structure does not say how many there are, so this is left to the measurement.");

        if (VisitedMarker(body, context.Model) is { } visited)
            return new(RecursionKind.VisitedMarking, branches, name,
                $"'{name}' marks '{visited}' as visited and stops on revisits, so every node or cell is processed once: " +
                "O(V + E) for a graph, O(rows × columns) for a grid. How that relates to n depends on the input's shape.");

        if (branches >= 2 && HalvesSomething(body))
            return new(RecursionKind.DivideAndConquer, branches, name,
                $"'{name}' calls itself {Times(branches)} but splits the range each time — divide and conquer rather " +
                "than a branching search. The result depends on the cost of combining the halves: linear combining " +
                "gives O(n log n), constant combining gives O(n).");

        if (branches >= 2)
            return new(RecursionKind.Branching, branches, name,
                $"'{name}' calls itself {Times(branches)} per invocation with no memoisation, no visited set, and no " +
                "sign the input is being split, so the call tree multiplies at every level.");

        return new(RecursionKind.Single, branches, name,
            $"'{name}' calls itself once. Whether that is O(n) or O(log n) depends on how much the input shrinks " +
            "each time, which the structure alone does not say.");
    }

    private static string Times(int count) => count == 2 ? "twice" : $"{count} times";

    /// <summary>
    /// Every self-call hands over a child of a node parameter — <c>root.left</c>,
    /// <c>head.next</c> — where the member has the node's own type.
    /// </summary>
    private static bool IsStructural(IMethodSymbol method, List<InvocationExpressionSyntax> selfCalls, SemanticModel model)
        => selfCalls.All(call => call.ArgumentList.Arguments.Any(argument => IsChildOfParameter(argument.Expression, method, model)));

    private static bool IsChildOfParameter(ExpressionSyntax expression, IMethodSymbol method, SemanticModel model)
    {
        if (expression is not MemberAccessExpressionSyntax access) return false;
        if (model.GetSymbolInfo(access.Expression).Symbol is not IParameterSymbol parameter) return false;
        if (!method.Parameters.Contains(parameter, SymbolEqualityComparer.Default)) return false;
        var memberType = model.GetTypeInfo(access).Type;
        return memberType is not null && SymbolEqualityComparer.Default.Equals(memberType, parameter.Type);
    }

    private static string ChildArguments(IMethodSymbol method, List<InvocationExpressionSyntax> selfCalls, SemanticModel model)
        => string.Join(", ", selfCalls
            .SelectMany(call => call.ArgumentList.Arguments)
            .Where(argument => IsChildOfParameter(argument.Expression, method, model))
            .Select(argument => argument.Expression.ToString())
            .Distinct());

    /// <summary>
    /// The memoisation shape: an early return guarded by a lookup in some table, that
    /// returns the cached value, and a write into that same table.
    /// <c>if (memo.ContainsKey(n)) return memo[n];</c> ... <c>memo[n] = result;</c>
    ///
    /// Returning the cached value is what separates a memo from a visited check. A flood
    /// fill also guards on a table and writes to it — <c>if (grid[r][c] != '1') return;</c>
    /// then <c>grid[r][c] = '0'</c> — but it returns nothing, because it is not reusing an
    /// answer, only refusing to repeat work.
    /// </summary>
    private static string? MemoTable(SyntaxNode body)
    {
        foreach (var guard in OwnNodes(body).OfType<IfStatementSyntax>())
        {
            var returned = guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>()
                .Select(r => r.Expression)
                .Where(e => e is not null)
                .ToList();
            if (returned.Count == 0) continue;
            bool viaTryGetValue = guard.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "TryGetValue" });

            var tables = guard.Condition.DescendantNodesAndSelf()
                .Select(node => node switch
                {
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ContainsKey" or "TryGetValue" } m } => m.Expression.ToString(),
                    ElementAccessExpressionSyntax e when e.Parent is BinaryExpressionSyntax => e.Expression.ToString(),
                    _ => null
                })
                .Where(name => name is not null)
                .Distinct();

            foreach (var table in tables)
            {
                bool returnsCached = viaTryGetValue || returned.Any(e => e!.ToString().Contains(table!));
                if (!returnsCached) continue;

                bool written = OwnNodes(body).Any(node => node switch
                {
                    AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax target } => target.Expression.ToString() == table,
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "TryAdd" } m } => m.Expression.ToString() == table,
                    _ => false
                });
                if (written) return table;
            }
        }
        return null;
    }

    /// <summary>
    /// The flood-fill and graph-search shape: something is written to mark a node or cell
    /// as visited — <c>grid[r][c] = '0'</c>, <c>visited[node] = true</c>, <c>seen.Add(x)</c> —
    /// and an early return guards against processing it again.
    /// </summary>
    private static string? VisitedMarker(SyntaxNode body, SemanticModel model)
    {
        bool hasGuard = OwnNodes(body).OfType<IfStatementSyntax>()
            .Any(guard => guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());
        if (!hasGuard) return null;

        foreach (var node in OwnNodes(body))
        {
            switch (node)
            {
                case AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax target } assignment
                    when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                {
                    var root = target.Expression is ElementAccessExpressionSyntax inner ? inner.Expression : target.Expression;
                    if (model.GetSymbolInfo(root).Symbol is IParameterSymbol or IFieldSymbol or ILocalSymbol)
                        return root.ToString();
                    break;
                }
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } member }
                    when model.GetTypeInfo(member.Expression).Type is INamedTypeSymbol { Name: "HashSet" }:
                    return member.Expression.ToString();
            }
        }
        return null;
    }

    // ---- loops, calls, and library costs -------------------------------------------

    private sealed class Amortisation
    {
        public List<string> Pointers { get; } = new();
        public string? Worklist { get; set; }
    }

    private static (Shape Shape, string? Because) Analyse(
        IMethodSymbol method, Context context, HashSet<IMethodSymbol> path, Amortisation? amortisation = null)
    {
        amortisation ??= new Amortisation();
        var body = context.Bodies[method];
        var best = new Shape(0, 0);
        string? because = null;

        void Consider(Shape candidate, string? reason)
        {
            // Linear depth dominates: n² outranks n log n regardless of log count.
            if (candidate.Linear > best.Linear || (candidate.Linear == best.Linear && candidate.Log > best.Log))
            {
                best = candidate;
                because = reason;
            }
        }

        void Walk(SyntaxNode? node, int linear, int log)
        {
            if (node is null) return;

            foreach (var child in node.ChildNodes())
            {
                switch (child)
                {
                    // Conditions and incrementors run on every iteration, so calls inside
                    // them belong inside the loop: `while (list.Contains(x))` is a scan per pass.
                    case ForStatementSyntax loop:
                        if (loop.Declaration is not null) Walk(loop.Declaration, linear, log);
                        foreach (var init in loop.Initializers) Walk(init, linear, log);
                        Descend(IsLogarithmic(loop), linear, log, loop.Condition, loop.Statement, loop.Incrementors);
                        break;
                    case WhileStatementSyntax loop:
                        if (IsWorklist(loop, context.Model) is { } worklist && amortisation.Worklist is null)
                            amortisation.Worklist = $"'while ({loop.Condition})' on line {LineOf(loop)}";

                        // A two-pointer inner loop: across the whole outer loop it runs at most
                        // n times in total, so it adds nothing to the nesting depth.
                        if (linear + log > 0 && MonotonicPointer(loop, body, context.Model) is { } pointer)
                        {
                            amortisation.Pointers.Add(
                                $"the inner while loop on line {LineOf(loop)} only ever moves '{pointer}' forward, so " +
                                "across the whole outer loop it runs at most n times in total");
                            Walk(loop.Condition, linear, log);
                            Walk(loop.Statement, linear, log);
                            break;
                        }

                        Descend(HalvesSomething(loop.Statement), linear, log, loop.Condition, loop.Statement);
                        break;
                    case DoStatementSyntax loop:
                        Descend(HalvesSomething(loop.Statement), linear, log, loop.Statement, loop.Condition);
                        break;
                    case CommonForEachStatementSyntax loop:
                        if (loop is ForEachStatementSyntax plain) Walk(plain.Expression, linear, log);
                        Descend(false, linear, log, loop.Statement);
                        break;

                    // Only work when called; the call is what gets counted.
                    case LocalFunctionStatementSyntax:
                    case AnonymousFunctionExpressionSyntax:
                        break;

                    case InvocationExpressionSyntax invocation:
                        if (context.Costs.ByNode.TryGetValue(invocation, out var libraryCall))
                            Library(libraryCall, linear, log);
                        else
                            UserCall(invocation, linear, log);
                        Walk(child, linear, log);
                        break;

                    case BinaryExpressionSyntax or AssignmentExpressionSyntax or ArrayCreationExpressionSyntax
                        when context.Costs.ByNode.TryGetValue(child, out var concat):
                        Library(concat, linear, log);
                        Walk(child, linear, log);
                        break;

                    default:
                        Walk(child, linear, log);
                        break;
                }
            }
        }

        void Descend(bool isLog, int linear, int log, params object?[] parts)
        {
            int nextLinear = linear + (isLog ? 0 : 1);
            int nextLog = log + (isLog ? 1 : 0);
            Consider(new Shape(nextLinear, nextLog), null);
            foreach (var part in parts)
            {
                if (part is SyntaxNode node) Walk(node, nextLinear, nextLog);
                else if (part is IEnumerable<SyntaxNode> nodes) foreach (var n in nodes) Walk(n, nextLinear, nextLog);
            }
        }

        void Library(CostCall call, int linear, int log)
        {
            var (addLinear, addLog, assumption) = Contribution(call);
            if (addLinear == 0 && addLog == 0) return;

            string where = linear + log > 0 ? " inside a loop" : "";
            Consider(new Shape(linear + addLinear, log + addLog),
                $"{call.Cost.Label} on line {call.Line}, which is {call.Cost.BigO} per call{where}{assumption}");
        }

        void UserCall(InvocationExpressionSyntax invocation, int linear, int log)
        {
            if (Resolve(invocation, context) is not { } target || path.Contains(target) || !context.Bodies.ContainsKey(target))
                return;

            path.Add(target);
            var (inner, innerBecause) = Analyse(target, context, path, amortisation);
            path.Remove(target);

            // Work inside a callee happens once per visit to this call site, so its depth
            // stacks on top of whatever loops surround the call.
            Consider(new Shape(linear + inner.Linear, log + inner.Log), innerBecause);
        }

        Walk(body, 0, 0);
        return (best, because);
    }

    /// <summary>
    /// <c>while (queue.Count &gt; 0)</c> over a queue, stack or priority queue: a traversal
    /// whose total work is bounded by what is ever added to the worklist, not by
    /// multiplying the depths of the loops around and inside it.
    /// </summary>
    private static string? IsWorklist(WhileStatementSyntax loop, SemanticModel model)
    {
        foreach (var access in loop.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Name.Identifier.Text is not ("Count" or "Any")) continue;
            if (model.GetTypeInfo(access.Expression).Type is INamedTypeSymbol { Name: "Queue" or "Stack" or "PriorityQueue" or "LinkedList" } type)
                return type.Name;
        }
        return null;
    }

    /// <summary>
    /// The two-pointer shape: an inner while loop that advances a variable declared
    /// outside it, where nothing anywhere in the method ever moves that variable back.
    /// Assignments of the form <c>x = Math.Max(x, ...)</c> also only move forward.
    /// </summary>
    private static string? MonotonicPointer(WhileStatementSyntax loop, SyntaxNode body, SemanticModel model)
    {
        var advanced = loop.DescendantNodes()
            .Select(node => node switch
            {
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression } p => p.Operand,
                PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression } p => p.Operand,
                AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression, Right: LiteralExpressionSyntax } a => a.Left,
                _ => null
            })
            .OfType<IdentifierNameSyntax>()
            .Select(identifier => model.GetSymbolInfo(identifier).Symbol)
            .Where(symbol => symbol is ILocalSymbol or IParameterSymbol)
            .Distinct(SymbolEqualityComparer.Default);

        foreach (var symbol in advanced)
        {
            // Declared outside this loop, or it would be reset on every pass.
            var declaration = symbol!.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (declaration is null || loop.Span.Contains(declaration.Span)) continue;

            bool onlyForward = OwnNodes(body).All(node => node switch
            {
                AssignmentExpressionSyntax assignment when Refers(assignment.Left, symbol, model) =>
                    assignment.IsKind(SyntaxKind.AddAssignmentExpression) && assignment.Right is LiteralExpressionSyntax ||
                    assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    assignment.Right is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Max" } } max &&
                    max.ArgumentList.Arguments.Any(a => Refers(a.Expression, symbol, model)),
                PostfixUnaryExpressionSyntax unary when Refers(unary.Operand, symbol, model) => unary.IsKind(SyntaxKind.PostIncrementExpression),
                PrefixUnaryExpressionSyntax unary when Refers(unary.Operand, symbol, model) && unary.IsKind(SyntaxKind.PreDecrementExpression) => false,
                _ => true
            });

            if (onlyForward) return symbol.Name;
        }
        return null;
    }

    private static bool Refers(ExpressionSyntax expression, ISymbol symbol, SemanticModel model)
        => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, symbol);

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// What a library call adds to the loop depth. Shift and count costs depend on their
    /// argument, so the obvious cases are read from it — RemoveAt(list.Count - 1) is O(1),
    /// RemoveAt(0) is O(n), Substring(i, k) with a fixed k does not grow — and anything
    /// else is stated as an assumption rather than silently guessed.
    /// </summary>
    private static (int Linear, int Log, string Assumption) Contribution(CostCall call)
    {
        switch (call.Cost.Kind)
        {
            case CostKind.Linearithmic: return (1, 1, "");
            case CostKind.Logarithmic: return (0, 1, "");

            case CostKind.Shift:
                if (call.Amount is LiteralExpressionSyntax { Token.Value: 0 }) return (1, 0, " (index 0 shifts everything)");
                if (IsLastIndex(call.Amount)) return (0, 0, "");
                return (1, 0, ", assuming the index is not near the end");

            case CostKind.CountArgument:
                return call.Cost.CountGrows || MentionsSize(call.Amount)
                    ? (1, 0, call.Cost.CountGrows ? "" : ", with a length that grows with the input")
                    : (0, 0, "");

            default: return (1, 0, "");
        }
    }

    private static bool IsLastIndex(ExpressionSyntax? amount)
        => amount is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.SubtractExpression } subtract
           && subtract.Right is LiteralExpressionSyntax { Token.Value: 1 }
           && MentionsSize(subtract.Left);

    private static bool MentionsSize(ExpressionSyntax? expression)
        => expression is not null && expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Name.Identifier.Text is "Length" or "Count");

    private static StaticEstimate Classify(Shape shape, string? because, Recursion recursion)
    {
        string loops = $"loop nesting depth {shape.Linear}" +
                       (shape.Log > 0 ? $", {shape.Log} of them halving or doubling" : "");
        string cause = because is null ? "" : $" The deepest cost is {because}.";

        switch (recursion.Kind)
        {
            case RecursionKind.Structural:
                // Each node once, times whatever each visit costs on top.
                return shape.Linear == 0
                    ? new StaticEstimate(ComplexityClass.Linear, recursion.Detail + cause, shape.Linear, shape.Log, recursion.Branches)
                    : new StaticEstimate(ComplexityClass.Unknown,
                        recursion.Detail + $" Each visit also does work at {loops}, so the total is that multiplied by n.{cause}",
                        shape.Linear, shape.Log, recursion.Branches);
            case RecursionKind.Branching:
                return new StaticEstimate(ComplexityClass.Exponential, recursion.Detail, shape.Linear, shape.Log, recursion.Branches);
            case RecursionKind.None:
                break;
            default:
                return new StaticEstimate(ComplexityClass.Unknown, recursion.Detail, shape.Linear, shape.Log, recursion.Branches);
        }

        var complexityClass = (shape.Linear, shape.Log) switch
        {
            (0, 0) => ComplexityClass.Constant,
            (0, _) => ComplexityClass.Logarithmic,
            (1, 0) => ComplexityClass.Linear,
            (1, _) => ComplexityClass.Linearithmic,
            (2, 0) => ComplexityClass.Quadratic,
            (3, 0) => ComplexityClass.Cubic,
            _ => ComplexityClass.Unknown
        };

        string reasoning = complexityClass == ComplexityClass.Unknown
            ? $"Found {loops}, which does not map onto a standard class.{cause}"
            : $"Found {loops}, and no recursion.{cause}";

        return new StaticEstimate(complexityClass, reasoning, shape.Linear, shape.Log, 0);
    }

    // ---- helpers --------------------------------------------------------------------

    private static IMethodSymbol? Resolve(InvocationExpressionSyntax invocation, Context context)
        => (context.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)?.OriginalDefinition;

    /// <summary>Nodes belonging to this body, not to functions nested inside it.</summary>
    private static IEnumerable<SyntaxNode> OwnNodes(SyntaxNode body)
        => body.DescendantNodesAndSelf(n => n == body || (n is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax));

    private static IEnumerable<InvocationExpressionSyntax> OwnInvocations(SyntaxNode body)
        => OwnNodes(body).OfType<InvocationExpressionSyntax>();

    /// <summary>
    /// An index that is multiplied or divided rather than stepped, or a range that is
    /// halved. Anything subtler is treated as linear, which keeps mistakes in the
    /// direction of overstating cost rather than understating it.
    /// </summary>
    private static bool IsLogarithmic(ForStatementSyntax loop) => loop.Incrementors.Any(IsScalingUpdate);

    private static bool IsScalingUpdate(SyntaxNode node) => node is AssignmentExpressionSyntax assignment &&
        assignment.Kind() is SyntaxKind.MultiplyAssignmentExpression or SyntaxKind.DivideAssignmentExpression
            or SyntaxKind.LeftShiftAssignmentExpression or SyntaxKind.RightShiftAssignmentExpression;

    private static bool HalvesSomething(SyntaxNode? body)
    {
        if (body is null) return false;
        foreach (var node in OwnNodes(body))
        {
            if (IsScalingUpdate(node)) return true;
            // The binary-search shape: mid = (lo + hi) / 2.
            if (node is BinaryExpressionSyntax binary
                && binary.Kind() is SyntaxKind.DivideExpression or SyntaxKind.RightShiftExpression
                && binary.Right is LiteralExpressionSyntax)
                return true;
        }
        return false;
    }
}
