using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DsaViz.Engine.Js;

/// <summary>Why Node cannot be used, said so the editor can offer to fix it.</summary>
public sealed class JsUnavailableException : Exception
{
    /// <summary><c>node-missing</c> or <c>node-version</c>.</summary>
    public string Code { get; }
    public JsUnavailableException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// The student's Node, and the two scripts that ship with the engine: the reader, which uses
/// the TypeScript compiler bundled beside it to read and instrument JavaScript and TypeScript,
/// and the runtime a built program runs against. Builds are kept in the temporary folder by
/// the hash of what was built, so an unchanged file runs without building again.
/// </summary>
public sealed class JsHost
{
    public const int MinimumVersion = 18;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Node { get; }

    private JsHost(string node) => Node = node;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "js");
    public static string Reader => Path.Combine(Folder, "dsaviz_js.js");
    public static string Runtime => Path.Combine(Folder, "dsaviz_rt.js");

    /// <summary>
    /// The TypeScript compiler: shipped beside the scripts; in a development checkout, the
    /// extension's own copy, which its packaging copies there.
    /// </summary>
    public static string TypeScript
    {
        get
        {
            string shipped = Path.Combine(Folder, "typescript.js");
            if (File.Exists(shipped)) return shipped;
            if (Environment.GetEnvironmentVariable("DSAVIZ_TYPESCRIPT") is { Length: > 0 } named && File.Exists(named)) return named;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                string candidate = Path.Combine(folder.FullName, "extension", "node_modules", "typescript", "lib", "typescript.js");
                if (File.Exists(candidate)) return candidate;
            }
            throw new InvalidOperationException("The TypeScript compiler is missing from this installation.");
        }
    }

    private static string CacheFolder => Path.Combine(Path.GetTempPath(), "dsaviz-js");

    public static string InstallAdvice() =>
        $"Install Node.js {MinimumVersion} or later from nodejs.org, or set dsaPlayback.node to the node you have.";

    /// <summary>The Node to use: the one the editor names, then DSAVIZ_NODE, then node on the path.</summary>
    public static JsHost Locate(string? requested = null)
    {
        if (!File.Exists(Reader) || !File.Exists(Runtime))
            throw new InvalidOperationException($"The JavaScript runner is missing from this installation: {Folder}");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        if (Environment.GetEnvironmentVariable("DSAVIZ_NODE") is { Length: > 0 } fromEnvironment) candidates.Add(fromEnvironment);
        candidates.Add("node");

        foreach (var candidate in candidates)
        {
            string? resolved = Path.IsPathRooted(candidate) ? (File.Exists(candidate) ? candidate : null) : OnPath(candidate);
            if (resolved is not null) return Checked(new JsHost(resolved));
        }
        throw new JsUnavailableException("node-missing", "Node.js was not found. " + InstallAdvice());
    }

    private static readonly Dictionary<string, string> Versions = new(StringComparer.Ordinal);

    private static JsHost Checked(JsHost host)
    {
        string version;
        lock (Versions)
        {
            if (!Versions.TryGetValue(host.Node, out version!))
            {
                version = Execute(host.Node, new[] { "--version" }, null, TimeSpan.FromSeconds(60)).Output.Trim();
                Versions[host.Node] = version;
            }
        }
        var major = Regex.Match(version, @"^v(\d+)\.");
        if (!major.Success)
            throw new JsUnavailableException("node-missing", $"{host.Node} did not run as Node.js ({version}). " + InstallAdvice());
        if (int.Parse(major.Groups[1].Value) < MinimumVersion)
            throw new JsUnavailableException("node-version", $"DSA Playback needs Node.js {MinimumVersion} or later; {host.Node} is {version}. " + InstallAdvice());
        return host;
    }

    private static string? OnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            foreach (var extension in extensions)
            {
                string path = Path.Combine(directory, name + extension);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>Asks the reader to describe or build a file: one JSON request in, one JSON answer out.</summary>
    public T Read<T>(object request) where T : class
    {
        var (exit, output, errors) = Execute(Node, new[] { Reader }, JsonSerializer.Serialize(request, Json), TimeSpan.FromSeconds(120));
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine($"JavaScript reader failed (exit {exit}): {errors}");
            throw new InvalidOperationException($"The JavaScript reader stopped unexpectedly (exit code {exit}). This is a bug in the visualizer, not in your code.");
        }
        return JsonSerializer.Deserialize<T>(output, Json) ?? throw new InvalidOperationException("The JavaScript reader answered with nothing.");
    }

    /// <summary>
    /// Writes a built program to its own folder, once: the solution, and a launcher that runs
    /// it on a worker with a deep stack. Returns the launcher's path.
    /// </summary>
    public static string Save(string program)
    {
        string runtime = File.ReadAllText(Runtime);
        string folder = Path.Combine(CacheFolder, Rust.RustHost.Hash(runtime + "\n" + program)[..16]);
        string launcher = Path.Combine(folder, "launcher.js");
        if (File.Exists(launcher)) return launcher;

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "solution.js"), program);
        string building = Path.Combine(folder, $"launcher-{Guid.NewGuid():N}.js");
        File.WriteAllText(building, $"require({JsonSerializer.Serialize(Runtime)}).launch(__dirname + \"/solution.js\");\n");
        File.Move(building, launcher, overwrite: true);
        return launcher;
    }

    /// <summary>How a built program is started: its launcher, which runs it on a worker with a deep stack.</summary>
    public static IReadOnlyList<string> Launcher(string launcher) => new[] { launcher };

    private static (int Exit, string Output, string Errors) Execute(string program, IEnumerable<string> arguments, string? input, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new JsUnavailableException("node-missing", $"Could not start {program}. " + InstallAdvice());
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new InvalidOperationException($"{Path.GetFileName(program)} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        return (process.ExitCode, output.Result, errors.Result);
    }
}
