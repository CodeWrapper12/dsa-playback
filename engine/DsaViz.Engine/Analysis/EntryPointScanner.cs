using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Lists the methods in a file that could be measured.
///
/// Reads the syntax rather than compiling and reflecting: the editor asks for this
/// every time a file is opened, and a file being edited is frequently not in a
/// compilable state. Parameter types are compared as written, which is why the map
/// below keys on spelling.
/// </summary>
public static class EntryPointScanner
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "int[]", "List<int>", "string", "int[][]", "int", "bool"
    };

    public static IReadOnlyList<EntryPoint> Scan(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var entries = new List<EntryPoint>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            bool isPublicStatic =
                method.Modifiers.Any(m => m.ValueText == "public") &&
                method.Modifiers.Any(m => m.ValueText == "static");

            if (!isPublicStatic) continue;

            var declaringType = method.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault()?.Identifier.Text ?? "";

            var parameters = method.ParameterList.Parameters
                .Select(p =>
                {
                    string type = p.Type?.ToString() ?? "";
                    return new EntryParameter(p.Identifier.Text, type, SupportedTypes.Contains(type));
                })
                .ToList();

            entries.Add(new EntryPoint(declaringType, method.Identifier.Text, parameters));
        }

        // Measurable ones first: the editor shows this as a pick list, and burying a
        // usable method under ones that cannot run would make the list actively unhelpful.
        return entries
            .OrderByDescending(e => e.Measurable)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }
}
