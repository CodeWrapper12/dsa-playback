using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DsaViz.Engine.Python;

/// <summary>Why Python cannot be used at all, said so the editor can offer to fix it.</summary>
public sealed class PythonUnavailableException : Exception
{
    /// <summary><c>python-missing</c> or <c>python-version</c>: what the editor offers to do about it.</summary>
    public string Code { get; }

    public PythonUnavailableException(string code, string message) : base(message) => Code = code;
}

/// <summary>The runner did not answer — it was stopped, or failed in a way that is the visualizer's bug.</summary>
public sealed class PythonRunFailedException : Exception
{
    public bool TimedOut { get; }

    public PythonRunFailedException(string message, bool timedOut = false) : base(message) => TimedOut = timedOut;
}

/// <summary>
/// Runs dsaviz_py.py with the student's Python: one process per request, one JSON object
/// in on stdin and one out on stdout. A process per request is what makes a runaway
/// solution stoppable — a Python thread cannot be stopped from outside, but its process
/// can — and starting Python takes tens of milliseconds, which a run never notices.
/// </summary>
public sealed class PythonHost
{
    public const string ScriptName = "dsaviz_py.py";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Interpreter { get; }
    public string Script { get; }

    private PythonHost(string interpreter, string script)
    {
        Interpreter = interpreter;
        Script = script;
    }

    /// <summary>The script ships beside the engine, in its python folder.</summary>
    public static string DefaultScript => Path.Combine(AppContext.BaseDirectory, "python", ScriptName);

    /// <summary>
    /// The Python to use: the one the editor names (the interpreter selected in VS Code's
    /// Python extension), then DSAVIZ_PYTHON, then python3 or python on the path.
    /// </summary>
    public static PythonHost Locate(string? requested = null)
    {
        if (!File.Exists(DefaultScript))
            throw new InvalidOperationException($"The Python runner is missing from this installation: {DefaultScript}");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        if (Environment.GetEnvironmentVariable("DSAVIZ_PYTHON") is { Length: > 0 } fromEnvironment) candidates.Add(fromEnvironment);
        candidates.AddRange(OperatingSystem.IsWindows() ? new[] { "python", "py" } : new[] { "python3", "python" });

        foreach (var candidate in candidates)
        {
            string? resolved = Path.IsPathRooted(candidate) ? (File.Exists(candidate) ? candidate : null) : OnPath(candidate);
            if (resolved is not null) return new PythonHost(resolved, DefaultScript);
        }

        throw new PythonUnavailableException("python-missing",
            "Python was not found. Install Python 3.9 or later, or select an interpreter in VS Code's Python extension.");
    }

    private static string? OnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            foreach (var extension in extensions)
            {
                string path = Path.Combine(directory, name + extension);
                // The Microsoft Store's python.exe stub is a zero-byte placeholder that opens the Store.
                if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            }
        }
        return null;
    }

    public T Call<T>(object request, TimeSpan timeout) where T : class
    {
        var start = new ProcessStartInfo(Interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Without this, Windows writes the request in the console's code page, and any
            // character outside it — a "—" or "▶" in a comment — reaches Python garbled.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        // -I: isolated mode. The student's working folder, PYTHONPATH and user site
        // packages stay out of the runner's own imports, so a file named json.py beside
        // the solution cannot replace the json module the runner reads its request with.
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Script);
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(start)
            ?? throw new PythonRunFailedException($"Could not start {Interpreter}.");

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(JsonSerializer.Serialize(request, Json));
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new PythonRunFailedException(
                $"The run did not finish within {timeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }

        string text = output.Result;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing on stdout is the runner itself failing: its stack trace is for the
            // log, and the student is told it is not their code.
            Console.Error.WriteLine($"Python runner failed (exit {process.ExitCode}): {errors.Result}");
            // The last line of Python's traceback names the exception without the stack:
            // the one thing a bug report needs, and nothing a student has to wade through.
            string last = errors.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? "";
            throw new PythonRunFailedException(
                $"The Python runner stopped unexpectedly (exit code {process.ExitCode}{(last.Length > 0 ? $": {last}" : "")}). " +
                "This is a bug in the visualizer, not in your code.");
        }

        var answer = JsonSerializer.Deserialize<T>(text, Json)
                     ?? throw new PythonRunFailedException("The Python runner answered with nothing.");
        CheckVersion(text);
        return answer;
    }

    /// <summary>The runner answers a too-old Python with an error object instead of running anything.</summary>
    private static void CheckVersion(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out var error) && error.GetString() == "python-version")
        {
            throw new PythonUnavailableException("python-version",
                document.RootElement.GetProperty("message").GetString() ?? "This Python is too old.");
        }
    }
}
