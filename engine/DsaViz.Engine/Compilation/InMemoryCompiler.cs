using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DsaViz.Runtime;

namespace DsaViz.Engine.Compilation;

public sealed record CompilationOutcome(byte[]? Assembly, IReadOnlyList<string> Errors)
{
    public bool Success => Assembly is not null;
}

public static class InMemoryCompiler
{
    private static readonly Lazy<ReferenceSet> References = new(BuildReferences);

    private sealed record ReferenceSet(
        ImmutableArray<MetadataReference> Items,
        IReadOnlyList<string> Skipped);

    private static ReferenceSet BuildReferences()
    {
        var items = ImmutableArray.CreateBuilder<MetadataReference>();
        var skipped = new List<string>();

        // The trusted-platform-assemblies list is the set of framework DLLs this
        // process was started with, which is exactly the surface a solution can use.
        var platformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(platformAssemblies))
            throw new InvalidOperationException(
                "TRUSTED_PLATFORM_ASSEMBLIES is unavailable, so no framework references " +
                "can be resolved and nothing could be compiled.");

        foreach (var path in platformAssemblies.Split(Path.PathSeparator))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                items.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex)
            {
                // Not every TPA entry is a readable managed assembly. Recorded rather
                // than swallowed, so a later compile failure can explain itself.
                skipped.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // Instrumented code calls into TraceState, so its assembly must be referenced.
        items.Add(MetadataReference.CreateFromFile(typeof(TraceState).Assembly.Location));

        return new ReferenceSet(items.ToImmutable(), skipped);
    }

    public static CSharpCompilation CreateCompilation(SyntaxTree tree, string assemblyName)
        => CreateCompilation(new[] { tree }, assemblyName);

    public static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees, string assemblyName)
        => CSharpCompilation.Create(
            assemblyName,
            trees,
            References.Value.Items,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true));

    public static CompilationOutcome Compile(SyntaxTree tree, string assemblyName)
        => Compile(new[] { tree }, assemblyName);

    public static CompilationOutcome Compile(IEnumerable<SyntaxTree> trees, string assemblyName)
    {
        var references = References.Value;
        var compilation = CreateCompilation(trees, assemblyName);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (result.Success) return new CompilationOutcome(stream.ToArray(), Array.Empty<string>());

        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        if (references.Skipped.Count > 0)
            errors.Add($"({references.Skipped.Count} framework reference(s) could not be loaded: " +
                       string.Join("; ", references.Skipped.Take(3)) + ")");

        return new CompilationOutcome(null, errors);
    }
}
