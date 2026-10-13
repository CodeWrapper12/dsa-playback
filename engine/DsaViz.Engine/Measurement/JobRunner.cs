using DsaViz.Engine.Contracts;

namespace DsaViz.Engine.Measurement;

/// <summary>
/// Runs a measurement request from the editor, optionally against a second solution.
/// Lives here rather than in the CLI entry point so it can be tested directly.
/// </summary>
public static class JobRunner
{
    public static JobResponse Run(JobRequest request, Func<string, bool>? exists = null,
        Func<string, string>? read = null)
    {
        exists ??= File.Exists;
        read ??= File.ReadAllText;

        if (!exists(request.File))
            return new JobResponse { Errors = new[] { $"File not found: {request.File}" } };

        try
        {
            return RunJob(request, exists, read);
        }
        catch (Python.PythonUnavailableException ex)
        {
            // Nothing runs without Python, and the editor can do something about that.
            return new JobResponse { Errors = new[] { ex.Message }, ErrorCode = ex.Code };
        }
        catch (Python.PythonRunFailedException ex)
        {
            return new JobResponse { Errors = new[] { ex.Message } };
        }
        catch (Rust.RustUnavailableException ex)
        {
            // Nothing runs without rustc, or without the linker it needs, and the editor can say how to get them.
            return new JobResponse { Errors = new[] { ex.Message }, ErrorCode = ex.Code };
        }
        catch (Cpp.CppUnavailableException ex)
        {
            // Nothing runs without a compiler, and the editor can say how to get one.
            return new JobResponse { Errors = new[] { ex.Message }, ErrorCode = ex.Code };
        }
        catch (Native.NativeRunFailedException ex)
        {
            return new JobResponse { Errors = new[] { ex.Message } };
        }
    }

    public static bool IsPython(string file) => file.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    private static JobResponse RunJob(JobRequest request, Func<string, bool> exists, Func<string, string> read)
    {
        var errors = new List<string>();

        string primarySource = read(request.File);
        Python.PythonHost? python = null;
        Python.PythonHost PythonHost() => python ??= Python.PythonHost.Locate(request.Python);
        Cpp.CppHost? cpp = null;
        Cpp.CppHost CppHost() => cpp ??= Cpp.CppHost.Locate(request.CppCompiler);
        bool isCpp = Cpp.CppSolution.IsCpp(request.File);
        Rust.RustHost? rust = null;
        Rust.RustHost RustHost() => rust ??= Rust.RustHost.Locate(request.Rustc);
        bool isRust = Rust.RustSolution.IsRust(request.File);

        if (request.AllExamples)
        {
            var report = IsPython(request.File)
                ? Stepper.RunExamplesPython(PythonHost(), primarySource, request.Entry, request.Budget)
                : isCpp
                ? Stepper.RunExamplesCpp(CppHost(), primarySource, request.Entry, request.Budget)
                : isRust
                ? Stepper.RunExamplesRust(RustHost(), primarySource, request.Entry, request.Budget)
                : Stepper.RunExamples(DsaViz.Engine.Analysis.SourceAnalysis.Create(primarySource), request.Entry, request.Budget);
            return new JobResponse
            {
                Examples = report,
                Source = primarySource,
                Label = Path.GetFileNameWithoutExtension(request.File)
            };
        }

        if (request.StepSize is { } stepSize)
        {
            var steps = IsPython(request.File)
                ? Stepper.RecordPython(PythonHost(), primarySource, request.Entry, request.Inputs,
                    stepSize, request.MaxSteps, request.Strategy, request.Budget, request.Values, request.Example)
                : isCpp
                ? Stepper.RecordCpp(CppHost(), primarySource, request.Entry, request.Inputs,
                    stepSize, request.MaxSteps, request.Strategy, request.Budget, request.Values, request.Example)
                : isRust
                ? Stepper.RecordRust(RustHost(), primarySource, request.Entry, request.Inputs,
                    stepSize, request.MaxSteps, request.Strategy, request.Budget, request.Values, request.Example)
                : Stepper.Record(
                    DsaViz.Engine.Analysis.SourceAnalysis.Create(primarySource), request.Entry, request.Inputs,
                    stepSize, request.MaxSteps, request.Strategy, request.Budget, request.Values, request.Example);
            return new JobResponse
            {
                Steps = steps,
                Source = primarySource,
                Label = Path.GetFileNameWithoutExtension(request.File)
            };
        }

        MeasurementResult Measure(string file, string source, string entry, IReadOnlyList<InputSpec>? inputs,
            IReadOnlyList<int>? sizes, GenerationStrategy? strategy)
            => IsPython(file)
                ? Measurer.MeasurePython(PythonHost(), source, entry, inputs, sizes, request.Budget, strategy)
                : Cpp.CppSolution.IsCpp(file)
                ? Measurer.MeasureCpp(CppHost(), source, entry, inputs, sizes, request.Budget, strategy)
                : Rust.RustSolution.IsRust(file)
                ? Measurer.MeasureRust(RustHost(), source, entry, inputs, sizes, request.Budget, strategy)
                : Measurer.Measure(source, entry, inputs, sizes, request.Budget, strategy);

        var primary = Measure(request.File, primarySource, request.Entry, request.Inputs, request.Sizes, request.Strategy);

        MeasurementResult? comparison = null;
        string? comparisonSource = null;

        if (request.CompareFile is { } comparePath)
        {
            if (!exists(comparePath))
            {
                errors.Add($"Comparison file not found: {comparePath}");
            }
            else
            {
                comparisonSource = read(comparePath);

                // Both solutions must see the same sizes and the same generated data,
                // or the gap between the curves would partly be the inputs rather than
                // the algorithms. So the primary's resolved configuration is reused
                // instead of being inferred again from the second signature.
                comparison = Measure(
                    comparePath,
                    comparisonSource,
                    request.CompareEntry ?? request.Entry,
                    primary.Inputs.Count > 0 ? primary.Inputs : request.Inputs,
                    primary.Points.Count > 0 ? primary.Points.Select(p => p.Size).ToArray() : request.Sizes,
                    null);
            }
        }

        return new JobResponse
        {
            Primary = primary,
            Comparison = comparison,
            Source = primarySource,
            ComparisonSource = comparisonSource,
            Label = Path.GetFileNameWithoutExtension(request.File),
            ComparisonLabel = request.CompareFile is null
                ? null
                : Path.GetFileNameWithoutExtension(request.CompareFile),
            Errors = errors
        };
    }
}
