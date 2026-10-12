using System.Reflection;

namespace DsaViz.Engine.Inputs;

/// <summary>A field or property, read and written the same way.</summary>
public sealed class NodeMember
{
    private readonly FieldInfo? _field;
    private readonly PropertyInfo? _property;

    private NodeMember(FieldInfo? field, PropertyInfo? property)
    {
        _field = field;
        _property = property;
        Type = field?.FieldType ?? property!.PropertyType;
    }

    public Type Type { get; }

    public object? Get(object target) => _field is not null ? _field.GetValue(target) : _property!.GetValue(target);

    public void Set(object target, object? value)
    {
        if (_field is not null) _field.SetValue(target, value);
        else _property!.SetValue(target, value);
    }

    public static NodeMember? Find(Type type, Type memberType, params string[] names)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        foreach (var name in names)
        {
            if (type.GetField(name, flags) is { } field && field.FieldType == memberType)
                return new NodeMember(field, null);
            if (type.GetProperty(name, flags) is { CanRead: true, CanWrite: true } property && property.PropertyType == memberType)
                return new NodeMember(null, property);
        }
        return null;
    }
}

public sealed record TreeShape(Type Type, NodeMember Value, NodeMember Left, NodeMember Right);
public sealed record ListShape(Type Type, NodeMember Value, NodeMember Next);

/// <summary>
/// Recognises LeetCode's node classes by structure rather than by name alone: a
/// TreeNode is a class with an int value and two children of its own type, a ListNode is
/// one with an int value and a next. Solutions declare these classes themselves, so the
/// engine has to build them through reflection against whatever the file defines.
/// </summary>
public static class NodeShapes
{
    private static readonly string[] ValueNames = { "val", "value" };

    /// <summary>
    /// A node holding <paramref name="value"/>, built without running a constructor:
    /// LeetCode's TreeNode takes optional parameters, so it has no parameterless one to call.
    /// </summary>
    public static object NewNode(Type type, NodeMember valueMember, int value)
    {
        var node = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        valueMember.Set(node, value);
        return node;
    }

    public static TreeShape? AsTree(Type type)
    {
        if (!type.IsClass || type.IsAbstract) return null;
        var value = NodeMember.Find(type, typeof(int), ValueNames);
        var left = NodeMember.Find(type, type, "left");
        var right = NodeMember.Find(type, type, "right");
        return value is not null && left is not null && right is not null ? new TreeShape(type, value, left, right) : null;
    }

    public static ListShape? AsList(Type type)
    {
        if (!type.IsClass || type.IsAbstract) return null;
        var value = NodeMember.Find(type, typeof(int), ValueNames);
        var next = NodeMember.Find(type, type, "next");
        return value is not null && next is not null ? new ListShape(type, value, next) : null;
    }
}
