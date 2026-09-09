using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Guesses a complexity class from the shape of the code, without running it.
///
/// This is a heuristic and is treated as one. Deciding the complexity of an arbitrary
/// program is not possible in general, so the useful design goal is not accuracy on
/// every input but honesty about the boundary: anything this cannot recognise is
/// reported as Unknown rather than resolved to the nearest plausible label. A
/// confidently wrong Big-O in a tool someone is learning from is worse than an
/// admission of ignorance, because it teaches the wrong lesson with authority.
///
/// Its value is mostly in the disagreements. When the static reading and the measured
/// curve differ, that gap is where the interesting explanation lives.
/// </summary>
public static class StaticEstimator
{
    private sealed record Shape(int LinearDepth, int LogDepth);

    public static StaticEstimate Estimate(string source, string entryMethod)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var byName = methods
            .GroupBy(m => m.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.First());

        if (!byName.TryGetValue(entryMethod, out var entry))
            return new StaticEstimate(ComplexityClass.Unknown,
                $"No method named '{entryMethod}' was found to analyse.", 0, 0, 0);

        // Recursion has to be looked for across everything the entry point reaches,
        // not just in the entry method itself. MergeSort.Sort does not call itself at
        // all — it calls SortRange, which does — and an estimator that only inspects
        // the entry sees a plain loop and reports Linear.
        var recursion = FindRecursion(entry, byName);
        var shape = Analyse(entry, byName, new HashSet<string> { entryMethod });

