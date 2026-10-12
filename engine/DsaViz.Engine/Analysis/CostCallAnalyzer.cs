using DsaViz.Engine.Contracts;
using DsaViz.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DsaViz.Engine.Analysis;

public enum WrapKind
{
    /// <summary><c>x</c> becomes <c>Tracer.Through(x, site, kind)</c>.</summary>
    Through,

    /// <summary>An index argument becomes <c>Tracer.ShiftIndex(i, site)</c>.</summary>
    ShiftIndex,

    /// <summary>A count argument becomes <c>Tracer.Units(n, site)</c>.</summary>
    Units,

    /// <summary><c>s += p</c> becomes <c>s += Tracer.Before(s, site, kind, p)</c>.</summary>
    BeforeAssignment
}

public sealed record CostWrap(SyntaxNode Target, WrapKind Kind);

/// <summary>
/// One costed call in the original source. <see cref="Charged"/> is false when the call
/// is recognised but cannot be safely rewritten — a struct receiver, a ref argument, a
/// target-typed expression. The static reading still counts it; the measurement cannot,
/// and the difference is reported rather than hidden.
/// </summary>
public sealed record CostCall(
    int Site,
    SyntaxNode Node,
    KnownCost Cost,
    int Line,
    int Column,
    bool Charged,
    IReadOnlyList<CostWrap> Wraps,
    ExpressionSyntax? Amount)
{
    public LibraryCallSite ToSite() => new(Site, Line, Column, Cost.Label, Cost.BigO);
}

public sealed class CostCallAnalysis
{
    public CostCallAnalysis(IReadOnlyList<CostCall> calls)
    {
        Calls = calls;
        ByNode = calls.ToDictionary(c => c.Node);
    }

    public IReadOnlyList<CostCall> Calls { get; }
    public IReadOnlyDictionary<SyntaxNode, CostCall> ByNode { get; }
    public IReadOnlyList<LibraryCallSite> Sites => Calls.Where(c => c.Charged).Select(c => c.ToSite()).ToList();
}

/// <summary>
/// Finds every library call with a known non-constant cost, and works out exactly which
/// expression to wrap so the measurement can charge it without changing what the
/// program does.
/// </summary>
public static class CostCallAnalyzer
{
    public static CostCallAnalysis Analyze(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        var found = new List<(SyntaxNode Node, KnownCost Cost, List<CostWrap> Wraps, ExpressionSyntax? Amount, bool Charged)>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    if (FromInvocation(invocation, model) is { } call) found.Add(call);
                    break;

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                    if (FromConcatenation(binary, model) is { } concat) found.Add(concat);
                    break;

                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.AddAssignmentExpression):
                    if (FromAppend(assignment, model) is { } append) found.Add(append);
                    break;

