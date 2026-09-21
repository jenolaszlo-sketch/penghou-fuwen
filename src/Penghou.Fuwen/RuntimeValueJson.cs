using System.Buffers;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>
/// Converts runtime values to detached JSON. The runtime may normalize
/// provider outputs (e.g. JSON to nominal composites for named output
/// types), so hosts must use this boundary instead of assuming
/// <see cref="JsonRuntimeValue"/> arguments (see review R06).
/// </summary>
public static class RuntimeValueJson
{
    /// <summary>
    /// Normalizes detached JSON into the runtime representation implied by an
    /// admitted Fuwen type. Named objects, lists, and artifact references are
    /// materialized recursively; primitive and unconstrained JSON values stay
    /// as detached JSON values.
    /// </summary>
    public static RuntimeValue Normalize(
        JsonElement value,
        FuwenType expectedType,
        IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(schemas);
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Runtime JSON cannot be undefined.", nameof(value));

        if (expectedType is OptionalType optional && value.ValueKind != JsonValueKind.Null)
            return Normalize(value, optional.ValueType, schemas);
        if (expectedType is ListType list && value.ValueKind == JsonValueKind.Array)
            return RuntimeValue.FromList(value.EnumerateArray()
                .Select(item => Normalize(item, list.ItemType, schemas))
                .ToArray());
        if (expectedType is NamedTypeReference named && value.ValueKind == JsonValueKind.Object &&
            schemas.FirstOrDefault(schema => schema.Descriptor == named.Schema) is ObjectSchemaDefinition objectSchema)
        {
            var properties = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var field = objectSchema.Fields.FirstOrDefault(candidate => candidate.Name == property.Name);
                properties[property.Name] = field is null
                    ? RuntimeValue.FromJson(property.Value)
                    : Normalize(property.Value, field.Type, schemas);
            }
            return RuntimeValue.FromObject(properties);
        }
        if (expectedType is ArtifactType && value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("$kind", out var kind) && string.Equals(kind.GetString(), "artifact", StringComparison.Ordinal))
        {
            var artifact = new ArtifactReference(
                value.GetProperty("provider").GetString()!,
                value.GetProperty("artifactId").GetString()!,
                CanonicalJson.Deserialize<DescriptorReference>(CanonicalJson.Canonicalize(value.GetProperty("artifactDescriptor"))),
                CanonicalJson.Deserialize<ContentDigest>(CanonicalJson.Canonicalize(value.GetProperty("contentDigest"))),
                value.TryGetProperty("byteLength", out var length) && length.ValueKind != JsonValueKind.Null ? length.GetInt64() : null,
                value.TryGetProperty("logicalName", out var logical) && logical.ValueKind != JsonValueKind.Null ? logical.GetString() : null);
            return RuntimeValue.FromArtifact(artifact);
        }
        return RuntimeValue.FromJson(value);
    }

    /// <summary>
    /// Normalizes any runtime representation through detached JSON and the
    /// admitted Fuwen type. Execution ports can use this boundary without
    /// depending on a concrete <see cref="RuntimeValue"/> subtype.
    /// </summary>
    public static RuntimeValue Normalize(
        RuntimeValue value,
        FuwenType expectedType,
        IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Normalize(ToJsonElement(value), expectedType, schemas);
    }

    /// <summary>Normalizes a runtime value as a bounded typed list.</summary>
    public static ListRuntimeValue NormalizeList(
        RuntimeValue value,
        FuwenType itemType,
        int maximumItems,
        IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(itemType);
        ArgumentNullException.ThrowIfNull(schemas);
        if (maximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));

        return Normalize(value, new ListType(itemType, maximumItems), schemas) as ListRuntimeValue
            ?? throw new ArgumentException("Runtime value is not a list after typed normalization.", nameof(value));
    }

    /// <summary>Serializes any runtime value to a detached JSON element.</summary>
    public static JsonElement ToJsonElement(RuntimeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, RuntimeValue value)
    {
        switch (value)
        {
            case JsonRuntimeValue json:
                json.Value.WriteTo(writer);
                break;
            case ListRuntimeValue list:
                writer.WriteStartArray();
                foreach (var item in list.Items)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case ObjectRuntimeValue @object:
                writer.WriteStartObject();
                foreach (var property in @object.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case ArtifactRuntimeValue artifact:
                writer.WriteStartObject();
                writer.WriteString("$kind", "artifact");
                writer.WriteString("provider", artifact.Artifact.Provider);
                writer.WriteString("artifactId", artifact.Artifact.ArtifactId);
                writer.WritePropertyName("artifactDescriptor");
                using (var descriptor = JsonDocument.Parse(CanonicalJson.Serialize(artifact.Artifact.ArtifactDescriptor)))
                    descriptor.RootElement.WriteTo(writer);
                writer.WritePropertyName("contentDigest");
                using (var digest = JsonDocument.Parse(CanonicalJson.Serialize(artifact.Artifact.ContentDigest)))
                    digest.RootElement.WriteTo(writer);
                if (artifact.Artifact.ByteLength is null)
                    writer.WriteNull("byteLength");
                else
                    writer.WriteNumber("byteLength", artifact.Artifact.ByteLength.Value);
                if (artifact.Artifact.LogicalName is null)
                    writer.WriteNull("logicalName");
                else
                    writer.WriteString("logicalName", artifact.Artifact.LogicalName);
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"Runtime value kind '{value.GetType().Name}' is not serializable.");
        }
    }
}
