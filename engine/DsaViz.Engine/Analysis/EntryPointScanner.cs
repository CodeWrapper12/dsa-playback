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
///
/// A parameter the engine cannot generate — a double, a List&lt;string&gt; — still runs
/// when an example in the file gives it a value, as Python's do. Such a method can be
/// visualized but not measured, since measuring needs generated inputs at many sizes.
/// </summary>
public static class EntryPointScanner
{
    private static readonly HashSet<string> SupportedSpellings = new(StringComparer.Ordinal)
    {
        "int[]", "char[]", "string[]", "int[][]", "char[][]", "string", "int", "long", "bool",
        "List<int>", "IList<int>", "ICollection<int>", "IEnumerable<int>", "IReadOnlyList<int>", "IReadOnlyCollection<int>",
        "IList<IList<int>>", "List<IList<int>>", "List<List<int>>", "IEnumerable<IList<int>>", "IReadOnlyList<IList<int>>"
    };

    /// <summary>Values Literals can read from an example, beyond what can be generated.</summary>
    private static readonly HashSet<string> TypeableScalars = new(StringComparer.Ordinal)
    {
        "int", "long", "double", "bool", "char", "string"
    };

    private static readonly HashSet<string> ListSpellings = new(StringComparer.Ordinal)
    {
        "List", "IList", "ICollection", "IEnumerable", "IReadOnlyList", "IReadOnlyCollection"
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

            var declared = method.ParameterList.Parameters
                .Select(p => (
                    Name: p.Identifier.Text,
                    Type: Normalise(p.Type?.ToString() ?? ""),
                    ByRef: p.Modifiers.Any(m => m.ValueText is "ref" or "out" or "in" or "params")))
                .ToList();

            // Only read when a parameter needs it: most solutions take generatable types.
            var generatable = declared.Select(p => !p.ByRef && (SupportedSpellings.Contains(p.Type) || nodeTypes.Contains(p.Type))).ToList();
            var examples = generatable.All(g => g)
                ? null
                : ExampleFinder.Find(root, method.Identifier.Text, declared.Select(p => p.Name).ToList()).Examples;
            bool Given(int index) => examples?.Any(e => e.Values[index] is not null) == true;

            var parameters = declared
                .Select((p, i) => new EntryParameter(p.Name, p.Type,
                    generatable[i] || (!p.ByRef && Typeable(p.Type, nodeTypes) && Given(i))))
                .ToList();

            int unsupported = parameters.FindIndex(p => !p.Supported);
            string? reason = null;
            if (!isStatic && !IsConstructible(owner))
                reason = $"{owner.Identifier.Text} has no public parameterless constructor, so it cannot be created to call this method.";
            else if (unsupported >= 0)
                reason = UnsupportedReason(declared[unsupported].Name, declared[unsupported].Type, declared[unsupported].ByRef, nodeTypes);

            int line = method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            entries.Add(new EntryPoint(owner.Identifier.Text, method.Identifier.Text, parameters)
            {
                Reason = reason,
                Line = line,
                Generatable = generatable.All(g => g)
            });
        }

        // Measurable ones first: the editor shows this as a pick list, and burying a usable
        // method under ones that cannot run would make it actively unhelpful.
        return entries
            .OrderByDescending(e => e.Measurable)
            .ThenBy(e => e.DeclaringType, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string UnsupportedReason(string name, string type, bool byRef, HashSet<string> nodeTypes)
    {
        if (byRef) return $"Parameter '{name}' is passed by reference, which the visualizer cannot do.";
        if (Typeable(type, nodeTypes))
            return $"Parameter '{name}' is '{type}', which cannot be generated. " +
                   "Give it a value in an example comment or a call, and it is used.";
        return $"Parameter '{name}' has type '{type}', which the visualizer can neither generate nor read from an example.";
    }

    /// <summary>
    /// Whether an example's value can be read as this type: the scalars Literals reads,
    /// and arrays and lists of them, nested to any depth. The syntactic mirror of
    /// Literals.ToObject.
    /// </summary>
    private static bool Typeable(string type, HashSet<string> nodeTypes)
    {
        if (TypeableScalars.Contains(type) || nodeTypes.Contains(type)) return true;
        if (type.EndsWith("[]", StringComparison.Ordinal)) return Typeable(type[..^2], nodeTypes);

        int open = type.IndexOf('<');
        if (open <= 0 || !type.EndsWith('>')) return false;
        string element = type[(open + 1)..^1];
        return ListSpellings.Contains(type[..open]) && !element.Contains(',') && Typeable(element, nodeTypes);
    }

    private static string Normalise(string type)
        => type.Replace(" ", "")
               .Replace("System.Collections.Generic.", "")
               .Replace("System.", "")
               .Replace("String", "string")
               .Replace("Int32", "int")
               .Replace("Int64", "long")
               .Replace("Boolean", "bool")
               .Replace("Double", "double");

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
