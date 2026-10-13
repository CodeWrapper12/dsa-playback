using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DsaViz.Engine.Contracts;
using DsaViz.Engine.Inputs;
using DsaViz.Engine.Measurement;

namespace DsaViz.Engine.Native;

/// <summary>The program did not answer: it was stopped, or failed in a way that is the visualizer's bug.</summary>
public sealed class NativeRunFailedException : Exception
{
    public bool TimedOut { get; }
    public NativeRunFailedException(string message, bool timedOut = false) : base(message) => TimedOut = timedOut;
}

/// <summary>A compiled program — C++ or Rust — measured at any size: one process per run.</summary>
public sealed class NativeTarget : IRunTarget
{
        private readonly string _executable;
        private readonly IReadOnlyList<StatementInfo> _statements;
        private readonly IReadOnlyList<LibraryCallSite> _sites;
        private readonly string _language;
        private readonly int _depthLimit;
        private readonly TimeSpan _timeout;

        /// <summary>For a program run through a launcher, what goes before the run's own arguments.</summary>
        public IReadOnlyList<string>? Launcher { get; init; }

        public NativeTarget(string executable, IReadOnlyList<StatementInfo> statements, IReadOnlyList<LibraryCallSite> sites,
            IReadOnlyList<SolutionParameter> parameters, string language, int depthLimit, TimeSpan timeout)
        {
            _executable = executable;
            _statements = statements;
            _sites = sites;
            Parameters = parameters;
            _language = language;
            _depthLimit = depthLimit;
            _timeout = timeout;
        }

        public IReadOnlyList<SolutionParameter> Parameters { get; }
        public bool Usable => true;
        public void Dispose() { }

        public TraceResult Run(Func<object?[]> arguments, long budget)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<string> lines;
            int exit;
            try
            {
                lines = NativeRun.Run(_executable, stepping: false, NativeRun.Inputs(arguments()), budget, _depthLimit, 0, _timeout, out exit, Launcher);
            }
            catch (NativeRunFailedException ex) when (ex.TimedOut)
            {
                return Runner.Failure(RunStatus.TimedOut, new[]
                {
                    $"The run did not finish within {_timeout.TotalSeconds:0} seconds. The statement budget cannot stop a " +
                    "single statement that never returns — a sleep, or one enormous library call."
                });
            }
            var end = NativeRun.ReadEnding(lines, exit, _statements);
            var statements = _statements.Select(s => new StatementCost(s.Id, s.Line, s.Column, s.Length, s.Snippet,
                    end.Hits.ElementAtOrDefault(s.Id), end.Bytes.ElementAtOrDefault(s.Id))
                {
                    LibraryWork = end.Work.ElementAtOrDefault(s.Id),
                    LargestAllocation = end.Largest.ElementAtOrDefault(s.Id)
                })
                .OrderBy(s => s.Line).ThenBy(s => s.Column).ToList();

            return new TraceResult
            {
                Status = end.Status,
                Statements = statements,
                Lines = Runner.LinesOf(statements),
                TotalOperations = end.Operations,
                TotalStatements = end.StatementsRun,
                TotalBytesAllocated = end.Allocated,
                MaxRecursionDepth = end.MaxDepth,
                // Measured directly by the C++ and Rust runtimes; Java's peak is inferred, as C#'s is.
                PeakBytes = end.Peak >= 0 ? end.Peak : null,
                ElapsedMs = started.Elapsed.TotalMilliseconds,
                LibraryCalls = _sites.Select((site, i) => new LibraryCall(site.Site, site.Line, site.Column, site.Member, site.Cost,
                        end.Sites.ElementAtOrDefault(i).Calls, end.Sites.ElementAtOrDefault(i).Work))
                    .Where(c => c.Calls > 0).ToList(),
                Errors = end.Errors,
                ErrorLine = end.ErrorLine,
                ReturnValue = end.Result,
                Language = _language
            };
        }
}

