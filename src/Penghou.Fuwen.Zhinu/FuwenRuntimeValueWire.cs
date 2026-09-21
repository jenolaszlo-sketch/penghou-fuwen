using System.Text.Json;

namespace Penghou.Fuwen.Zhinu;

/// <summary>
/// Owns durable JSON serialization at the Zhinu boundary while delegating
/// runtime-value normalization to the provider-neutral Fuwen contract.
/// </summary>
internal static class FuwenRuntimeValueWire
{
    internal static JsonElement Serialize<T>(T value)
    {
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(value));
        return document.RootElement.Clone();
    }

    internal static RuntimeValue FromJson(
        JsonElement value,
        FuwenType expectedType,
        IReadOnlyList<ResolvedSchemaDefinition> schemas) =>
        RuntimeValueJson.Normalize(value, expectedType, schemas);

    internal static ListRuntimeValue NormalizeList(
        RuntimeValue value,
        FuwenType itemType,
        int maximumItems,
        IReadOnlyList<ResolvedSchemaDefinition> schemas) =>
        RuntimeValueJson.NormalizeList(value, itemType, maximumItems, schemas);

    internal static JsonElement ToJson(RuntimeValue value) =>
        RuntimeValueJson.ToJsonElement(value);
}
