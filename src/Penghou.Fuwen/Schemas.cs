using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>A completely resolved nominal schema included in executable IR.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ObjectSchemaDefinition), "object")]
[JsonDerivedType(typeof(EnumSchemaDefinition), "enum")]
public abstract record ResolvedSchemaDefinition(DescriptorReference Descriptor);

/// <summary>A named field whose optionality is expressed by <see cref="OptionalType"/>.</summary>
public sealed record SchemaField(string Name, FuwenType Type);

/// <summary>A constrained nominal object schema.</summary>
public sealed record ObjectSchemaDefinition(
    DescriptorReference Descriptor,
    IReadOnlyList<SchemaField> Fields) : ResolvedSchemaDefinition(Descriptor);

/// <summary>A stable source name and serialized string value for an enum member.</summary>
public sealed record EnumMember(string Name, string Value);

/// <summary>A constrained nominal string enum schema.</summary>
public sealed record EnumSchemaDefinition(
    DescriptorReference Descriptor,
    IReadOnlyList<EnumMember> Members) : ResolvedSchemaDefinition(Descriptor);
