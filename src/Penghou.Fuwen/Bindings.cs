using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>An immutable value expression used to bind node inputs and returns.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(InputBinding), "input")]
[JsonDerivedType(typeof(NodeOutputBinding), "node-output")]
[JsonDerivedType(typeof(LiteralBinding), "literal")]
[JsonDerivedType(typeof(ListBinding), "list")]
[JsonDerivedType(typeof(ObjectBinding), "object")]
public abstract record Binding;

/// <summary>A workflow input with an optional field projection.</summary>
public sealed record InputBinding(IReadOnlyList<string> Projection) : Binding;

/// <summary>A prior node output with an optional field projection.</summary>
public sealed record NodeOutputBinding(string NodePath, IReadOnlyList<string> Projection) : Binding;

/// <summary>A JSON scalar or bounded structured literal.</summary>
public sealed record LiteralBinding(JsonElement Value) : Binding;

/// <summary>A bounded structural list literal.</summary>
public sealed record ListBinding(IReadOnlyList<Binding> Items) : Binding;

/// <summary>A structural record literal.</summary>
public sealed record ObjectBinding(IReadOnlyDictionary<string, Binding> Properties) : Binding;

/// <summary>A named argument binding.</summary>
public sealed record ArgumentBinding(string Name, Binding Value);

/// <summary>Operators supported by deterministic v1 conditions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConditionOperator
{
    /// <summary>Tests value equality.</summary>
    Equal,
    /// <summary>Tests value inequality.</summary>
    NotEqual,
    /// <summary>Tests ordered less-than comparison.</summary>
    LessThan,
    /// <summary>Tests ordered less-than-or-equal comparison.</summary>
    LessThanOrEqual,
    /// <summary>Tests ordered greater-than comparison.</summary>
    GreaterThan,
    /// <summary>Tests ordered greater-than-or-equal comparison.</summary>
    GreaterThanOrEqual,
    /// <summary>Requires both Boolean operands.</summary>
    And,
    /// <summary>Requires either Boolean operand.</summary>
    Or,
    /// <summary>Negates one Boolean operand.</summary>
    Not,
    /// <summary>Tests whether an optional projection has a value.</summary>
    Exists,
}

/// <summary>A restricted deterministic condition expression.</summary>
public sealed record ConditionExpression(
    ConditionOperator Operator,
    Binding Left,
    Binding? Right = null);
