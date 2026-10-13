using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DsaViz.Engine.Native;

namespace DsaViz.Engine.Java;

/// <summary>Why Java cannot be compiled at all, said so the editor can help: <c>java-missing</c> or <c>java-version</c>.</summary>
public sealed class JavaUnavailableException : Exception
{
    public string Code { get; }
    public JavaUnavailableException(string code, string message) : base(message) => Code = code;
}

/// <summary>One javac diagnostic: its key (compiler.err.…), its text, and where it is.</summary>
public sealed record JavaDiagnostic(string Key, string Message, int Line, int Column);

public sealed record JavaCompileResult(string? Classes, IReadOnlyList<JavaDiagnostic> Errors);

/// <summary>
/// The student's JDK: found, used to compile the visualizer's runtime once per JDK and each
/// program once, and to run them. Builds are kept in the temporary folder by the hash of what
/// was compiled.
/// </summary>
public sealed class JavaHost
{
    public const string RuntimeName = "Dsaviz.java";
    public const int MinimumVersion = 17;

    public static string RuntimeFolder => Path.Combine(AppContext.BaseDirectory, "java");

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);

    public string Javac { get; }
    public string Java { get; }
    private string? _version;

    private JavaHost(string javac, string java)
    {
        Javac = javac;
        Java = java;
    }

    /// <summary>The JVM options every run uses.</summary>
    /// <remarks>
    /// The first tier of the JIT only: it starts fastest, and it never removes an allocation by
    /// escape analysis, so every byte the code asks for is counted however hot the loop.
    /// </remarks>
    public IReadOnlyList<string> Launcher(string classes)
        => new[]
        {
            "-XX:TieredStopAtLevel=1", "-XX:+IgnoreUnrecognizedVMOptions", "-XX:-DoEscapeAnalysis", "-Xshare:auto",
            "-Dfile.encoding=UTF-8", "-cp", Runtime() + Path.PathSeparator + classes, "DsavizMain"
        };

    /// <summary>The setting (a JDK folder, or javac itself), DSAVIZ_JAVA_HOME, JAVA_HOME, then javac on the path.</summary>
    public static JavaHost Locate(string? requested = null)
    {
        if (!File.Exists(Path.Combine(RuntimeFolder, RuntimeName)))
            throw new InvalidOperationException($"The Java runtime is missing from this installation: {RuntimeFolder}");

        string exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var homes = new[] { requested, Environment.GetEnvironmentVariable("DSAVIZ_JAVA_HOME"), Environment.GetEnvironmentVariable("JAVA_HOME") };
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            string javac = File.Exists(home) && Path.GetFileName(home).StartsWith("javac") ? home : Path.Combine(home, "bin", "javac" + exe);
            string java = Path.Combine(Path.GetDirectoryName(javac)!, "java" + exe);
            if (File.Exists(javac) && File.Exists(java)) return Checked(new JavaHost(javac, java));
        }
        if (OnPath("javac" + exe) is { } onPath)
        {
            string resolved = new FileInfo(onPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? onPath;
            string java = Path.Combine(Path.GetDirectoryName(resolved)!, "java" + exe);
            return Checked(new JavaHost(resolved, File.Exists(java) ? java : (OnPath("java" + exe) ?? java)));
        }
        throw new JavaUnavailableException("java-missing", "No Java Development Kit (JDK) was found. " + InstallAdvice());
    }

    public static string InstallAdvice() =>
        $"Install JDK {MinimumVersion} or later — Eclipse Temurin from adoptium.net is free — or set dsaPlayback.javaHome to one you have.";

    private static JavaHost Checked(JavaHost host)
    {
        string version = host.Version();
        var major = Regex.Match(version, @"javac (?:1\.)?(\d+)");
        if (!major.Success)
            throw new JavaUnavailableException("java-missing", $"{host.Javac} did not run as a JDK ({version.Trim()}). " + InstallAdvice());
        if (int.Parse(major.Groups[1].Value) < MinimumVersion)
            throw new JavaUnavailableException("java-version",
                $"DSA Playback needs JDK {MinimumVersion} or later; {host.Javac} is {version.Trim()}. " + InstallAdvice());
        return host;
    }

    private static string? OnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private string Version()
    {
        if (_version is not null) return _version;
        // The first JVM a machine starts can be slow — on a cold Windows runner, over 30 s — so
        // asking its version is allowed as long as compiling is.
        var (_, output) = Execute(Javac, new[] { "-version" }, null, CompileTimeout);
        return _version = output.Trim();
    }

    private static string CacheFolder => Path.Combine(Path.GetTempPath(), "dsaviz-java");

    public static string Hash(string text) => Rust.RustHost.Hash(text);

    // ---- the runtime -----------------------------------------------------------------------------

    private string? _runtime;

    /// <summary>The runtime's classes for this JDK, compiled once and kept.</summary>
    private string Runtime()
    {
        if (_runtime is not null) return _runtime;
        string source = File.ReadAllText(Path.Combine(RuntimeFolder, RuntimeName));
        string folder = Path.Combine(CacheFolder, "runtime-" + Hash(Version() + "\n" + source)[..16]);
        if (File.Exists(Path.Combine(folder, "Dsaviz.class"))) return _runtime = folder;

        string building = folder + "-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(building);
        var (exit, output) = Execute(Javac, new[]
        {
            "--release", MinimumVersion.ToString(), "-nowarn", "-encoding", "UTF-8", "-d", building, Path.Combine(RuntimeFolder, RuntimeName)
        }, null, CompileTimeout);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Building the Java runtime failed:\n{output}");
            throw new NativeRunFailedException("The visualizer's Java runtime did not compile with this JDK. This is a bug in the visualizer, not in your code.");
        }
        // Another engine may have finished first; either copy is the same.
        try { Directory.Move(building, folder); }
        catch (IOException) { Directory.Delete(building, recursive: true); }
        return _runtime = folder;
    }

    // ---- compiling -------------------------------------------------------------------------------------

    /// <summary>
    /// Compiles <paramref name="source"/> as <paramref name="fileName"/>, or finds it already
    /// compiled. <paramref name="raw"/> asks javac for its machine-readable diagnostics, which
    /// carry a column and a key for each error; otherwise the messages are the ones a person reads.
    /// </summary>
    public JavaCompileResult Compile(string source, string fileName, bool raw = true)
    {
        string runtime = Runtime();
        string folder = Path.Combine(CacheFolder, Hash(Version() + "\n" + runtime + "\n" + fileName + "\n" + source)[..16]);
        string classes = Path.Combine(folder, "classes");
        string done = Path.Combine(folder, "compiled");
        if (File.Exists(done)) return new JavaCompileResult(classes, Array.Empty<JavaDiagnostic>());

        Directory.CreateDirectory(classes);
        string file = Path.Combine(folder, fileName);
        File.WriteAllText(file, source);
        CleanOldBuilds();

        // String + compiled as StringBuilder calls, not invokedynamic: the first + of every call
        // site otherwise links a method handle, allocating ~150 KB that is not the solution's.
        var arguments = new List<string> { "-nowarn", "-encoding", "UTF-8", "-proc:none", "-g", "-XDstringConcat=inline", "-cp", runtime, "-d", classes };
        if (raw) arguments.Add("-XDrawDiagnostics");
        arguments.Add(file);
        var (exit, output) = Execute(Javac, arguments, folder, CompileTimeout);
        if (exit == 0)
        {
            File.WriteAllText(done, "");
            return new JavaCompileResult(classes, Array.Empty<JavaDiagnostic>());
        }

        var errors = raw ? RawDiagnostics(output, fileName) : Diagnostics(output, fileName);
        if (errors.Count == 0)
        {
            Console.Error.WriteLine($"javac failed (exit {exit}):\n{output}");
            errors.Add(new JavaDiagnostic("", $"javac stopped with exit code {exit}.", 0, 0));
        }
        return new JavaCompileResult(null, errors);
    }

    /// <summary>"Solution.java:12:5: compiler.err.var.might.not.have.been.initialized: x"</summary>
    private static List<JavaDiagnostic> RawDiagnostics(string output, string fileName)
    {
        var errors = new List<JavaDiagnostic>();
        foreach (Match m in Regex.Matches(output, $@"^{Regex.Escape(fileName)}:(\d+):(\d+): (compiler\.err\.[\w.]+): ?(.*)$", RegexOptions.Multiline))
            errors.Add(new JavaDiagnostic(m.Groups[3].Value, m.Groups[4].Value.Trim(), int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        return errors;
    }

    /// <summary>
    /// "Solution.java:12: error: cannot find symbol", then the line, a caret under the column,
    /// and details such as "symbol: variable m".
    /// </summary>
    private static List<JavaDiagnostic> Diagnostics(string output, string fileName)
    {
        var errors = new List<JavaDiagnostic>();
        var lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], $@"^.*{Regex.Escape(fileName)}:(\d+): error: (.*)$");
            if (!m.Success) continue;
            string message = m.Groups[2].Value.Trim();
            int column = 0;
            for (int k = i + 1; k < lines.Length && k <= i + 6; k++)
            {
                if (Regex.IsMatch(lines[k], @"^.*\.java:\d+: ")) break;
                if (lines[k].TrimEnd().EndsWith('^') && lines[k].Trim() == "^") { column = lines[k].IndexOf('^') + 1; continue; }
                var detail = Regex.Match(lines[k], @"^\s+(symbol|location|required|found|reason):\s+(.*)$");
                if (detail.Success && detail.Groups[1].Value is "symbol" or "required" or "found")
                    message += $" ({detail.Groups[1].Value}: {detail.Groups[2].Value.Trim()})";
            }
            errors.Add(new JavaDiagnostic("", message, int.Parse(m.Groups[1].Value), column));
        }
        return errors;
    }

    // ---- a repair, remembered --------------------------------------------------------------------------

    public Dictionary<int, string>? Remembered(string key)
    {
        string path = Path.Combine(CacheFolder, $"repair-{key[..16]}.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public void Remember(string key, Dictionary<int, string> left)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(Path.Combine(CacheFolder, $"repair-{key[..16]}.json"), JsonSerializer.Serialize(left));
        }
        catch (IOException ex) { Console.Error.WriteLine($"Could not remember a Java repair: {ex.Message}"); }
    }

    private static (int Exit, string Output) Execute(string program, IEnumerable<string> arguments, string? folder, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (folder is not null) start.WorkingDirectory = folder;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // javac's own messages in English, the language the rest of the visualizer speaks.
        start.Environment["JAVA_TOOL_OPTIONS"] = "-Duser.language=en -Duser.country=US";

        using var process = Process.Start(start) ?? throw new NativeRunFailedException($"Could not start {program}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new NativeRunFailedException($"Compiling took longer than {timeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }
        // JAVA_TOOL_OPTIONS announces itself on stderr; that line is not javac's.
        string text = Regex.Replace(output.Result + errors.Result, @"^Picked up JAVA_TOOL_OPTIONS:.*\n?", "", RegexOptions.Multiline);
        return (process.ExitCode, text);
    }

    private static void CleanOldBuilds()
    {
        try
        {
            foreach (var folder in Directory.GetDirectories(CacheFolder))
                if (!Path.GetFileName(folder).StartsWith("runtime-") && Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove an old Java build: {ex.Message}");
        }
    }
}
