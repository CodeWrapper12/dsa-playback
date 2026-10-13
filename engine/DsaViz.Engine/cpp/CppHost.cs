using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DsaViz.Engine.Cpp;

/// <summary>Why C++ cannot be compiled at all, said so the editor can help: <c>cpp-missing</c>.</summary>
public sealed class CppUnavailableException : Exception
{
    public string Code { get; }
    public CppUnavailableException(string code, string message) : base(message) => Code = code;
}

/// <summary>The program did not answer: it was stopped, or failed in a way that is the visualizer's bug.</summary>
public sealed class CppRunFailedException : Exception
{
    public bool TimedOut { get; }
    public CppRunFailedException(string message, bool timedOut = false) : base(message) => TimedOut = timedOut;
}

/// <summary>A compile that failed: the compiler's errors, each as "line 12, column 5: message".</summary>
public sealed record CppCompileResult(string? Executable, IReadOnlyList<string> Errors, bool InVisualizerCode);

/// <summary>
/// The student's C++ compiler: found, used to build each program once, and the built
/// program run with a time limit. Builds are kept in the temporary folder by the hash of
/// what was compiled, so running the same code again — new values, another example —
/// does not compile it again.
/// </summary>
public sealed class CppHost
{
    public const string HeaderName = "dsaviz.hpp";

    /// <summary>The header ships beside the engine, in its cpp folder.</summary>
    public static string HeaderFolder => Path.Combine(AppContext.BaseDirectory, "cpp");

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(90);

    public string Compiler { get; }

    /// <summary>Extra flags that make this compiler work: the macOS standard-library fix, when it is needed.</summary>
    private List<string>? _fix;

    private CppHost(string compiler) => Compiler = compiler;

    /// <summary>
    /// The compiler to use: the one the editor names (the dsaPlayback.cppCompiler setting),
    /// then DSAVIZ_CXX, then clang++ or g++ on the path.
    /// </summary>
    public static CppHost Locate(string? requested = null)
    {
        if (!File.Exists(Path.Combine(HeaderFolder, HeaderName)))
            throw new InvalidOperationException($"The C++ runtime is missing from this installation: {HeaderFolder}");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        if (Environment.GetEnvironmentVariable("DSAVIZ_CXX") is { Length: > 0 } fromEnvironment) candidates.Add(fromEnvironment);
        candidates.AddRange(new[] { "clang++", "g++" });

        foreach (var candidate in candidates)
        {
            string? resolved = Path.IsPathRooted(candidate) ? (File.Exists(candidate) ? candidate : null) : OnPath(candidate);
            if (resolved is not null) return new CppHost(resolved);
        }

        throw new CppUnavailableException("cpp-missing", "No C++ compiler was found. " + InstallAdvice());
    }

    /// <summary>How to get a compiler on this system, in one sentence with the command.</summary>
    public static string InstallAdvice() =>
        OperatingSystem.IsMacOS() ? "Install Apple's command line tools by running: xcode-select --install"
        : OperatingSystem.IsWindows() ? "Install MSYS2 from msys2.org and its MinGW-w64 compiler (pacman -S mingw-w64-ucrt-x86_64-gcc), then add its bin folder to PATH."
        : "Install g++ — on Ubuntu or Debian: sudo apt install g++";

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

    // ---- compiling --------------------------------------------------------------------------

    private static readonly Regex Diagnostic = new(@"^(?<file>[^\n]*?):(?<line>\d+):(?:(?<column>\d+):)?\s*(?:fatal )?error:\s*(?<message>.*)$",
        RegexOptions.Multiline);

    private static string CacheFolder => Path.Combine(Path.GetTempPath(), "dsaviz-cpp");

