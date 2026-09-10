using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Portable deterministic JSON used by Fuwen identity contracts.</summary>
public static class CanonicalJson
{
    // v1 historically used decimal first and double as a fallback.  Keep the
    // resulting bytes for values that were already accepted, but do not let a
    // fallback silently round a JSON number into a different value.  These
    // bounds keep the exactness check deterministic and bounded for hostile
    // input while remaining far above the precision of either supported CLR
    // numeric representation.
    private const int MaximumNumberLength = 1_000_000;
    private const int MaximumExponentDigits = 128;
    private const int MaximumSignificantDigits = 1_000_000;

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
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            Indented = false,
            SkipValidation = false,
        }))
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
        var raw = element.GetRawText();
        if (raw.Length == 0 || raw.Length > MaximumNumberLength)
            throw new JsonException($"JSON number length must be between 1 and {MaximumNumberLength} characters.");

        if (element.TryGetDecimal(out var decimalValue))
        {
            var formatted = decimalValue == 0
                ? "0"
                : decimalValue.ToString("G29", CultureInfo.InvariantCulture);
            if (NumbersAreEquivalent(raw, formatted))
            {
                writer.WriteRawValue(formatted);
                return;
            }
        }

        // A number outside decimal's exact range is deliberately not sent
        // through JsonElement.GetDouble(): the conversion can round a valid
        // JSON token (or underflow it to zero) and silently change identity.
        if (IsZeroToken(raw))
        {
            writer.WriteRawValue("0");
            return;
        }
        throw new JsonException("JSON number cannot be represented without precision loss.");
    }

    private static bool IsZeroToken(string raw)
    {
        var end = raw.IndexOfAny(['e', 'E']);
        if (end < 0) end = raw.Length;
        foreach (var character in raw.AsSpan(0, end))
        {
            if (character is '-' or '+' or '.') continue;
            if (character != '0') return false;
        }
        return true;
    }

    private static bool NumbersAreEquivalent(string left, string right)
    {
        if (!TryParseExactNumber(left, out var leftNumber) ||
            !TryParseExactNumber(right, out var rightNumber))
            return false;

        return leftNumber.Sign == rightNumber.Sign &&
            leftNumber.Digits == rightNumber.Digits &&
            leftNumber.DecimalExponent == rightNumber.DecimalExponent;
    }

    private static bool TryParseExactNumber(string raw, out ExactNumber number)
    {
        number = default;
        if (raw.Length == 0 || raw.Length > MaximumNumberLength)
            return false;

        var cursor = 0;
        var sign = 1;
        if (raw[cursor] == '-')
        {
            sign = -1;
            cursor++;
        }
        else if (raw[cursor] == '+')
        {
            cursor++;
        }

        var exponentMarker = raw.IndexOfAny(['e', 'E'], cursor);
        if (exponentMarker < 0) exponentMarker = raw.Length;

        var exponent = BigInteger.Zero;
        if (exponentMarker < raw.Length)
        {
            var exponentText = raw[(exponentMarker + 1)..];
            if (exponentText.Length == 0 ||
                exponentText.TrimStart('+', '-').Length > MaximumExponentDigits ||
                !BigInteger.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return false;
        }

        var mantissa = raw[cursor..exponentMarker];
        var point = mantissa.IndexOf('.');
        var fractionalDigits = point < 0 ? 0 : mantissa.Length - point - 1;
        var digitsText = point < 0
            ? mantissa
            : string.Concat(mantissa.AsSpan(0, point), mantissa.AsSpan(point + 1));
        if (digitsText.Length == 0)
            return false;

        var first = 0;
        while (first < digitsText.Length && digitsText[first] == '0') first++;
        if (first == digitsText.Length)
        {
            number = new ExactNumber(0, BigInteger.Zero, BigInteger.Zero);
            return true;
        }

        var digits = digitsText[first..];
        var last = digits.Length;
        while (last > 1 && digits[last - 1] == '0') last--;
        var trailingZeroes = digits.Length - last;
        digits = digits[..last];
        if (digits.Length > MaximumSignificantDigits)
            return false;

        if (!BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient))
            return false;

        var decimalExponent = exponent - fractionalDigits + trailingZeroes;
        while (coefficient % 10 == 0)
        {
            coefficient /= 10;
            decimalExponent++;
        }

        number = new ExactNumber(sign, coefficient, decimalExponent);
        return true;
    }

    private readonly record struct ExactNumber(int Sign, BigInteger Digits, BigInteger DecimalExponent);
}
