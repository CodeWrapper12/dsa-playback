using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DsaViz.Engine.Native;

namespace DsaViz.Engine.Rust;

/// <summary>Why Rust cannot be compiled at all, said so the editor can help: <c>rust-missing</c> or <c>rust-linker</c>.</summary>
public sealed class RustUnavailableException : Exception
{
    public string Code { get; }
    public RustUnavailableException(string code, string message) : base(message) => Code = code;
}

/// <summary>One compiler error: its code (E0382), its message, where its main span is, and its other spans.</summary>
public sealed record RustDiagnostic(string? Code, string Message, int? ByteStart, int Line, int Column)
{
    /// <summary>The error's other places in the program, each with its label: "mutable borrow later used here".</summary>
    public IReadOnlyList<(int ByteStart, string Label)> Others { get; init; } = Array.Empty<(int, string)>();
}

public sealed record RustCompileResult(string? Executable, IReadOnlyList<RustDiagnostic> Errors);

/// <summary>
/// The student's Rust compiler: found, used to build the visualizer's runtime once per
/// compiler version and each program once, and builds kept in the temporary folder by the
/// hash of what was compiled.
/// </summary>
public sealed class RustHost
{
    public const string RuntimeName = "dsaviz.rs";

    public static string RuntimeFolder => Path.Combine(AppContext.BaseDirectory, "rust");

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);

    public string Compiler { get; }
    private string? _version;

    private RustHost(string compiler) => Compiler = compiler;

    /// <summary>The setting, DSAVIZ_RUSTC, rustc on the path, then rustup's own folder.</summary>
    public static RustHost Locate(string? requested = null)
    {
        if (!File.Exists(Path.Combine(RuntimeFolder, RuntimeName)))
            throw new InvalidOperationException($"The Rust runtime is missing from this installation: {RuntimeFolder}");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        if (Environment.GetEnvironmentVariable("DSAVIZ_RUSTC") is { Length: > 0 } fromEnvironment) candidates.Add(fromEnvironment);
        candidates.Add("rustc");
        // An editor started from the dock may not have ~/.cargo/bin on its PATH.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".cargo", "bin", OperatingSystem.IsWindows() ? "rustc.exe" : "rustc"));

        foreach (var candidate in candidates)
        {
            string? resolved = Path.IsPathRooted(candidate) ? (File.Exists(candidate) ? candidate : null) : OnPath(candidate);
            if (resolved is not null) return new RustHost(resolved);
        }
        throw new RustUnavailableException("rust-missing", "Rust was not found. " + InstallAdvice());
    }

    public static string InstallAdvice() => OperatingSystem.IsWindows()
        ? "Install it with rustup-init.exe from rustup.rs, and the Visual Studio C++ build tools when it asks."
        : "Install it with rustup by running: curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh";

    private static string LinkerAdvice() =>
        OperatingSystem.IsWindows()
            ? "Rust is installed, but the linker it needs is not: install Visual Studio Build Tools with \"Desktop development with C++\" (visualstudio.microsoft.com/downloads)."
            : OperatingSystem.IsMacOS()
            ? "Rust is installed, but the linker it needs is not. Install Apple's command line tools by running: xcode-select --install"
            : "Rust is installed, but the linker it needs is not. Install it — on Ubuntu or Debian: sudo apt install build-essential";

    private static string? OnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
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

    private static string CacheFolder => Path.Combine(Path.GetTempPath(), "dsaviz-rust");

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The compiler's full version: builds made by another are not reused.</summary>
    private string Version()
    {
        if (_version is not null) return _version;
        var (exit, output) = Execute(new[] { "-vV" }, null, TimeSpan.FromSeconds(30));
        if (exit != 0) throw new RustUnavailableException("rust-missing", $"{Compiler} did not run ({output.Trim()}). " + InstallAdvice());
        return _version = output.Trim();
    }

    // ---- the runtime ------------------------------------------------------------------------

    /// <summary>The runtime compiled as a library for this compiler, built once and kept.</summary>
    private string Runtime()
    {
        string source = File.ReadAllText(Path.Combine(RuntimeFolder, RuntimeName));
        string folder = Path.Combine(CacheFolder, "runtime-" + Hash(Version() + "\n" + source)[..16]);
        string library = Path.Combine(folder, "libdsaviz.rlib");
        if (File.Exists(library)) return library;

        Directory.CreateDirectory(folder);
        string building = Path.Combine(folder, $"building-{Guid.NewGuid():N}.rlib");
        var (exit, output) = Execute(new[]
        {
            "--edition", "2021", "--crate-type", "rlib", "--crate-name", "dsaviz", "-A", "warnings",
            "-C", "opt-level=1", "-C", "debuginfo=0", Path.Combine(RuntimeFolder, RuntimeName), "-o", building
        }, folder, CompileTimeout);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Building the Rust runtime failed:\n{output}");
            throw new NativeRunFailedException("The visualizer's Rust runtime did not compile with this version of Rust. " +
                                               "This is a bug in the visualizer, not in your code.");
        }
        // Another engine may have finished first; either copy is the same.
        try { File.Move(building, library); }
        catch (IOException) { File.Delete(building); }
        return library;
    }

    // ---- compiling ------------------------------------------------------------------------------

    /// <summary>Compiles <paramref name="source"/>, or finds it already compiled.</summary>
    /// <param name="overflowChecks">
    /// On, an overflow panics, as in a debug build — what a student stepping through their own
    /// input should see. Off, it wraps, as in a release build: measuring at sizes far beyond a
    /// problem's limits must not stop at an i32 that LeetCode's inputs would never overflow.
    /// </param>
    public RustCompileResult Compile(string source, bool overflowChecks = true)
    {
        source = (overflowChecks ? "" : "// overflow wraps\n") + source;
        string runtime = Runtime();
        string folder = Path.Combine(CacheFolder, Hash(Version() + "\n" + runtime + "\n" + source)[..16]);
        string executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "solution.exe" : "solution");
        if (File.Exists(executable)) return new RustCompileResult(executable, Array.Empty<RustDiagnostic>());

        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, RustInstrumenter.ProgramFile);
        File.WriteAllText(file, source);
        CleanOldBuilds();

        var (exit, output) = Execute(new[]
        {
            "--edition", "2021", "--crate-type", "bin", "--crate-name", "solution", "-A", "warnings", "--error-format=json",
            "-C", "opt-level=0", "-C", "debuginfo=0", "-C", $"overflow-checks={(overflowChecks ? "on" : "off")}",
            "--extern", $"dsaviz={runtime}", "-L", $"dependency={Path.GetDirectoryName(runtime)}",
            file, "-o", executable
        }, folder, CompileTimeout);
        if (exit == 0 && File.Exists(executable)) return new RustCompileResult(executable, Array.Empty<RustDiagnostic>());

        var errors = Diagnostics(output);
        if (errors.Any(e => Regex.IsMatch(e.Message, @"linker `[^`]+` not found|linking with `[^`]+` failed|link\.exe")))
            throw new RustUnavailableException("rust-linker", LinkerAdvice());
        if (errors.Count == 0)
        {
            Console.Error.WriteLine($"rustc failed (exit {exit}):\n{output}");
            errors.Add(new RustDiagnostic(null, $"The Rust compiler stopped with exit code {exit}.", null, 0, 0));
        }
        return new RustCompileResult(null, errors);
    }

    private static List<RustDiagnostic> Diagnostics(string output)
    {
        var errors = new List<RustDiagnostic>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("level").GetString() != "error") continue;
                string message = root.GetProperty("message").GetString() ?? "";
                if (message.StartsWith("aborting due to", StringComparison.Ordinal)) continue;
                string? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("code").GetString() : null;
                var spans = root.GetProperty("spans").EnumerateArray()
                    .Where(s => s.GetProperty("file_name").GetString()!.EndsWith(RustInstrumenter.ProgramFile)).ToList();
                var others = spans.Where(s => !s.GetProperty("is_primary").GetBoolean())
                    .Select(s => (s.GetProperty("byte_start").GetInt32(),
                                  s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : ""))
                    .ToList();
                var primary = spans.FirstOrDefault(s => s.GetProperty("is_primary").GetBoolean());
                if (primary.ValueKind == JsonValueKind.Object)
                    errors.Add(new RustDiagnostic(code, message, primary.GetProperty("byte_start").GetInt32(),
                        primary.GetProperty("line_start").GetInt32(), primary.GetProperty("column_start").GetInt32()) { Others = others });
                else
                    errors.Add(new RustDiagnostic(code, message, null, 0, 0));
            }
            catch (JsonException)
            {
                // Not one of rustc's messages: the linker's own output, already in the log.
            }
        }
        return errors;
    }

    // ---- a repair, remembered ---------------------------------------------------------------------

    /// <summary>The reads left out of the program with this key the last time it was built.</summary>
    public Dictionary<int, string>? Remembered(string key)
    {
        string path = Path.Combine(CacheFolder, $"repair-{key[..16]}.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Remember(string key, Dictionary<int, string> left)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(Path.Combine(CacheFolder, $"repair-{key[..16]}.json"), JsonSerializer.Serialize(left));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Could not remember a Rust repair: {ex.Message}");
        }
    }

    private (int Exit, string Output) Execute(IEnumerable<string> arguments, string? folder, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(Compiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (folder is not null) start.WorkingDirectory = folder;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // rustup's rustc is a proxy that finds the real one through PATH; its folder must be on it.
        if (Path.GetDirectoryName(Compiler) is { Length: > 0 } own)
            start.Environment["PATH"] = own + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");

        using var process = Process.Start(start) ?? throw new NativeRunFailedException($"Could not start {Compiler}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new NativeRunFailedException($"Compiling took longer than {timeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }
        return (process.ExitCode, output.Result + errors.Result);
    }

    /// <summary>Builds older than a day are removed, so the temporary folder does not grow without end.</summary>
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
            Console.Error.WriteLine($"Could not remove an old Rust build: {ex.Message}");
        }
    }
}