/// <summary>
/// The protocol a compiled C++ or Rust program speaks: inputs in on stdin, one per line in
/// LeetCode's format; a JSON object per step, per return, and a last one saying how the run
/// ended and what it counted, out on stdout.
/// </summary>
public static class NativeRun
{
    /// <summary>The text each argument is passed as: LeetCode's format, never capped.</summary>
    public static IReadOnlyList<string> Inputs(object?[] values) => values.Select(v => Literals.Format(v, int.MaxValue)).ToList();

    /// <summary>What a run's last line said: how it ended and what it counted.</summary>
    public sealed record Ending(RunStatus Status, string? Result, IReadOnlyList<string> Errors, int? ErrorLine)
    {
        public long Operations { get; init; }
        public long StatementsRun { get; init; }
        public long Allocated { get; init; }
        public long Peak { get; init; }
        public int MaxDepth { get; init; }
        public bool Truncated { get; init; }
        public IReadOnlyList<long> Hits { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Bytes { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Largest { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Work { get; init; } = Array.Empty<long>();
        public IReadOnlyList<(long Calls, long Work)> Sites { get; init; } = Array.Empty<(long, long)>();
    }

    /// <summary>
    /// Reads the program's last line. A program that ended without one crashed in a way its
    /// own handler could not report — a stack overflow, or being killed — and the exit code
    /// is what is left to explain it.
    /// </summary>
    /// <param name="sourceLines">
    /// Lines in the student's file. A program can name the exact line it failed on — a Rust
    /// panic does — and a line beyond the student's file is the visualizer's own code.
    /// </param>
    public static Ending ReadEnding(IReadOnlyList<string> lines, int exitCode, IReadOnlyList<StatementInfo> statements,
        int sourceLines = int.MaxValue)
    {
        var last = lines.LastOrDefault(l => l.StartsWith("{\"end\"", StringComparison.Ordinal));
        if (last is null)
        {
            string what = exitCode switch
            {
                -1073741571 or unchecked((int)0xC00000FD) => "Your code crashed: the stack overflowed. Very deep recursion causes this.",
                -1073741819 or unchecked((int)0xC0000005) or 139 or -11 => "Your code crashed with a segmentation fault",
                _ => $"Your code stopped unexpectedly (exit code {exitCode})"
            };
            return new Ending(RunStatus.RuntimeError, null, new[] { what + "." }, null);
        }

        using var document = JsonDocument.Parse(last);
        var root = document.RootElement;
        string status = root.GetProperty("end").GetString() ?? "";
        int statement = root.TryGetProperty("statement", out var at) ? at.GetInt32() : -1;
        int? line = statement >= 0 && statement < statements.Count ? statements[statement].Line : null;
        if (root.TryGetProperty("line", out var exact) && exact.GetInt32() is var named and >= 1 && named <= sourceLines) line = named;
        string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";

        var (runStatus, errors) = status switch
        {
            "Completed" => (RunStatus.Completed, Array.Empty<string>()),
            "BudgetExceeded" => (RunStatus.BudgetExceeded, new[] { error }),
            "RecursionLimitExceeded" => (RunStatus.RecursionLimitExceeded, new[] { error }),
            "Crashed" => (RunStatus.RuntimeError, new[] { Crash(error, line) }),
            "RuntimeError" => (RunStatus.RuntimeError, new[] { line is { } l ? InsertLine(error, l) : error }),
            "InvalidInput" => (RunStatus.InvalidInput, new[] { error }),
            "InputError" => (RunStatus.RuntimeError, new[] { $"The visualizer could not build the input for your function ({error}). This is a bug in the visualizer." }),
            _ => (RunStatus.RuntimeError, new[] { error })
        };
        static IReadOnlyList<long> Numbers(JsonElement root, string name)
            => root.TryGetProperty(name, out var array) ? array.EnumerateArray().Select(x => x.GetInt64()).ToList() : Array.Empty<long>();

        return new Ending(runStatus,
            root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null,
            errors, status is "Completed" ? null : line)
        {
            Operations = root.GetProperty("ops").GetInt64(),
            StatementsRun = root.GetProperty("statements").GetInt64(),
            Allocated = root.GetProperty("allocated").GetInt64(),
            Peak = root.GetProperty("peak").GetInt64(),
            MaxDepth = root.GetProperty("maxDepth").GetInt32(),
            Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean(),
            Hits = Numbers(root, "hits"),
            Bytes = Numbers(root, "bytes"),
            Largest = Numbers(root, "largest"),
            Work = Numbers(root, "work"),
            Sites = root.TryGetProperty("sites", out var sites)
                ? sites.EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList()
                : Array.Empty<(long, long)>()
        };
    }

    /// <summary>"Your code threw out_of_range: vector" → "Your code threw out_of_range at line 12: vector".</summary>
    private static string InsertLine(string error, int line)
    {
        int colon = error.IndexOf(':');
        return colon < 0 ? $"{error} at line {line}" : $"{error[..colon]} at line {line}{error[colon..]}";
    }

    private static string Crash(string what, int? line)
        => (line is { } l ? $"{what} at line {l}" : what) +
           ". Reading through a null pointer, or past the end of an array or vector, usually causes this.";

    // ---- recording ---------------------------------------------------------------------------------

    /// <summary>
    /// Turns a recorded run's output — a full snapshot per step — into steps as C# and
    /// Python send them: objects by small ids, and each object only when it changed.
    /// </summary>
    public sealed class Recording
    {
        private readonly IReadOnlyList<StatementInfo> _statements;
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, HeapObject> _sent = new();

        public List<ExecutionStep> Steps { get; } = new();
        public List<ReturnEvent> Returns { get; } = new();

        /// <summary>A design problem's calls: each one's number and the step count when it started.</summary>
        public List<(int Index, int Step)> CallStarts { get; } = new();

        public Recording(IReadOnlyList<StatementInfo> statements) => _statements = statements;

        public void Read(IReadOnlyList<string> lines)
        {
            foreach (var line in lines.Where(l => l.StartsWith("{\"call\"", StringComparison.Ordinal)))
            {
                using var call = JsonDocument.Parse(line);
                CallStarts.Add((call.RootElement.GetProperty("call").GetInt32(), call.RootElement.GetProperty("step").GetInt32()));
            }
            foreach (var line in lines)
            {
                if (line.StartsWith("{\"s\"", StringComparison.Ordinal)) Step(line);
                else if (line.StartsWith("{\"ret\"", StringComparison.Ordinal)) Returned(line);
            }
        }

        private int Id(string address)
        {
            if (!_ids.TryGetValue(address, out int id)) { id = _ids.Count + 1; _ids[address] = id; }
            return id;
        }

        private void Step(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            int statementId = root.GetProperty("s").GetInt32();
            var info = statementId < _statements.Count ? _statements[statementId] : new StatementInfo(statementId, 0, 0, 0, "");

            var objects = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var o in root.GetProperty("o").EnumerateArray()) objects[o.GetProperty("id").GetRawText()] = o;

            var variables = root.GetProperty("v").EnumerateArray().Select(v => Variable(v, objects)).ToList();
            var changed = new List<HeapObject>();
            foreach (var (address, element) in objects)
            {
                var current = Describe(Id(address), element);
                if (_sent.TryGetValue(current.Id, out var previous))
                {
                    if (HeapRecorder.Same(previous, current)) continue;
                    changed.Add(HeapRecorder.AsPatch(previous, current) ?? current);
                }
                else changed.Add(current);
                _sent[current.Id] = current;
            }

            Steps.Add(new ExecutionStep(Steps.Count, statementId, info.Line, info.Column, root.GetProperty("d").GetInt32(), info.Method, variables)
            {
                Heap = changed.Count > 0 ? changed : null,
                Allocated = root.GetProperty("a").GetInt64()
            });
        }

        private void Returned(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Returns.Add(new ReturnEvent(root.GetProperty("after").GetInt32(), root.GetProperty("ret").GetInt32(), Value(root.GetProperty("v"))));
        }

        private StepVariable Variable(JsonElement v, Dictionary<string, JsonElement> objects)
        {
            string name = v.GetProperty("n").GetString()!;
            var value = v.GetProperty("v");
            string text = value.GetProperty("x").GetString() ?? "";

            if (value.TryGetProperty("r", out var reference))
            {
                string address = reference.GetRawText();
                string kind = objects.TryGetValue(address, out var o) ? o.GetProperty("k").GetString()! : "object";
                string type = objects.TryGetValue(address, out var described) && described.TryGetProperty("t", out var t) ? t.GetString()! : kind;
                string summary = kind is "tree" or "listnode" ? text : $"{type} {text}";
                return new StepVariable(name, summary, kind, null, null) { Ref = Id(address) };
            }
            if (value.TryGetProperty("num", out var number))
                return new StepVariable(name, text, "scalar", null, number.GetInt64());
            if (text == "null") return new StepVariable(name, "null", "null", null, null);
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                string inner = text[1..^1];
                return new StepVariable(name, text, "string", inner.Length <= 40 ? inner.Select(c => c.ToString()).ToList() : null, null);
            }
            return new StepVariable(name, text, "scalar", null, null);
        }

        private HeapValue Value(JsonElement value)
        {
            string text = value.TryGetProperty("x", out var x) ? x.GetString() ?? "" : "";
            if (value.TryGetProperty("r", out var reference)) return new HeapValue(text) { Ref = Id(reference.GetRawText()) };
            if (value.TryGetProperty("num", out var number)) return new HeapValue(text) { Number = number.GetInt64() };
            return new HeapValue(text);
        }

        private HeapObject Describe(int id, JsonElement o)
        {
            var described = new HeapObject(id, o.GetProperty("k").GetString()!, o.TryGetProperty("t", out var t) ? t.GetString()! : "")
            {
                Count = o.TryGetProperty("c", out var c) ? c.GetInt32() : 0,
                Bytes = o.TryGetProperty("b", out var b) ? b.GetInt64() : 0,
                Arity = o.TryGetProperty("ar", out var ar) ? ar.GetInt32() : null
            };
            if (o.TryGetProperty("i", out var items)) described = described with { Items = items.EnumerateArray().Select(Value).ToList() };
            if (o.TryGetProperty("e", out var entries))
                described = described with { Entries = entries.EnumerateArray().Select(e => new HeapEntry(Value(e[0]), Value(e[1]))).ToList() };
            if (o.TryGetProperty("f", out var fields))
                described = described with { Fields = fields.EnumerateArray().Select(f => new HeapField(f[0].GetString()!, Value(f[1]))).ToList() };
            return described;
        }
    }

    // ---- running ------------------------------------------------------------------------------

    /// <summary>
    /// Runs a built program: `run` to measure, `step` to record. Inputs go in one per line,
    /// in LeetCode's format; what comes out is one JSON object per line.
    /// </summary>
    /// <param name="launcher">
    /// For a program run through a launcher — Java's: the launcher is <paramref name="executable"/>,
    /// and these come before the run's own arguments.
    /// </param>
    public static IReadOnlyList<string> Run(string executable, bool stepping, IReadOnlyList<string> inputs,
        long budget, int depthLimit, int maxSteps, TimeSpan timeout, out int exitCode, IReadOnlyList<string>? launcher = null)
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
        foreach (var argument in launcher ?? Array.Empty<string>()) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(stepping ? "step" : "run");
        start.ArgumentList.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(depthLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(maxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new NativeRunFailedException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        foreach (var input in inputs) process.StandardInput.WriteLine(input);
        process.StandardInput.Close();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new NativeRunFailedException(
                $"The run did not finish within {timeout.TotalSeconds:0} seconds and was stopped.", timedOut: true);
        }
        exitCode = process.ExitCode;
        if (errors.Result.Length > 0) Console.Error.WriteLine($"C++ program stderr: {errors.Result.Trim()}");
        return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
