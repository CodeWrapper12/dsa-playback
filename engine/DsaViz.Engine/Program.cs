using System.Text.Json;
using System.Text.Json.Serialization;
using DsaViz.Engine;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;

// Three modes:
//   --list-entries   what could be measured in this file, read without compiling
//   --job <path|->   a full measurement request as JSON, used by the editor extension
//   (default)        a single run, or --measure for the flag-driven curve
//
// The extension drives the JSON mode: flags cannot express a full input configuration,
// and the editor needs the source text back with the result so it can lay costs over
// the code without reading the file a second time and risking a mismatch.

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    // camelCase on the wire so the TypeScript side reads naturally; case-insensitive
    // on the way in so a hand-written job file can use either.
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};

string? file = null;
string entry = "Solve";
long budget = Runner.DefaultBudget;
bool measure = false;
bool listEntries = false;
string? jobPath = null;
int[]? sizes = null;
GenerationStrategy? strategy = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length: file = args[++i]; break;
        case "--entry" when i + 1 < args.Length: entry = args[++i]; break;
        case "--job" when i + 1 < args.Length: jobPath = args[++i]; break;
        case "--measure": measure = true; break;
        case "--list-entries": listEntries = true; break;

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
            if (parsed.Count == 0) { Console.Error.WriteLine("--sizes needs at least one value."); return 2; }
            sizes = parsed.ToArray();
            break;

        case "--strategy" when i + 1 < args.Length:
            if (!Enum.TryParse<GenerationStrategy>(args[++i], ignoreCase: true, out var chosenStrategy))
            {
                Console.Error.WriteLine(
                    $"Unknown strategy. Options: {string.Join(", ", Enum.GetNames<GenerationStrategy>())}");
                return 2;
            }
            strategy = chosenStrategy;
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

// ---- JSON job -------------------------------------------------------------------

if (jobPath is not null)
{
    string requestText;
    try
    {
        requestText = jobPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(jobPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not read the job: {ex.Message}");
        return 2;
    }

    JobRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<JobRequest>(requestText, json);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"The job was not valid JSON: {ex.Message}");
        return 2;
    }

    if (request is null) { Console.Error.WriteLine("The job was empty."); return 2; }

    Console.WriteLine(JsonSerializer.Serialize(JobRunner.Run(request), json));
    return 0;
}

// ---- everything else needs a file -----------------------------------------------

if (file is null)
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  DsaViz.Engine --file <solution.cs> [--entry <Method>] [--measure]\n" +
        "                [--sizes 10,100,1000] [--strategy Random|Distinct|Sorted|\n" +
        "                ReverseSorted|AllDuplicates|NearlySorted] [--budget <n>]\n" +
        "  DsaViz.Engine --file <solution.cs> --list-entries\n" +
        "  DsaViz.Engine --job <request.json|->");
    return 2;
}

if (!File.Exists(file)) { Console.Error.WriteLine($"File not found: {file}"); return 2; }

string source = File.ReadAllText(file);

if (listEntries)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Entries = EntryPointScanner.Scan(source) }, json));
    return 0;
}

if (!measure)
{
    var single = Runner.Run(source, entry, budget);
    Console.WriteLine(JsonSerializer.Serialize(single, json));
    return single.Status == RunStatus.Completed ? 0 : 1;
}

var result = Measurer.Measure(source, entry, ApplyStrategy(source, entry, strategy), sizes, budget);
Console.WriteLine(JsonSerializer.Serialize(result, json));
return result.Status == RunStatus.Completed ? 0 : 1;

// ---- helpers ---------------------------------------------------------------------

IReadOnlyList<InputSpec>? ApplyStrategy(string text, string entryName, GenerationStrategy? chosen)
{
    if (chosen is not { } value) return null;

    var compiled = Runner.Compile(text, entryName, instrument: false);
    if (!compiled.Success) return null;

    using var probe = compiled.Solution!;
    try
    {
        return ParameterBinder.Infer(probe.Parameters)
            .Select(spec => spec.Shape is InputShape.IntScalar or InputShape.BoolScalar
                ? spec
                : spec with { Strategy = value })
            .ToList();
    }
    catch (UnsupportedSignatureException)
    {
        // Measurer reports this properly with the full explanation; returning null
        // here lets it infer and fail with that message rather than a partial one.
        return null;
    }
}
