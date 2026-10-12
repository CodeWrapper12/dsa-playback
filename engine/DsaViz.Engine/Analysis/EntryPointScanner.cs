using DsaViz.Engine.Contracts;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DsaViz.Engine.Analysis;

/// <summary>
/// Lists the methods in a file that could be measured.
///
/// Reads the syntax rather than compiling: the editor asks every time a file is opened,
/// and a file being edited is often not in a compilable state. Parameter types are
/// matched as written. The list of spellings is the syntactic mirror of
/// ParameterBinder.ShapeFor, and a test keeps the two in agreement.
/// </summary>
public static class EntryPointScanner
{
    private static readonly HashSet<string> SupportedSpellings = new(StringComparer.Ordinal)
    {
        "int[]", "char[]", "string[]", "int[][]", "char[][]", "string", "int", "long", "bool",
        "List<int>", "IList<int>", "ICollection<int>", "IEnumerable<int>", "IReadOnlyList<int>", "IReadOnlyCollection<int>",
        "IList<IList<int>>", "List<IList<int>>", "List<List<int>>", "IEnumerable<IList<int>>", "IReadOnlyList<IList<int>>"
    };

    public static IReadOnlyList<EntryPoint> Scan(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var nodeTypes = classes.Where(IsNodeClass).Select(c => c.Identifier.Text).ToHashSet();

        var entries = new List<EntryPoint>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.ValueText == "public")) continue;
            if (method.Modifiers.Any(m => m.ValueText is "abstract" or "extern")) continue;
            if (method.TypeParameterList is not null) continue;
            if (method.Parent is not ClassDeclarationSyntax owner) continue;

            // A program's entry point is where examples are called from, not a solution.
            if (method.Identifier.Text == "Main" && method.Modifiers.Any(m => m.ValueText == "static")) continue;

            // A node class's own members are plumbing, not solutions.
            if (nodeTypes.Contains(owner.Identifier.Text)) continue;

            bool isStatic = method.Modifiers.Any(m => m.ValueText == "static");

            var parameters = method.ParameterList.Parameters
                .Select(p =>
                {
                    string type = Normalise(p.Type?.ToString() ?? "");
                    bool byRef = p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in" or "params");
                    return new EntryParameter(p.Identifier.Text, type,
                        !byRef && (SupportedSpellings.Contains(type) || nodeTypes.Contains(type)));
                })
                .ToList();

            string? reason = null;
            if (!isStatic && !IsConstructible(owner))
                reason = $"{owner.Identifier.Text} has no public parameterless constructor, so it cannot be created to call this method.";
            else if (parameters.FirstOrDefault(p => !p.Supported) is { } unsupported)
                reason = $"Parameter '{unsupported.Name}' has type '{unsupported.Type}', which the engine cannot generate.";

            int line = method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            entries.Add(new EntryPoint(owner.Identifier.Text, method.Identifier.Text, parameters) { Reason = reason, Line = line });
        }

        // Measurable ones first: the editor shows this as a pick list, and burying a usable
        // method under ones that cannot run would make it actively unhelpful.
        return entries
            .OrderByDescending(e => e.Measurable)
            .ThenBy(e => e.DeclaringType, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Normalise(string type)
        => type.Replace(" ", "")
               .Replace("System.Collections.Generic.", "")
               .Replace("System.", "")
               .Replace("String", "string")
               .Replace("Int32", "int")
               .Replace("Int64", "long")
               .Replace("Boolean", "bool");

    /// <summary>No constructors at all, or a public one taking no arguments.</summary>
    private static bool IsConstructible(ClassDeclarationSyntax type)
    {
        if (type.Modifiers.Any(m => m.ValueText is "abstract" or "static")) return false;
        var constructors = type.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(m => m.ValueText == "static"))
            .ToList();
        return constructors.Count == 0 || constructors.Any(c =>
            c.Modifiers.Any(m => m.ValueText == "public") && c.ParameterList.Parameters.Count == 0);
    }

    /// <summary>LeetCode's TreeNode and ListNode shapes, recognised syntactically.</summary>
    private static bool IsNodeClass(ClassDeclarationSyntax type)
    {
        string self = type.Identifier.Text;
        var members = type.Members.SelectMany(member => member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => (Name: v.Identifier.Text.ToLowerInvariant(), Type: field.Declaration.Type.ToString())),
            PropertyDeclarationSyntax property => new[] { (Name: property.Identifier.Text.ToLowerInvariant(), Type: property.Type.ToString()) },
            _ => Array.Empty<(string Name, string Type)>()
        }).ToList();

        bool hasValue = members.Any(m => m.Name is "val" or "value" && m.Type == "int");
        bool hasChildren = members.Any(m => m.Name == "left" && m.Type.TrimEnd('?') == self)
                           && members.Any(m => m.Name == "right" && m.Type.TrimEnd('?') == self);
        bool hasNext = members.Any(m => m.Name == "next" && m.Type.TrimEnd('?') == self);
        return hasValue && (hasChildren || hasNext);
    }
}
