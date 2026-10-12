using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// The space reading from the code: what the interview answer would be.
///
/// Two sources of space, read separately. The call stack, from the kind of recursion —
/// a tree traversal is O(h), divide and conquer is O(log n), a linear chain of calls is
/// O(n). And the heap, from what grows: an n-sized array, or a collection added to inside
/// a loop. A buffer created and dropped inside a loop is counted once, not once per pass,
/// because only one is ever held at a time — that is the difference between space and
/// allocation, and it is exactly what the measured readings are compared against.
/// </summary>
public static partial class StaticEstimator
{
    private static readonly string[] GrowingMethods =
        { "Add", "Enqueue", "Push", "Insert", "AddLast", "AddFirst", "TryAdd", "Append", "AppendLine", "AddRange" };

    private static readonly string[] CopyingMethods =
        { "ToList", "ToArray", "ToHashSet", "ToDictionary", "ToCharArray", "Split", "GetRange", "Clone" };

    private sealed record SpacePart(ComplexityClass Class, string Reason);

    public static StaticEstimate EstimateSpace(string source, string entryMethod)
        => EstimateSpace(SourceAnalysis.Create(source), entryMethod);

    public static StaticEstimate EstimateSpace(SourceAnalysis analysis, string entryMethod)
    {
        var (context, entry, problem) = Prepare(analysis, entryMethod);
        if (problem is not null) return new StaticEstimate(ComplexityClass.Unknown, problem, 0, 0, 0);

        var found = new List<SpacePart> { Stack(FindRecursion(entry, context), context) };

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(entry);
        visited.Add(entry);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            var body = context.Bodies[method];
            found.AddRange(Heap(body, context.Model));

            foreach (var invocation in OwnInvocations(body))
                if (Resolve(invocation, context) is { } target && context.Bodies.ContainsKey(target) && visited.Add(target))
                    queue.Enqueue(target);
        }

        if (found.Any(c => c.Class == ComplexityClass.Unknown))
        {
            var unknown = found.First(c => c.Class == ComplexityClass.Unknown);
            return new StaticEstimate(ComplexityClass.Unknown, unknown.Reason, 0, 0, 0);
        }

        var biggest = found.MaxBy(c => Growth.Rank(c.Class))!;
        if (biggest.Class == ComplexityClass.Constant)
            return new StaticEstimate(ComplexityClass.Constant,
                "Nothing it allocates grows with the input, and it does not recurse.", 0, 0, 0);