                case ArrayCreationExpressionSyntax creation:
                    if (FromArrayCreation(creation, model) is { } array) found.Add(array);
                    break;
            }
        }

        // Sites are numbered in document order so their ids are stable for a given file.
        var calls = found
            .OrderBy(f => f.Node.SpanStart)
            .Select((f, index) =>
            {
                var position = tree.GetLineSpan(f.Node.Span).StartLinePosition;
                return new CostCall(index, f.Node, f.Cost, position.Line + 1, position.Character + 1,
                    f.Charged, f.Wraps, f.Amount);
            })
            .ToList();

        return new CostCallAnalysis(calls);
    }

    private static (SyntaxNode, KnownCost, List<CostWrap>, ExpressionSyntax?, bool)? FromInvocation(
        InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return null;
        if (KnownCosts.Lookup(method) is not { } cost) return null;
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return (invocation, cost, new(), null, false);

        var wraps = new List<CostWrap>();
        bool charged = true;
        ExpressionSyntax? amount = null;

        // A conditional access (list?.Contains) and an implicit receiver have no
        // expression of their own to wrap. Recognised, not charged.
        if (invocation.Parent is MemberBindingExpressionSyntax || invocation.Expression is MemberBindingExpressionSyntax)
            charged = false;

        ExpressionSyntax? Measured()
        {
            if (cost.Target == CostTarget.Instance)
                return operation.Instance is { IsImplicit: false } instance ? instance.Syntax as ExpressionSyntax : null;
            return ArgumentFor(operation, cost.Ordinal);
        }

        switch (cost.Kind)
        {
            case CostKind.Shift:
            {
                var receiver = Measured();
                amount = ArgumentFor(operation, cost.Ordinal);
                if (IsWrappableReference(receiver, model) && IsPlainInt(amount, model))
                {
                    wraps.Add(new CostWrap(receiver!, WrapKind.Through));
                    wraps.Add(new CostWrap(amount!, WrapKind.ShiftIndex));
                }
                else charged = false;
                break;
            }

            case CostKind.CountArgument:
            {
                amount = ArgumentFor(operation, cost.Ordinal);
                if (IsPlainInt(amount, model)) wraps.Add(new CostWrap(amount!, WrapKind.Units));
                else charged = false;
                break;
            }

            default:
            {
                var measured = Measured();
                if (IsWrappableReference(measured, model)) wraps.Add(new CostWrap(measured!, WrapKind.Through));
                else charged = false;
                break;
            }
        }

        return (invocation, cost, charged ? wraps : new List<CostWrap>(), amount, charged);
    }

    private static (SyntaxNode, KnownCost, List<CostWrap>, ExpressionSyntax?, bool)? FromConcatenation(
        BinaryExpressionSyntax binary, SemanticModel model)
    {
        if (!IsStringConcatenation(binary, model)) return null;

        // Charge only the innermost left operand of a chain. `s + a + b` copies s once in
        // the way that matters for accumulation; charging every link would count the
        // same growing string several times on one line.
        if (binary.Left is BinaryExpressionSyntax left && IsStringConcatenation(left, model)) return null;

        // A literal on the left — "Total: " + x — has a fixed small cost and is never the
        // accumulation pattern this exists to catch.
        if (model.GetConstantValue(binary.Left).HasValue) return null;

        var cost = new KnownCost(CostKind.Linear, "string concatenation (+)", "O(n)", CostTarget.Instance);
        bool charged = IsWrappableReference(binary.Left, model);
        var wraps = charged ? new List<CostWrap> { new(binary.Left, WrapKind.Through) } : new List<CostWrap>();
        return (binary, cost, wraps, null, charged);
    }

    private static (SyntaxNode, KnownCost, List<CostWrap>, ExpressionSyntax?, bool)? FromAppend(
        AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        if (model.GetTypeInfo(assignment.Left).Type?.SpecialType != SpecialType.System_String) return null;

        var cost = new KnownCost(CostKind.Linear, "string += (copies the whole string)", "O(n)", CostTarget.Instance);

        // The rewrite reads the left side a second time, so it is only safe when that
        // read is side-effect free: a local, a parameter, or a field.
        var symbol = model.GetSymbolInfo(assignment.Left).Symbol;
        bool simple = assignment.Left is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }
                      && symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol;

        var wraps = simple ? new List<CostWrap> { new(assignment, WrapKind.BeforeAssignment) } : new List<CostWrap>();
        return (assignment, cost, wraps, null, simple);
    }

    /// <summary>
    /// <c>new int[n]</c> is one statement, but the runtime zeroes all n elements, so it is
    /// O(n) work. Allocating inside a loop is the mistake this makes visible: a fresh
    /// n-sized array on every pass turns an O(n) loop into O(n²) without any extra code
    /// on the page. Only single-dimension sizes are charged; a literal size is constant.
    /// </summary>
    private static (SyntaxNode, KnownCost, List<CostWrap>, ExpressionSyntax?, bool)? FromArrayCreation(
        ArrayCreationExpressionSyntax creation, SemanticModel model)
    {
        var rank = creation.Type.RankSpecifiers.FirstOrDefault();
        if (rank is null || rank.Sizes.Count != 1) return null;

        var size = rank.Sizes[0];
        if (size is OmittedArraySizeExpressionSyntax || model.GetConstantValue(size).HasValue) return null;

        var cost = new KnownCost(CostKind.CountArgument, "array allocation (new T[n])", "O(n)", CostTarget.Instance)
        {
            CountGrows = true
        };

        bool charged = IsPlainInt(size, model);
        var wraps = charged ? new List<CostWrap> { new(size, WrapKind.Units) } : new List<CostWrap>();
        return (creation, cost, wraps, size, charged);
    }

    private static bool IsStringConcatenation(BinaryExpressionSyntax binary, SemanticModel model)
        => binary.IsKind(SyntaxKind.AddExpression)
           && model.GetSymbolInfo(binary).Symbol is IMethodSymbol { MethodKind: MethodKind.BuiltinOperator } op
           && op.ContainingType?.SpecialType == SpecialType.System_String
           && model.GetTypeInfo(binary.Left).Type?.SpecialType == SpecialType.System_String;

    private static ExpressionSyntax? ArgumentFor(IInvocationOperation operation, int ordinal)
    {
        var argument = operation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal);
        if (argument is null || argument.ArgumentKind != ArgumentKind.Explicit) return null;

        return argument.Syntax switch
        {
            ArgumentSyntax { RefKindKeyword.RawKind: 0 } syntax => syntax.Expression,
            ArgumentSyntax => null,                       // ref, out, in: cannot be wrapped
            ExpressionSyntax receiver => receiver,         // the `this` argument of a reduced extension call
            _ => null
        };
    }

    /// <summary>
    /// Tracer.Through is generic over a reference type and returns its argument, so the
    /// wrapped expression keeps exactly its original type and binds to the same overload.
    /// That only holds when the expression has a natural type of its own — target-typed
    /// expressions such as <c>new()</c> and collection expressions do not — and when that
    /// type is a reference type, since passing a struct through would copy it.
    /// </summary>
    private static bool IsWrappableReference(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null or ThisExpressionSyntax or BaseExpressionSyntax) return false;
        var type = model.GetTypeInfo(expression).Type;
        return type is { IsReferenceType: true } && type.TypeKind != TypeKind.Error && type is not IDynamicTypeSymbol;
    }

    private static bool IsPlainInt(ExpressionSyntax? expression, SemanticModel model)
        => expression is not null
           && model.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_Int32;
}