        return Classify(shape, recursion);
    }

    private sealed record Recursion(int Branches, bool ShrinksByHalving, string MethodName);

    private static Recursion FindRecursion(
        MethodDeclarationSyntax entry, IReadOnlyDictionary<string, MethodDeclarationSyntax> byName)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<MethodDeclarationSyntax>();
        queue.Enqueue(entry);
        seen.Add(entry.Identifier.Text);

        var strongest = new Recursion(0, false, "");

        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            int selfCalls = CountSelfCalls(method);

            if (selfCalls > strongest.Branches)
                strongest = new Recursion(
                    selfCalls, HalvesSomething(method.Body), method.Identifier.Text);

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? called = NameOf(invocation);
                if (called is null || !seen.Add(called)) continue;
                if (byName.TryGetValue(called, out var declaration)) queue.Enqueue(declaration);
            }
        }

        return strongest;
    }

    private static StaticEstimate Classify(Shape shape, Recursion recursion)
    {
        string loops = $"loop nesting depth {shape.LinearDepth}" +
                       (shape.LogDepth > 0 ? $", {shape.LogDepth} of them halving or doubling" : "");

        // Branching recursion is only exponential when each call still gets nearly the
        // whole input. Fibonacci recurses on n-1 and n-2 and doubles its work at every
        // level; merge sort recurses twice on halves and is O(n log n). They are
        // indistinguishable by call-site count alone, so the halving check is what
        // keeps this from confidently mislabelling every divide-and-conquer algorithm.
        if (recursion.Branches >= 2 && recursion.ShrinksByHalving)
            return new StaticEstimate(ComplexityClass.Unknown,
                $"'{recursion.MethodName}' calls itself {recursion.Branches} times but splits the " +
                "range each time, which is divide and conquer rather than a branching search. " +
                "The result depends on the cost of combining the halves — linear combining gives " +
                "O(n log n), constant combining gives O(n) — and the structure alone does not say which.",
                shape.LinearDepth, shape.LogDepth, recursion.Branches);

        if (recursion.Branches >= 2)
            return new StaticEstimate(ComplexityClass.Exponential,
                $"'{recursion.MethodName}' calls itself {recursion.Branches} times per invocation " +
                "with no memoisation visible and no sign the input is being split, so the call " +
                "tree doubles at every level.",
                shape.LinearDepth, shape.LogDepth, recursion.Branches);

        if (recursion.Branches == 1)
            return new StaticEstimate(ComplexityClass.Unknown,
                $"'{recursion.MethodName}' calls itself once. Whether that is O(n) or O(log n) " +
                "depends on how much the input shrinks each time, which the structure alone does " +
                "not say — walking a list and halving a range look identical here.",
                shape.LinearDepth, shape.LogDepth, recursion.Branches);

        var complexityClass = (shape.LinearDepth, shape.LogDepth) switch
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
            ? $"Found {loops}, which does not map onto a standard class."
            : $"Found {loops}, and no recursion.";

        return new StaticEstimate(complexityClass, reasoning, shape.LinearDepth, shape.LogDepth, 0);
    }

    private static int CountSelfCalls(MethodDeclarationSyntax method)
    {
        string name = method.Identifier.Text;
        return method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => NameOf(invocation) == name);
    }

    private static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null
    };

    private static Shape Analyse(
        MethodDeclarationSyntax method,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> byName,
        HashSet<string> path)
    {
        var best = new Shape(0, 0);

        void Consider(Shape candidate)
        {
            // Linear depth dominates: n^2 outranks n log n regardless of log count.
            if (candidate.LinearDepth > best.LinearDepth ||
                (candidate.LinearDepth == best.LinearDepth && candidate.LogDepth > best.LogDepth))
                best = candidate;
        }

        void Walk(SyntaxNode? node, int linear, int log)
        {
            if (node is null) return;

            foreach (var child in node.ChildNodes())
            {
                switch (child)
                {
                    case ForStatementSyntax loop:
                        Descend(loop.Statement, IsLogarithmic(loop));
                        break;

                    case WhileStatementSyntax loop:
                        Descend(loop.Statement, IsLogarithmic(loop));
                        break;

                    case DoStatementSyntax loop:
                        Descend(loop.Statement, IsLogarithmic(loop));
                        break;

                    case CommonForEachStatementSyntax loop:
                        Descend(loop.Statement, isLog: false);
                        break;

                    case InvocationExpressionSyntax invocation:
                        VisitCall(invocation);
                        Walk(child, linear, log);
                        break;

                    // A local function is only work when it is called, and the call is
                    // what gets counted, so its declaration is not descended into here.
                    case LocalFunctionStatementSyntax:
                        break;

                    default:
                        Walk(child, linear, log);
                        break;
                }
            }

            void Descend(SyntaxNode? body, bool isLog)
            {
                int nextLinear = linear + (isLog ? 0 : 1);
                int nextLog = log + (isLog ? 1 : 0);
                Consider(new Shape(nextLinear, nextLog));
                Walk(body, nextLinear, nextLog);
            }

            void VisitCall(InvocationExpressionSyntax invocation)
            {
                string? called = NameOf(invocation);
                if (called is null || path.Contains(called)) return;
                if (!byName.TryGetValue(called, out var declaration)) return;

                path.Add(called);
                var inner = Analyse(declaration, byName, path);
                path.Remove(called);

                // Work inside a callee happens once per visit to this call site, so its
                // depth stacks on top of whatever loops surround the call.
                Consider(new Shape(linear + inner.LinearDepth, log + inner.LogDepth));
            }
        }

        Walk(method.Body ?? (SyntaxNode?)method.ExpressionBody, 0, 0);
        return best;
    }

    /// <summary>
    /// Recognises the two shapes that produce logarithmic behaviour: an index that is
    /// multiplied or divided rather than stepped, and a range that is halved. Anything
    /// subtler is treated as linear, which keeps the estimator's mistakes in the
    /// direction of over- rather than under-stating cost.
    /// </summary>
    private static bool IsLogarithmic(ForStatementSyntax loop)
        => loop.Incrementors.Any(IsScalingUpdate);

    private static bool IsLogarithmic(WhileStatementSyntax loop) => HalvesSomething(loop.Statement);

    private static bool IsLogarithmic(DoStatementSyntax loop) => HalvesSomething(loop.Statement);

    private static bool IsScalingUpdate(SyntaxNode node) => node switch
    {
        AssignmentExpressionSyntax assignment => assignment.Kind() is
            SyntaxKind.MultiplyAssignmentExpression or
            SyntaxKind.DivideAssignmentExpression or
            SyntaxKind.LeftShiftAssignmentExpression or
            SyntaxKind.RightShiftAssignmentExpression,
        _ => false
    };

    private static bool HalvesSomething(SyntaxNode? body)
    {
        if (body is null) return false;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (IsScalingUpdate(node)) return true;

            // The binary-search shape: mid = (lo + hi) / 2, then one bound moves to mid.
            if (node is BinaryExpressionSyntax binary
                && binary.Kind() is SyntaxKind.DivideExpression or SyntaxKind.RightShiftExpression
                && binary.Right is LiteralExpressionSyntax)
                return true;
        }

        return false;
    }
}
