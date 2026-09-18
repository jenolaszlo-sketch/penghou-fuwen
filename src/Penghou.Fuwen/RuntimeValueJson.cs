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