    /// <summary>Compiles <paramref name="source"/>, or finds it already compiled.</summary>
    public CppCompileResult Compile(string source)
    {
        string header = File.ReadAllText(Path.Combine(HeaderFolder, HeaderName));
        string key = Hash(Compiler + "\n" + header + "\n" + source);
        string folder = Path.Combine(CacheFolder, key[..16]);
        string executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "solution.exe" : "solution");
        if (File.Exists(executable)) return new CppCompileResult(executable, Array.Empty<string>(), false);

        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "program.cpp");
        File.WriteAllText(file, source);
        CleanOldBuilds();

        _fix ??= RememberedFix();
        var (exit, output) = RunCompiler(file, executable, _fix ?? new List<string>());
        if (exit != 0 && _fix is null && OperatingSystem.IsMacOS() && MissingStandardHeader(output) && MacStandardLibrary() is { } fix)
        {
            // Command Line Tools left half-removed by a macOS update: clang looks for the C++
            // headers in a folder that no longer has them, though the SDK still does.
            Console.Error.WriteLine($"C++ standard headers not found by {Compiler}; using the SDK's: {string.Join(" ", fix)}");
            (exit, output) = RunCompiler(file, executable, fix);
            if (exit == 0) Remember(fix);
        }

        if (exit == 0 && File.Exists(executable)) return new CppCompileResult(executable, Array.Empty<string>(), false);

        var errors = new List<string>();
        bool visualizer = false;
        foreach (Match m in Diagnostic.Matches(output))
        {
            string where = Path.GetFileName(m.Groups["file"].Value);
            if (where != CppInstrumenter.SolutionFile) { visualizer = true; continue; }
            string column = m.Groups["column"].Success ? $", column {m.Groups["column"].Value}" : "";
            errors.Add($"line {m.Groups["line"].Value}{column}: {m.Groups["message"].Value.Trim()}");
        }
        // Errors only outside the student's file are the visualizer's: its header or its driver.
        bool ours = errors.Count == 0;
        if (ours)
        {
            Console.Error.WriteLine($"C++ compile failed (exit {exit}):\n{output}");
            errors.Add(output.Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim()
                       ?? $"The compiler stopped with exit code {exit}.");
        }
        return new CppCompileResult(null, errors.Distinct().Take(10).ToList(), ours || (visualizer && errors.Count == 0));
    }

    private (int Exit, string Output) RunCompiler(string file, string executable, IReadOnlyList<string> fix)
    {
        var start = new ProcessStartInfo(Compiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(file)!
        };
        // MinGW's g++ runs helper programs that load DLLs from its own folder, which Windows
        // only finds on PATH. A student who names the compiler in the setting, without that
        // folder on PATH, would otherwise see every compile fail with no error at all.
        if (Path.GetDirectoryName(Compiler) is { Length: > 0 } folder)
            start.Environment["PATH"] = folder + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        foreach (var flag in new[] { "-std=c++17", "-O1", "-w", "-I", HeaderFolder }) start.ArgumentList.Add(flag);
        foreach (var flag in fix) start.ArgumentList.Add(flag);
        // Deep recursion needs a deep stack. Elsewhere the header runs the solution on a
        // thread it gives a deep stack; on Windows the main thread's is set when linking,
        // with whichever linker the compiler uses: MinGW's, or Microsoft's for a clang
        // that targets Visual Studio's libraries.
        if (OperatingSystem.IsWindows())
        {
            bool msvc = TargetsMsvc();
            start.ArgumentList.Add(msvc ? "-Wl,/STACK:536870912" : "-Wl,--stack,536870912");
            // MinGW's runtime is linked in, so the program runs without g++'s folder on PATH.
            if (!msvc) start.ArgumentList.Add("-static");
        }
        else if (OperatingSystem.IsLinux()) start.ArgumentList.Add("-pthread");
        start.ArgumentList.Add(file);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(executable);

        using var process = Process.Start(start) ?? throw new CppRunFailedException($"Could not start {Compiler}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CompileTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new CppRunFailedException($"Compiling took longer than {CompileTimeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }
        return (process.ExitCode, output.Result + errors.Result);
    }

    /// <summary>Where the macOS fix is remembered for this compiler, so later engines do not find it again.</summary>
    private string FixFile => Path.Combine(CacheFolder, $"flags-{Hash(Compiler)[..12]}.txt");

    private List<string>? RememberedFix()
    {
        try
        {
            return System.IO.File.Exists(FixFile) ? System.IO.File.ReadAllLines(FixFile).Where(l => l.Length > 0).ToList() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Remember(List<string> fix)
    {
        _fix = fix;
        try { System.IO.File.WriteAllLines(FixFile, fix); }
        catch (IOException ex) { Console.Error.WriteLine($"Could not remember the compiler fix: {ex.Message}"); }
    }

    private bool? _msvc;

    /// <summary>Whether this compiler builds for Microsoft's toolchain: its target triple says msvc.</summary>
    private bool TargetsMsvc()
    {
        if (_msvc is { } known) return known;
        try
        {
            var start = new ProcessStartInfo(Compiler, "-dumpmachine") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start);
            string target = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(10_000);
            _msvc = target.Contains("msvc", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _msvc = false;
        }
        return _msvc.Value;
    }

    private static bool MissingStandardHeader(string output)
        => Regex.IsMatch(output, @"fatal error: '(algorithm|vector|string|cstdio|iostream|cstdlib)' file not found");

    private static List<string>? MacStandardLibrary()
    {
        try
        {
            var start = new ProcessStartInfo("xcrun", "--show-sdk-path") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start);
            if (process is null) return null;
            string sdk = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            string headers = Path.Combine(sdk, "usr", "include", "c++", "v1");
            return File.Exists(Path.Combine(headers, "vector")) ? new List<string> { "-nostdinc++", "-isystem", headers } : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Builds older than a day are removed, so the temporary folder does not grow without end.</summary>
    private static void CleanOldBuilds()
    {
        try
        {
            foreach (var folder in Directory.GetDirectories(CacheFolder))
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException ex)
        {
            // Another engine may be using one of them; it is removed another time.
            Console.Error.WriteLine($"Could not remove an old C++ build: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Could not remove an old C++ build: {ex.Message}");
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- running ------------------------------------------------------------------------------

    /// <summary>
    /// Runs a built program: `run` to measure, `step` to record. Inputs go in one per line,
    /// in LeetCode's format; what comes out is one JSON object per line.
    /// </summary>
    public static IReadOnlyList<string> Run(string executable, bool stepping, IReadOnlyList<string> inputs,
        long budget, int depthLimit, int maxSteps, TimeSpan timeout, out int exitCode)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        start.ArgumentList.Add(stepping ? "step" : "run");
        start.ArgumentList.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(depthLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(maxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new CppRunFailedException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        foreach (var input in inputs) process.StandardInput.WriteLine(input);
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new CppRunFailedException(
                $"The run did not finish within {timeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }
        exitCode = process.ExitCode;
        if (errors.Result.Length > 0) Console.Error.WriteLine($"C++ program stderr: {errors.Result.Trim()}");
        return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
