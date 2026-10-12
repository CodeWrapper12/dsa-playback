using DsaViz.Runtime;
using Microsoft.CodeAnalysis;

namespace DsaViz.Engine.Analysis;

/// <summary>What a costed call is measured against.</summary>
public enum CostTarget
{
    /// <summary>The object the method is called on: <c>list</c> in <c>list.Contains(x)</c>.</summary>
    Instance,

    /// <summary>
    /// A parameter of the method's definition. For an extension method parameter 0 is
    /// the <c>this</c> parameter, so <c>nums.Contains(x)</c> and
    /// <c>Enumerable.Contains(nums, x)</c> resolve to the same thing.
    /// </summary>
    Parameter
}

/// <summary>
/// The cost of one library member. <see cref="Kind"/> is a <see cref="CostKind"/>
/// constant. <see cref="Ordinal"/> means different things by kind: the measured
/// parameter for a Parameter target, the index argument for Shift, and the count
/// argument for CountArgument.
/// </summary>
public sealed record KnownCost(int Kind, string Label, string BigO, CostTarget Target, int Ordinal = 0)
{
    /// <summary>
    /// For count costs: whether a non-constant count should be read as growing with the
    /// input. True for an array's length — <c>new int[n]</c> is O(n) — and false for a
    /// substring length, which in a sliding window is usually a fixed width.
    /// </summary>
    public bool CountGrows { get; init; }
}

/// <summary>
/// The library calls whose cost is not visible as statements.
///
/// Statement counting treats <c>seen.Contains(v)</c> as one step. For a HashSet that is
/// right. For a List it is a scan of the whole list, and a loop around it is O(n²)
/// while every statement count says O(n). That is the exact trap interview questions
/// are built around, so a tool that counts it as one step teaches the trap backwards.
///
/// Matching is done against Roslyn symbols, never names, because the names collide on
/// purpose: HashSet.Contains and List.Contains differ only in their containing type.
/// Members not listed here are treated as O(1), which is correct for the operations a
/// DSA solution leans on hardest — List.Add, indexers, Dictionary and HashSet lookups,
/// Queue and Stack operations.
/// </summary>
public static class KnownCosts
{
    private static readonly string[] AlwaysLinearListMembers =
    {
        "Contains", "IndexOf", "LastIndexOf", "Remove", "RemoveAll", "Exists", "Find", "FindAll",
        "FindIndex", "FindLast", "FindLastIndex", "TrueForAll", "ConvertAll", "ForEach", "ToArray", "CopyTo"
    };

    private static readonly string[] LinearStringMembers =
    {
        "Contains", "IndexOf", "IndexOfAny", "LastIndexOf", "LastIndexOfAny", "Replace", "Split",
        "Trim", "TrimStart", "TrimEnd", "ToUpper", "ToLower", "ToUpperInvariant", "ToLowerInvariant",
        "ToCharArray", "PadLeft", "PadRight", "Insert", "Remove", "Normalize"
    };

    private static readonly string[] LinearArrayStatics =
    {
        "IndexOf", "LastIndexOf", "Exists", "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
        "TrueForAll", "ConvertAll", "ForEach", "Reverse", "Fill"
    };

    // Always O(n) over the source, with or without a predicate.
    private static readonly string[] LinearLinq =
    {
        "Contains", "Sum", "Max", "Min", "Average", "Aggregate", "ToList", "ToArray", "ToHashSet",
        "ToDictionary", "ToLookup", "Distinct", "Reverse", "SequenceEqual", "Except", "Intersect", "Union",
        "MaxBy", "MinBy", "DistinctBy"
    };

    // O(1) without a predicate, O(n) with one: Any() looks at one element, Any(p) may look at all.
    private static readonly string[] LinearLinqWithPredicate =
    {
        "Any", "All", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Count", "LongCount"
    };

