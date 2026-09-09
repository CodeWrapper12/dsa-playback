using System.Text.Json;
using System.Text.Json.Serialization;
using DsaViz.Engine;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;

// M2 CLI. Two modes: a single instrumented run, or a measurement across input sizes
// that fits a growth curve and compares it against a static reading of the code.
// The configuration surface here is deliberately small; the extension's panel in M3
// will drive the same engine through richer options.

string? file = null;
string entry = "Solve";
long budget = Runner.DefaultBudget;
bool measure = false;
int[]? sizes = null;
GenerationStrategy? strategy = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length:
            file = args[++i];
            break;

        case "--entry" when i + 1 < args.Length:
            entry = args[++i];
            break;

        case "--measure":
            measure = true;
            break;

        case "--sizes" when i + 1 < args.Length:
            var parsed = new List<int>();
            foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), out int size) || size < 0)
                {
                    Console.Error.WriteLine($"--sizes must be non-negative integers; got '{part}'.");
                    return 2;
                }
                parsed.Add(size);
            }
            if (parsed.Count == 0)
            {
                Console.Error.WriteLine("--sizes needs at least one value.");
                return 2;
            }
            sizes = parsed.ToArray();
            break;

        case "--strategy" when i + 1 < args.Length:
            if (!Enum.TryParse<GenerationStrategy>(args[++i], ignoreCase: true, out var parsedStrategy))
            {
                Console.Error.WriteLine(
                    $"Unknown strategy. Options: {string.Join(", ", Enum.GetNames<GenerationStrategy>())}");
                return 2;
            }
            strategy = parsedStrategy;
            break;

        case "--budget" when i + 1 < args.Length:
            if (!long.TryParse(args[++i], out budget) || budget <= 0)
            {
                Console.Error.WriteLine("--budget must be a positive integer.");
                return 2;
            }
            break;

        default:
            Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
            return 2;
    }
}

if (file is null)
{
    Console.Error.WriteLine(
        "Usage: DsaViz.Engine --file <solution.cs> [--entry <Method>] [--measure]\n" +
        "                     [--sizes 10,100,1000] [--strategy Random|Sorted|ReverseSorted|\n" +
        "                     AllDuplicates|NearlySorted] [--budget <n>]");
    return 2;
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"File not found: {file}");
    return 2;
}

string source = File.ReadAllText(file);

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

if (!measure)
{
    var single = Runner.Run(source, entry, budget);
    Console.WriteLine(JsonSerializer.Serialize(single, options));
    return single.Status == RunStatus.Completed ? 0 : 1;
}

// A strategy given on the command line applies to every collection input. Scalars are
// left alone: overriding a search target with a sort order would mean nothing.
IReadOnlyList<InputSpec>? inputs = null;
if (strategy is { } chosen)
{
    var compiled = Runner.Compile(source, entry, instrument: false);
    if (!compiled.Success)
    {
        Console.Error.WriteLine(string.Join(Environment.NewLine, compiled.Errors));
        return 1;
    }

    using var probe = compiled.Solution!;
    try
    {
        inputs = DsaViz.Engine.Inputs.ParameterBinder.Infer(probe.Parameters)
            .Select(spec => spec.Shape is InputShape.IntScalar or InputShape.BoolScalar
                ? spec
                : spec with { Strategy = chosen })
            .ToList();
    }
    catch (DsaViz.Engine.Inputs.UnsupportedSignatureException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

var result = Measurer.Measure(source, entry, inputs, sizes, budget);
Console.WriteLine(JsonSerializer.Serialize(result, options));
return result.Status == RunStatus.Completed ? 0 : 1;
