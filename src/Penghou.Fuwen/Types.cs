using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Primitive value kinds supported by Fuwen IR v1.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FuwenPrimitiveKind
{
    /// <summary>A Unicode text value.</summary>
    String,
    /// <summary>A true or false value.</summary>
    Boolean,
    /// <summary>A signed integral number.</summary>
    Integer,
    /// <summary>A finite decimal or floating-point number.</summary>
    Number,
    /// <summary>An ISO 8601 duration.</summary>
    Duration,
    /// <summary>A host-bounded JSON value.</summary>
    Json,
}

/// <summary>Base contract for portable Fuwen value types.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PrimitiveType), "primitive")]
[JsonDerivedType(typeof(NamedTypeReference), "named")]
[JsonDerivedType(typeof(OptionalType), "optional")]
[JsonDerivedType(typeof(ListType), "list")]
[JsonDerivedType(typeof(ArtifactType), "artifact")]
public abstract record FuwenType;

/// <summary>A built-in scalar type.</summary>
public sealed record PrimitiveType(FuwenPrimitiveKind Primitive) : FuwenType;

/// <summary>A nominal reference to a resolved schema descriptor.</summary>
public sealed record NamedTypeReference(DescriptorReference Schema) : FuwenType;

/// <summary>An explicitly nullable value type.</summary>
public sealed record OptionalType(FuwenType ValueType) : FuwenType;

/// <summary>A bounded immutable list.</summary>
public sealed record ListType(FuwenType ItemType, int MaxItems) : FuwenType;

/// <summary>A durable reference whose nominal kind is defined by a trusted descriptor.</summary>
public sealed record ArtifactType(DescriptorReference ArtifactDescriptor) : FuwenType;

/// <summary>The kinds of trusted catalogue descriptors referenced by resolved IR.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DescriptorKind
{
    /// <summary>A nominal data schema.</summary>
    Schema,
    /// <summary>A nominal artifact type.</summary>
    Artifact,
    /// <summary>A trusted workflow activity.</summary>
    Activity,
    /// <summary>A durable context provider.</summary>
    ContextProvider,
    /// <summary>A logical inference requirements profile.</summary>
    InferenceProfile,
    /// <summary>A versioned typed prompt template.</summary>
    PromptTemplate,
    /// <summary>A model-visible tool.</summary>
    Tool,
}

/// <summary>A versioned digest with an explicit interpretation contract.</summary>
public sealed record ContentDigest(string Algorithm, string Contract, string Value);

/// <summary>An immutable reference to a trusted catalogue descriptor.</summary>
public sealed record DescriptorReference(
    DescriptorKind Kind,
    string Name,
    string Version,
    ContentDigest ContentDigest);

/// <summary>A declared capability and optional host-defined scope class.</summary>
public sealed record CapabilityRequirement(string Name, string? ScopeClass = null);

/// <summary>The compiler-inferred capabilities required by a resolved plan.</summary>
public sealed record CapabilityManifest(IReadOnlyList<CapabilityRequirement> Requirements);
