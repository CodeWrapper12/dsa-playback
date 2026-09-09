using System.Text.Json;
using System.Text.Json.Serialization;
using DsaViz.Engine;

// M1 CLI: instrument one C# file, run its parameterless entry point, emit the trace.
// Input configuration and multi-n measurement arrive in M2.

string? file = null;
string entry = "Solve";
long budget = Runner.DefaultBudget;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length: file = args[++i]; break;
        case "--entry" when i + 1 < args.Length: entry = args[++i]; break;
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
    Console.Error.WriteLine("Usage: DsaViz.Engine --file <solution.cs> [--entry <Method>] [--budget <n>]");
    return 2;
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"File not found: {file}");
    return 2;
}

var result = Runner.Run(File.ReadAllText(file), entry, budget);

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
Console.WriteLine(JsonSerializer.Serialize(result, options));

return result.Status == DsaViz.Engine.Contracts.RunStatus.Completed ? 0 : 1;