        var reasons = found.Where(c => c.Class == biggest.Class).Select(c => c.Reason).Distinct();
        return new StaticEstimate(biggest.Class, string.Join(" ", reasons), 0, 0, 0);
    }

    private static SpacePart Stack(Recursion recursion, Context context) => recursion.Kind switch
    {
        RecursionKind.None => new(ComplexityClass.Constant, ""),
        RecursionKind.Structural => new(ComplexityClass.Linear,
            $"The call stack is as deep as the structure '{recursion.MethodName}' walks: O(h) — n on a skewed tree or a list, " +
            "log n on a balanced tree. O(n) is the worst case."),
        RecursionKind.DivideAndConquer => new(ComplexityClass.Logarithmic,
            $"'{recursion.MethodName}' halves its range on each call, so the stack is O(log n) deep."),
        RecursionKind.Memoised => new(ComplexityClass.Linear,
            $"'{recursion.MethodName}' keeps a memo table with an entry per subproblem and recurses O(n) deep to fill it."),
        RecursionKind.VisitedMarking => new(ComplexityClass.Unknown,
            $"'{recursion.MethodName}' recurses once per node or cell it reaches, so the stack can be as deep as the whole " +
            "structure — O(V) for a graph, up to O(rows × columns) for a grid. How that compares with n depends on the input's shape."),
        RecursionKind.Branching => new(ComplexityClass.Linear,
            $"'{recursion.MethodName}' branches, but the stack only ever holds one path through the call tree, which is O(n) deep."),
        _ => new(HalvesSomething(context.Bodies.FirstOrDefault(b => b.Key.Name == recursion.MethodName).Value)
                ? ComplexityClass.Logarithmic : ComplexityClass.Linear,
            $"'{recursion.MethodName}' calls itself once per step, so the stack is as deep as the number of steps.")
    };

    private static IEnumerable<SpacePart> Heap(SyntaxNode body, SemanticModel model)
    {
        bool insideWorklist(SyntaxNode node) => node.Ancestors().TakeWhile(a => a != body)
            .OfType<WhileStatementSyntax>().Any(w => IsWorklist(w, model) is not null);

        int depthOf(SyntaxNode node, SyntaxNode? declaredAt) => node.Ancestors()
            .TakeWhile(a => a != body)
            .Count(a => a is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CommonForEachStatementSyntax
                        && (declaredAt is null || !a.Span.Contains(declaredAt.Span)));

        foreach (var node in OwnNodes(body))
        {
            switch (node)
            {
                // new T[n]: one is held at a time if it lives in a loop-local variable;
                // if it is stored into something longer-lived, they accumulate.
                case ArrayCreationExpressionSyntax creation
                    when creation.Type.RankSpecifiers.FirstOrDefault()?.Sizes.FirstOrDefault() is { } size
                         && size is not OmittedArraySizeExpressionSyntax && !model.GetConstantValue(size).HasValue:
                {
                    bool stored = creation.Parent is AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax or MemberAccessExpressionSyntax }
                                  || creation.Parent is ArgumentSyntax;
                    int depth = stored ? depthOf(creation, null) : 0;
                    yield return new(Power(depth + 1),
                        stored && depth > 0
                            ? $"`{creation}` on line {LineOf(creation)} is stored on every pass of {depth} enclosing loop(s), so they accumulate."
                            : $"`{creation}` on line {LineOf(creation)} allocates an n-sized array" +
                              (depthOf(creation, null) > 0 ? " — inside a loop, but only one is held at a time." : "."));
                    break;
                }

                // Something added to on every pass grows to the number of passes.
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation
                    when GrowingMethods.Contains(member.Name.Identifier.Text) && IsCollection(model.GetTypeInfo(member.Expression).Type):
                {
                    var declaredAt = model.GetSymbolInfo(member.Expression).Symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    int depth = depthOf(invocation, declaredAt);
                    if (depth == 0) break;
                    bool bounded = insideWorklist(invocation);
                    yield return new(bounded ? ComplexityClass.Linear : Power(depth),
                        bounded
                            ? $"'{member.Expression}' grows inside a traversal, bounded by the number of nodes: O(n)."
                            : $"'{member.Expression}' gains an element on every pass of {Loops(depth)} (line {LineOf(invocation)}).");
                    break;
                }

                // map[key] = value on a dictionary adds an entry each pass.
                case AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax target } assignment
                    when model.GetTypeInfo(target.Expression).Type is INamedTypeSymbol { Name: "Dictionary" or "SortedDictionary" }:
                {
                    var declaredAt = model.GetSymbolInfo(target.Expression).Symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                    int depth = depthOf(assignment, declaredAt);
                    if (depth > 0)
                        yield return new(Power(depth),
                            $"'{target.Expression}' gains an entry on every pass of {Loops(depth)} (line {LineOf(assignment)}).");
                    break;
                }

                // text += piece builds a string as long as everything appended.
                case AssignmentExpressionSyntax append when append.IsKind(SyntaxKind.AddAssignmentExpression)
                    && model.GetTypeInfo(append.Left).Type?.SpecialType == SpecialType.System_String && depthOf(append, null) > 0:
                    yield return new(Power(depthOf(append, null)),
                        $"'{append.Left}' grows by a piece on every pass (line {LineOf(append)}); the old copies are garbage, " +
                        "so the space is the final string, not the total allocated.");
                    break;

                // Copies of a collection: ToList(), Split(), new List<int>(values).
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax copy } when CopyingMethods.Contains(copy.Name.Identifier.Text):
                    yield return new(ComplexityClass.Linear, $"`{copy.Name}` on line {LineOf(copy)} makes an O(n) copy.");
                    break;

                case ObjectCreationExpressionSyntax created
                    when IsCollection(model.GetTypeInfo(created).Type)
                         && created.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } argument
                         && (IsCollection(model.GetTypeInfo(argument).Type) || model.GetTypeInfo(argument).Type is IArrayTypeSymbol):
                    yield return new(ComplexityClass.Linear, $"`{created}` on line {LineOf(created)} copies its input: O(n).");
                    break;
            }
        }
    }

    private static bool IsCollection(ITypeSymbol? type)
        => type is INamedTypeSymbol named && (named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                                              || named.Name == "StringBuilder");

    private static ComplexityClass Power(int depth) => depth switch
    {
        <= 0 => ComplexityClass.Constant,
        1 => ComplexityClass.Linear,
        2 => ComplexityClass.Quadratic,
        3 => ComplexityClass.Cubic,
        _ => ComplexityClass.Unknown
    };

    private static string Loops(int depth) => depth == 1 ? "a loop" : $"{depth} nested loops";
}
