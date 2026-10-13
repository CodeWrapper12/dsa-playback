using System.Reflection;
using System.Runtime.ExceptionServices;

namespace DsaViz.Runtime;

/// <summary>A design problem's calls that do not fit the class: an unknown method, or the wrong number of arguments.</summary>
public sealed class DesignInputException : Exception
{
    public DesignInputException(string message) : base(message) { }
}

/// <summary>
/// Runs a LeetCode design problem — LRU Cache, Min Stack — the way LeetCode does: the
/// first operation creates the object, every later one calls a method on it, and the
/// results are collected in order, null for the constructor and for void methods.
///
/// Lives in the runtime so the engine can use it as the entry point of a compiled
/// solution: the student's class is instrumented as usual, and this is not.
/// </summary>
public static class DesignCalls
{
    /// <summary>
    /// The constructor or method each operation names, matched by name ignoring case —
    /// LeetCode writes "put" for C#'s Put — and by the number of arguments.
    /// </summary>
    public static MethodBase[] Resolve(Type type, IReadOnlyList<string> operations, IReadOnlyList<int> arities)
    {
        if (operations.Count == 0)
            throw new DesignInputException("The list of operations is empty. The first one creates the object, like \"" + type.Name + "\".");
        if (operations.Count != arities.Count)
            throw new DesignInputException(
                $"There are {operations.Count} operations but {arities.Count} argument lists. Each operation needs its own list, [] when it takes none.");
        if (!string.Equals(operations[0], type.Name, StringComparison.Ordinal))
            throw new DesignInputException($"The first operation creates the object, so it must be \"{type.Name}\", not \"{operations[0]}\".");

        var members = new MethodBase[operations.Count];
        members[0] = type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == arities[0])
            ?? throw new DesignInputException($"{type.Name} has no public constructor taking {Count(arities[0])}.");

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToList();
        for (int i = 1; i < operations.Count; i++)
        {
            string name = operations[i];
            var named = methods.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                throw new DesignInputException($"Operation {i + 1} is \"{name}\", but {type.Name} has no public method by that name.");
            members[i] = named.FirstOrDefault(m => m.GetParameters().Length == arities[i])
                ?? throw new DesignInputException(
                    $"Operation {i + 1} calls {named[0].Name} with {Count(arities[i])}, but it takes {Count(named[0].GetParameters().Length)}.");
        }
        return members;
    }

    /// <summary>Makes every call in order and returns each result. Entry point of a design run.</summary>
    /// <param name="starting">Told the number of each call just before it is made, so a recording can mark where it began.</param>
    public static object?[] Run(Type type, string[] operations, object?[][] arguments, Action<int>? starting)
    {
        var members = Resolve(type, operations, arguments.Select(a => a.Length).ToList());
        var results = new object?[operations.Length];
        object? target = null;

        for (int i = 0; i < members.Length; i++)
        {
            starting?.Invoke(i);
            try
            {
                if (members[i] is ConstructorInfo constructor) target = constructor.Invoke(arguments[i]);
                else results[i] = members[i].Invoke(target, arguments[i]);
            }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } thrown)
            {
                // The student's own exception, not reflection's wrapper around it, so it is
                // reported exactly as when their method is called directly.
                ExceptionDispatchInfo.Capture(thrown).Throw();
            }
        }
        return results;
    }

    private static string Count(int arguments) => arguments == 1 ? "1 argument" : $"{arguments} arguments";
}
