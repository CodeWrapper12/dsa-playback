using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DsaViz.Engine;
using DsaViz.Engine.Analysis;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Measurement;

// Three modes:
//   --list-entries   what could be measured in this file, read without compiling
//   --job <path|->   a full measurement request as JSON, used by the editor extension
//   (default)        a single run, or --measure for the flag-driven curve

// Every number the engine prints is formatted the same way on every machine. Without
// this, a machine in an Indian locale prints 50 million as 5,00,00,000 in one message
// while the editor prints 50,000,000 in the next.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// The result goes out on its own handle to stdout. Console.Out is pointed at nothing, so
// neither a solution printing outside a measured run nor a thread abandoned after a
// timeout can write into the JSON the editor is about to parse.
var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
Console.SetOut(TextWriter.Null);

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    // camelCase on the wire so the TypeScript side reads naturally; case-insensitive on
    // the way in so a hand-written job file can use either.
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
int? stepSize = null;
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

        case "--steps" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
                return Fail("--steps needs a non-negative input size.");
            stepSize = n;
            break;

        case "--sizes" when i + 1 < args.Length:
            var parsed = new List<int>();
            foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 0)
                    return Fail($"--sizes must be non-negative integers; got '{part}'.");
                parsed.Add(size);
            }
            if (parsed.Count == 0) return Fail("--sizes needs at least one value.");
            sizes = parsed.ToArray();
            break;

        case "--strategy" when i + 1 < args.Length:
            if (!Enum.TryParse<GenerationStrategy>(args[++i], ignoreCase: true, out var chosen))
                return Fail($"Unknown strategy. Options: {string.Join(", ", Enum.GetNames<GenerationStrategy>())}");
            strategy = chosen;
            break;

        case "--budget" when i + 1 < args.Length:
            if (!long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out budget) || budget <= 0)
                return Fail("--budget must be a positive integer.");
            break;

        default:
            return Fail($"Unrecognised argument: {args[i]}");
    }
}

if (jobPath is not null)
{
    string requestText;
    try { requestText = jobPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(jobPath); }
    catch (Exception ex) { return Fail($"Could not read the job: {ex.Message}"); }

    JobRequest? request;
    try { request = JsonSerializer.Deserialize<JobRequest>(requestText, json); }
    catch (JsonException ex) { return Fail($"The job was not valid JSON: {ex.Message}"); }

    if (request is null) return Fail("The job was empty.");
    return Emit(JobRunner.Run(request), 0);
}

if (file is null)
    return Fail(
        "Usage:\n" +
        "  DsaViz.Engine --file <solution.cs> [--entry <Method|Type.Method>] [--measure]\n" +
        "                [--sizes 10,100,1000] [--strategy <" + string.Join("|", Enum.GetNames<GenerationStrategy>()) + ">]\n" +
        "                [--budget <n>]\n" +
        "  DsaViz.Engine --file <solution.cs> --entry <Method> --steps <n> [--strategy <...>]\n" +
        "  DsaViz.Engine --file <solution.cs|-> --list-entries\n" +
        "  DsaViz.Engine --job <request.json|->");

// "-" reads the source from stdin, so the editor can list the methods of a file with
// unsaved changes and put its buttons on the lines the student is looking at.
string source;
if (file == "-") source = Console.In.ReadToEnd();
else if (!File.Exists(file)) return Fail($"File not found: {file}");
else source = File.ReadAllText(file);

if (listEntries) return Emit(new { Entries = EntryPointScanner.Scan(source) }, 0);

if (stepSize is { } stepAt)
{
    var steps = Stepper.Record(SourceAnalysis.Create(source), entry, inputs: null, stepAt, Stepper.DefaultMaxSteps, strategy, budget);
    return Emit(steps, steps.Status == RunStatus.Completed ? 0 : 1);
}

if (!measure)
{
    var single = Runner.Run(source, entry, budget);
    return Emit(single, single.Status == RunStatus.Completed ? 0 : 1);
}

var result = Measurer.Measure(source, entry, inputs: null, sizes, budget, strategy);
return Emit(result, result.Status == RunStatus.Completed ? 0 : 1);

int Emit(object value, int exitCode)
{
    output.WriteLine(JsonSerializer.Serialize(value, json));
    return exitCode;
}

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
