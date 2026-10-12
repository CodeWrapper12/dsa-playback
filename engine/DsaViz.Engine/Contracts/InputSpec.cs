namespace DsaViz.Engine.Contracts;

/// <summary>The kinds of argument the engine knows how to build at a chosen size.</summary>
public enum InputShape
{
    IntArray,
    IntList,
    StringValue,
    Matrix,
    IntScalar,
    BoolScalar,
    LongScalar,
    CharArray,
    StringArray,

    /// <summary>IList&lt;IList&lt;int&gt;&gt;, generated as a graph's adjacency list over n nodes.</summary>
    NestedIntList,

    /// <summary>A LeetCode-style TreeNode with val, left and right, defined in the solution file.</summary>
    Tree,

    /// <summary>A LeetCode-style ListNode with val and next, defined in the solution file.</summary>
    LinkedList,

    /// <summary>An int[][] of [u, v] pairs, paired with a node count — the usual LeetCode graph input.</summary>
    EdgeList,

    /// <summary>A char[][] grid of '0' and '1', as in Number of Islands.</summary>
    CharMatrix
}

/// <summary>
/// How the values in a generated collection are arranged. This is not decoration:
/// the arrangement is often what decides the complexity actually observed. A quicksort
/// is O(n log n) on random input and O(n^2) on already-sorted input, and being able to
/// flip between them and watch the curve change is the point of having strategies.
/// </summary>
public enum GenerationStrategy
{
    Random,

    /// <summary>
    /// Every value unique. Distinct from Random, which draws from a bounded range and
    /// therefore repeats: an algorithm that stops at the first duplicate exits almost
    /// immediately on random data and never shows its worst case. This is how you ask
    /// for the worst case explicitly.
    /// </summary>
    Distinct,

    Sorted,
    ReverseSorted,
    AllDuplicates,
    NearlySorted,

    /// <summary>Trees: a perfectly balanced binary search tree, height log n.</summary>
    Balanced,

    /// <summary>
    /// Trees: every node has only a right child, so the tree is a chain of height n.
    /// This is what a binary search tree built from sorted input degenerates into, and it
    /// turns an O(log n) recursion depth into O(n).
    /// </summary>
    Skewed,

    /// <summary>Graphs: nodes joined in a single line, the deepest possible traversal.</summary>
    Path
}

/// <summary>
/// One argument's configuration. Collections take their length from the measurement
/// size; a scalar either takes the size too (a lone <c>int n</c> parameter) or holds a
/// fixed value (a search target that should not grow with the input).
/// </summary>
public sealed record InputSpec
{
    public required InputShape Shape { get; init; }
    public GenerationStrategy Strategy { get; init; } = GenerationStrategy.Random;

    public int MinValue { get; init; } = 0;
    public int MaxValue { get; init; } = 1_000;

    /// <summary>When false, <see cref="FixedValue"/> is used instead of the size.</summary>
    public bool ScalesWithSize { get; init; } = true;
    public int FixedValue { get; init; }

    /// <summary>Fixed so a growth curve compares like with like across sizes.</summary>
    public int Seed { get; init; } = 20260909;
}