    public static KnownCost? Lookup(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        string type = QualifiedName(definition.ContainingType);
        string name = definition.Name;
        int parameters = definition.Parameters.Length;

        static KnownCost Linear(string label) => new(CostKind.Linear, label, "O(n)", CostTarget.Instance);

        switch (type)
        {
            case "System.Collections.Generic.List`1":
                if (AlwaysLinearListMembers.Contains(name)) return Linear($"List<T>.{name}");
                if (name == "Reverse")
                    return parameters == 0
                        ? Linear("List<T>.Reverse")
                        : new(CostKind.CountArgument, "List<T>.Reverse", "O(count)", CostTarget.Instance, 1);
                if (name is "Insert" or "RemoveAt" or "InsertRange" or "RemoveRange")
                    return new(CostKind.Shift, $"List<T>.{name}", "O(n − index)", CostTarget.Instance, 0);
                if (name == "GetRange")
                    return new(CostKind.CountArgument, "List<T>.GetRange", "O(count)", CostTarget.Instance, 1);
                if (name == "Sort") return new(CostKind.Linearithmic, "List<T>.Sort", "O(n log n)", CostTarget.Instance);
                if (name == "BinarySearch") return new(CostKind.Logarithmic, "List<T>.BinarySearch", "O(log n)", CostTarget.Instance);
                return null;

            case "System.Collections.Generic.LinkedList`1":
                if (name is "Contains" or "Find" or "FindLast") return Linear($"LinkedList<T>.{name}");
                // Remove(T value) searches; Remove(LinkedListNode<T>) unlinks in O(1).
                if (name == "Remove" && parameters == 1 && definition.Parameters[0].Type is ITypeParameterSymbol)
                    return Linear("LinkedList<T>.Remove(value)");
                return null;

            case "System.Collections.Generic.Queue`1":
            case "System.Collections.Generic.Stack`1":
                if (name is "Contains" or "ToArray" or "CopyTo")
                    return Linear($"{definition.ContainingType.Name}<T>.{name}");
                return null;

            case "System.Collections.Generic.Dictionary`2":
                return name == "ContainsValue" ? Linear("Dictionary<K,V>.ContainsValue") : null;

            case "System.Collections.Generic.SortedList`2":
                if (name is "Add" or "Remove" or "RemoveAt" or "ContainsValue" or "IndexOfValue")
                    return Linear($"SortedList<K,V>.{name}");
                if (name is "ContainsKey" or "IndexOfKey" or "TryGetValue")
                    return new(CostKind.Logarithmic, $"SortedList<K,V>.{name}", "O(log n)", CostTarget.Instance);
                return null;

            case "System.Collections.Generic.SortedSet`1":
                return name is "Add" or "Remove" or "Contains"
                    ? new(CostKind.Logarithmic, $"SortedSet<T>.{name}", "O(log n)", CostTarget.Instance)
                    : null;

            case "System.Collections.Generic.SortedDictionary`2":
                if (name is "Add" or "Remove" or "ContainsKey" or "TryGetValue")
                    return new(CostKind.Logarithmic, $"SortedDictionary<K,V>.{name}", "O(log n)", CostTarget.Instance);
                return name == "ContainsValue" ? Linear("SortedDictionary<K,V>.ContainsValue") : null;

            case "System.Collections.Generic.PriorityQueue`2":
                if (name is "Enqueue" or "Dequeue" or "TryDequeue" or "EnqueueDequeue" or "DequeueEnqueue" or "Remove")
                    return new(CostKind.Logarithmic, $"PriorityQueue.{name}", "O(log n)", CostTarget.Instance);
                return name == "EnqueueRange" ? Linear("PriorityQueue.EnqueueRange") : null;

            case "System.String":
                if (definition.IsStatic)
                    return name == "Join" && parameters == 2
                        ? new(CostKind.Linear, "string.Join", "O(n)", CostTarget.Parameter, 1)
                        : null;
                if (name == "Substring")
                    return parameters == 1
                        ? new(CostKind.Shift, "string.Substring", "O(n − start)", CostTarget.Instance, 0)
                        : new(CostKind.CountArgument, "string.Substring", "O(length)", CostTarget.Instance, 1);
                return LinearStringMembers.Contains(name) ? Linear($"string.{name}") : null;

            case "System.Text.StringBuilder":
                if (name == "ToString" && parameters == 0) return Linear("StringBuilder.ToString");
                // Inserting at the front of a builder shifts everything after it.
                if (name is "Insert" or "Remove")
                    return new(CostKind.Shift, $"StringBuilder.{name}", "O(n − index)", CostTarget.Instance, 0);
                return null;

            case "System.Array":
                if (!definition.IsStatic)
                    return name is "Clone" or "CopyTo" ? Linear($"Array.{name}") : null;
                if (name == "Sort") return new(CostKind.Linearithmic, "Array.Sort", "O(n log n)", CostTarget.Parameter, 0);
                if (name == "BinarySearch") return new(CostKind.Logarithmic, "Array.BinarySearch", "O(log n)", CostTarget.Parameter, 0);
                if (name == "Copy" && parameters == 3) return new(CostKind.CountArgument, "Array.Copy", "O(length)", CostTarget.Parameter, 2);
                if (name == "Copy" && parameters == 5) return new(CostKind.CountArgument, "Array.Copy", "O(length)", CostTarget.Parameter, 4);
                if (name == "Clear" && parameters == 1) return new(CostKind.Linear, "Array.Clear", "O(n)", CostTarget.Parameter, 0);
                if (name == "Clear" && parameters == 3) return new(CostKind.CountArgument, "Array.Clear", "O(length)", CostTarget.Parameter, 2);
                return LinearArrayStatics.Contains(name)
                    ? new(CostKind.Linear, $"Array.{name}", "O(n)", CostTarget.Parameter, 0)
                    : null;

            case "System.Linq.Enumerable":
                if (name is "OrderBy" or "OrderByDescending" or "Order" or "OrderDescending")
                    return new(CostKind.Linearithmic, $"Enumerable.{name}", "O(n log n)", CostTarget.Parameter, 0);
                if (LinearLinq.Contains(name))
                    return new(CostKind.Linear, $"Enumerable.{name}", "O(n)", CostTarget.Parameter, 0);
                if (LinearLinqWithPredicate.Contains(name) && parameters == 2)
                    return new(CostKind.Linear, $"Enumerable.{name}(predicate)", "O(n)", CostTarget.Parameter, 0);
                return null;

            default:
                return null;
        }
    }

    public static string QualifiedName(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString() + "."
            : "";
        return ns + type.MetadataName;
    }
}
