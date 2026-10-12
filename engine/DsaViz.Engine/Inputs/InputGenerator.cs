using System.Runtime.CompilerServices;
using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Inputs;

public static class InputGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Builds one argument at the given size, as an instance of <paramref name="targetType"/>.
    ///
    /// The seed is derived from the spec's seed and the size rather than shared, so every
    /// size is reproducible on its own while still differing from its neighbours. That
    /// matters more than it looks: a growth curve compares runs against each other, and if
    /// the data changed between runs the curve would be measuring the generator as much as
    /// the algorithm.
    /// </summary>
    public static object? Generate(InputSpec spec, int size, Type targetType)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        var random = new Random(HashCode.Combine(spec.Seed, size));

        return spec.Shape switch
        {
            InputShape.IntArray => Ints(spec, size, random),
            InputShape.IntList => new List<int>(Ints(spec, size, random)),
            InputShape.CharArray => Text(spec, size, random).ToCharArray(),
            InputShape.StringValue => Text(spec, size, random),
            InputShape.StringArray => Words(spec, size, random),
            InputShape.Matrix => Matrix(spec, size, random),
            InputShape.CharMatrix => Grid(spec, size, random),
            InputShape.IntScalar => spec.ScalesWithSize ? size : spec.FixedValue,
            InputShape.LongScalar => (long)(spec.ScalesWithSize ? size : spec.FixedValue),
            InputShape.BoolScalar => spec.FixedValue != 0,
            InputShape.Tree => Tree(spec, size, random, targetType),
            InputShape.LinkedList => Chain(spec, size, random, targetType),
            InputShape.NestedIntList => Adjacency(spec, size, random, targetType),
            InputShape.EdgeList => Edges(spec, size, random),
            _ => throw new NotSupportedException($"Unsupported input shape: {spec.Shape}")
        };
    }

    /// <summary>Kept for callers that only deal in built-in shapes.</summary>
    public static object? Generate(InputSpec spec, int size) => Generate(spec, size, typeof(object));

    // ---- flat data ------------------------------------------------------------------

    private static int[] Ints(InputSpec spec, int size, Random random)
    {
        int span = Math.Max(1, spec.MaxValue - spec.MinValue);
        var values = new int[size];

        switch (spec.Strategy)
        {
            case GenerationStrategy.Sorted:
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + i;
                break;

            case GenerationStrategy.ReverseSorted:
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + size - i;
                break;

            case GenerationStrategy.AllDuplicates:
                Array.Fill(values, spec.MinValue);
                break;

            case GenerationStrategy.Distinct:
                // Shuffled rather than sequential, so it is unordered like Random but
                // guaranteed collision-free. MaxValue is ignored: uniqueness needs at least
                // `size` distinct values available.
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + i;
                Shuffle(values, random);
                break;

            case GenerationStrategy.NearlySorted:
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + i;
                // Perturb roughly 5% of positions: enough to defeat a sorted-input fast
                // path without changing the overall shape of the data.
                for (int i = 0; i < Math.Max(1, size / 20) && size > 1; i++)
                {
                    int a = random.Next(size), b = random.Next(size);
                    (values[a], values[b]) = (values[b], values[a]);
                }
                break;

            default:
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + random.Next(span);
                break;
        }

        return values;
    }

    private static string Text(InputSpec spec, int size, Random random)
    {
        var characters = new char[size];
        for (int i = 0; i < size; i++)
        {
            characters[i] = spec.Strategy switch
            {
                GenerationStrategy.AllDuplicates => Alphabet[0],
                GenerationStrategy.Sorted => Alphabet[Math.Min(Alphabet.Length - 1, i * Alphabet.Length / Math.Max(1, size))],
                GenerationStrategy.ReverseSorted => Alphabet[Alphabet.Length - 1 - Math.Min(Alphabet.Length - 1, i * Alphabet.Length / Math.Max(1, size))],
                _ => Alphabet[random.Next(Alphabet.Length)]
            };
        }

        if (spec.Strategy == GenerationStrategy.NearlySorted)
        {
            for (int i = 0; i < size; i++) characters[i] = Alphabet[Math.Min(25, i * 26 / Math.Max(1, size))];
            for (int i = 0; i < Math.Max(1, size / 20) && size > 1; i++)
            {
                int a = random.Next(size), b = random.Next(size);
                (characters[a], characters[b]) = (characters[b], characters[a]);
            }
        }

        return new string(characters);
    }

    private static string[] Words(InputSpec spec, int size, Random random)
    {
        // Distinct words come from the index written in base 26, so uniqueness holds at
        // any size rather than running out of short random words.
        string Encode(int value)
        {
            var letters = new List<char>();
            do { letters.Add(Alphabet[value % 26]); value /= 26; } while (value > 0);
            while (letters.Count < 3) letters.Add('a');
            letters.Reverse();
            return new string(letters.ToArray());
        }

        var words = new string[size];
        for (int i = 0; i < size; i++)
        {
            words[i] = spec.Strategy switch
            {
                GenerationStrategy.AllDuplicates => "abc",
                GenerationStrategy.Random => new string(Enumerable.Range(0, 3 + random.Next(4))
                    .Select(_ => Alphabet[random.Next(26)]).ToArray()),
                _ => Encode(i)
            };
        }

        if (spec.Strategy == GenerationStrategy.Distinct) Shuffle(words, random);
        if (spec.Strategy == GenerationStrategy.ReverseSorted) Array.Reverse(words);
        if (spec.Strategy == GenerationStrategy.NearlySorted)
            for (int i = 0; i < Math.Max(1, size / 20) && size > 1; i++)
            {
                int a = random.Next(size), b = random.Next(size);
                (words[a], words[b]) = (words[b], words[a]);
            }

        return words;
    }

    private static int[][] Matrix(InputSpec spec, int size, Random random)
    {
        // Square, so "size" means the dimension that grows. A matrix job is therefore
        // O(size²) in data alone, which is worth remembering when reading its curve.
        int span = Math.Max(1, spec.MaxValue - spec.MinValue);
        var rows = new int[size][];
        for (int r = 0; r < size; r++)
        {
            rows[r] = new int[size];
            for (int c = 0; c < size; c++)
            {
                rows[r][c] = spec.Strategy switch
                {
                    GenerationStrategy.Sorted => spec.MinValue + r * size + c,
                    GenerationStrategy.ReverseSorted => spec.MinValue + (size - r) * size - c,
                    GenerationStrategy.AllDuplicates => spec.MinValue,
                    _ => spec.MinValue + random.Next(span)
                };
            }
        }
        return rows;
    }

    /// <summary>
    /// A grid of '1' (land) and '0' (water). AllDuplicates is all land: one island that
    /// covers everything, which is the deepest a flood fill can ever go.
    /// </summary>
    private static char[][] Grid(InputSpec spec, int size, Random random)
    {
        var rows = new char[size][];
        for (int r = 0; r < size; r++)
        {
            rows[r] = new char[size];
            for (int c = 0; c < size; c++)
                rows[r][c] = spec.Strategy == GenerationStrategy.AllDuplicates || random.Next(2) == 0 ? '1' : '0';
        }
        return rows;
    }

    // ---- linked structures ------------------------------------------------------------

    /// <summary>
    /// Builds a binary search tree. Random inserts distinct values in random order, giving
    /// an expected height of O(log n). Balanced builds the perfectly balanced tree. Skewed
    /// is what inserting sorted data produces: a chain of right children, height n — the
    /// case that turns an O(log n) recursion depth into O(n).
    /// </summary>
    private static object? Tree(InputSpec spec, int size, Random random, Type type)
    {
        var shape = NodeShapes.AsTree(type)
            ?? throw new UnsupportedSignatureException($"'{type.Name}' is not shaped like a TreeNode.");
        if (size == 0) return null;

        object NewNode(int value)
        {
            // Built without running a constructor: LeetCode's TreeNode takes optional
            // parameters, so it has no parameterless constructor to call.
            var node = RuntimeHelpers.GetUninitializedObject(type);
            shape.Value.Set(node, value);
            return node;
        }

        switch (spec.Strategy)
        {
            case GenerationStrategy.Balanced:
            {
                // Built iteratively from ranges so an enormous tree cannot overflow the
                // generator's own stack.
                var root = NewNode(size / 2);
                var pending = new Stack<(object Node, int Low, int High)>();
                pending.Push((root, 0, size - 1));
                while (pending.Count > 0)
                {
                    var (node, low, high) = pending.Pop();
                    int mid = (low + high) / 2;
                    if (low <= mid - 1)
                    {
                        var left = NewNode((low + mid - 1) / 2);
                        shape.Left.Set(node, left);
                        pending.Push((left, low, mid - 1));
                    }
                    if (mid + 1 <= high)
                    {
                        var right = NewNode((mid + 1 + high) / 2);
                        shape.Right.Set(node, right);
                        pending.Push((right, mid + 1, high));
                    }
                }
                return root;
            }

            case GenerationStrategy.Skewed:
            {
                var root = NewNode(0);
                var current = root;
                for (int i = 1; i < size; i++)
                {
                    var next = NewNode(i);
                    shape.Right.Set(current, next);
                    current = next;
                }
                return root;
            }

            default:
            {
                var values = Enumerable.Range(0, size).ToArray();
                Shuffle(values, random);
                var root = NewNode(values[0]);
                for (int i = 1; i < values.Length; i++)
                {
                    var node = root;
                    while (true)
                    {
                        bool goLeft = values[i] < (int)shape.Value.Get(node)!;
                        var child = goLeft ? shape.Left.Get(node) : shape.Right.Get(node);
                        if (child is null)
                        {
                            var leaf = NewNode(values[i]);
                            if (goLeft) shape.Left.Set(node, leaf); else shape.Right.Set(node, leaf);
                            break;
                        }
                        node = child;
                    }
                }
                return root;
            }
        }
    }

    private static object? Chain(InputSpec spec, int size, Random random, Type type)
    {
        var shape = NodeShapes.AsList(type)
            ?? throw new UnsupportedSignatureException($"'{type.Name}' is not shaped like a ListNode.");
        var values = Ints(spec, size, random);

        object? head = null;
        for (int i = values.Length - 1; i >= 0; i--)
        {
            var node = RuntimeHelpers.GetUninitializedObject(type);
            shape.Value.Set(node, values[i]);
            shape.Next.Set(node, head);
            head = node;
        }
        return head;
    }

    // ---- graphs -----------------------------------------------------------------------

    /// <summary>
    /// Edges over nodes 0..n-1. Random is a spanning tree plus about n/2 extra edges, so
    /// the graph is connected and has cycles, with roughly 1.5n edges — sparse, as most
    /// interview graphs are. Path is a single line, the deepest traversal possible.
    /// </summary>
    private static List<(int From, int To)> GraphEdges(InputSpec spec, int size, Random random)
    {
        var edges = new List<(int, int)>();
        if (size < 2) return edges;

        if (spec.Strategy == GenerationStrategy.Path)
        {
            for (int i = 0; i + 1 < size; i++) edges.Add((i, i + 1));
            return edges;
        }

        for (int i = 1; i < size; i++) edges.Add((random.Next(i), i));
        for (int i = 0; i < size / 2; i++)
        {
            int a = random.Next(size), b = random.Next(size);
            if (a != b) edges.Add((a, b));
        }
        return edges;
    }

    private static int[][] Edges(InputSpec spec, int size, Random random)
        => GraphEdges(spec, size, random).Select(e => new[] { e.From, e.To }).ToArray();

    private static object Adjacency(InputSpec spec, int size, Random random, Type type)
    {
        var neighbours = Enumerable.Range(0, size).Select(_ => new List<int>()).ToList();
        foreach (var (from, to) in GraphEdges(spec, size, random))
        {
            neighbours[from].Add(to);
            neighbours[to].Add(from);
        }

        return type == typeof(List<List<int>>)
            ? neighbours
            : neighbours.Select(list => (IList<int>)list).ToList();
    }

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
