using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Portable deterministic JSON used by Fuwen identity contracts.</summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>Serializes a CLR value and canonicalizes the resulting JSON tree.</summary>
    public static byte[] Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
        return Canonicalize(element);
    }

    /// <summary>Reads JSON into a portable contract using the same discriminator options.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var value = JsonSerializer.Deserialize<T>(utf8Json, SerializerOptions);
        return value ?? throw new JsonException($"JSON did not contain a '{typeof(T).Name}' value.");
    }

    /// <summary>Produces deterministic UTF-8 JSON under penghou-canonical-json/v1.</summary>
    public static byte[] Canonicalize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            Write(writer, element);
        return buffer.WrittenSpan.ToArray();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement element)
    {
        var properties = element.EnumerateObject().ToArray();
        var duplicate = properties
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new JsonException($"Duplicate JSON property '{duplicate.Key}' cannot be canonicalized.");

        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            Write(writer, property.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(decimalValue == 0
                ? "0"
                : decimalValue.ToString("G29", CultureInfo.InvariantCulture));
            return;
        }

        var value = element.GetDouble();
        if (!double.IsFinite(value))
            throw new JsonException("Non-finite JSON numbers are unsupported.");
        if (value == 0)
        {
            writer.WriteRawValue("0");
            return;
        }

        var formatted = value.ToString("R", CultureInfo.InvariantCulture)
            .Replace("E+", "e", StringComparison.Ordinal)
            .Replace("E", "e", StringComparison.Ordinal);
        var exponent = formatted.IndexOf('e');
        if (exponent >= 0)
        {
            var prefix = formatted[..(exponent + 1)];
            var suffix = formatted[(exponent + 1)..];
            var negative = suffix.StartsWith("-", StringComparison.Ordinal);
            suffix = suffix.TrimStart('+', '-').TrimStart('0');
            formatted = prefix + (negative ? "-" : string.Empty) + (suffix.Length == 0 ? "0" : suffix);
        }
        writer.WriteRawValue(formatted);
    }
}
