using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Inputs;

public static class InputGenerator
{
    /// <summary>
    /// Builds one argument at the given size.
    ///
    /// The seed is derived from the spec's seed and the size rather than being shared,
    /// so every size is reproducible on its own while still differing from its
    /// neighbours. Reproducibility matters more than it looks: a growth curve compares
    /// runs against each other, so if the data changed between runs the curve would be
    /// measuring the generator as much as the algorithm.
    /// </summary>
    public static object Generate(InputSpec spec, int size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

        var random = new Random(HashCode.Combine(spec.Seed, size));

        return spec.Shape switch
        {
            InputShape.IntArray => BuildIntArray(spec, size, random),
            InputShape.IntList => new List<int>(BuildIntArray(spec, size, random)),
            InputShape.StringValue => BuildString(spec, size, random),
            InputShape.Matrix => BuildMatrix(spec, size, random),
            InputShape.IntScalar => spec.ScalesWithSize ? size : spec.FixedValue,
            InputShape.BoolScalar => spec.FixedValue != 0,
            _ => throw new NotSupportedException($"Unsupported input shape: {spec.Shape}")
        };
    }

    private static int[] BuildIntArray(InputSpec spec, int size, Random random)
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
                // guaranteed collision-free. MinValue offsets it; MaxValue is ignored
                // because uniqueness needs at least `size` distinct values available.
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + i;
                for (int i = size - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (values[i], values[j]) = (values[j], values[i]);
                }
                break;

            case GenerationStrategy.NearlySorted:
                for (int i = 0; i < size; i++) values[i] = spec.MinValue + i;
                // Perturb roughly 5% of positions: enough to defeat a sorted-input
                // fast path without changing the overall shape of the data.
                int swaps = Math.Max(1, size / 20);
                for (int i = 0; i < swaps && size > 1; i++)
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

    private static string BuildString(InputSpec spec, int size, Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        var characters = new char[size];

        for (int i = 0; i < size; i++)
        {
            characters[i] = spec.Strategy switch
            {
                GenerationStrategy.AllDuplicates => alphabet[0],
                // A 26-letter alphabet cannot supply unique characters beyond 26, so
                // Distinct degrades to Random here rather than silently repeating.
                GenerationStrategy.Distinct => alphabet[random.Next(alphabet.Length)],
                GenerationStrategy.Sorted => alphabet[i % alphabet.Length],
                GenerationStrategy.ReverseSorted => alphabet[alphabet.Length - 1 - i % alphabet.Length],
                _ => alphabet[random.Next(alphabet.Length)]
            };
        }

        return new string(characters);
    }

    private static int[][] BuildMatrix(InputSpec spec, int size, Random random)
    {
        // Square, so "size" means the same thing as it does for a flat array: the
        // dimension that grows. A matrix job is therefore O(size^2) in data alone,
        // which is worth remembering when reading its curve.
        var rows = new int[size][];
        int span = Math.Max(1, spec.MaxValue - spec.MinValue);

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
}
