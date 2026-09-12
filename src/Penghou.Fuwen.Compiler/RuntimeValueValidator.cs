using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>The bounded result of validating one runtime value against a Fuwen type.</summary>
public sealed class RuntimeValueValidationResult
{
    internal RuntimeValueValidationResult(DiagnosticCollection diagnostics) => Diagnostics = diagnostics;

    /// <summary>The stable diagnostics produced by validation.</summary>
    public DiagnosticCollection Diagnostics { get; }

    /// <summary>Whether the value is safe and exactly compatible with the expected type.</summary>
    public bool Succeeded => !Diagnostics.HasErrors;
}

/// <summary>
/// Strict, provider-neutral validation of runtime values at typed boundaries.
/// This validator never coerces JSON values, dereferences artifacts, or
/// performs authorization; those decisions belong to the host verifier.
/// </summary>
public static class RuntimeValueValidator
{
    private const int MaximumJsonDepth = JsonRuntimeValue.MaximumJsonDepth;
    private const int MaximumJsonNodes = JsonRuntimeValue.MaximumJsonNodes;
    private const int MaximumSchemaDefinitions = 4_096;
    private const int MaximumSchemaFields = 10_000;
    private const int MaximumDiagnosticPathLength = 1_024;

    /// <summary>Validates a runtime value against an expected type and schema set.</summary>
    public static RuntimeValueValidationResult Validate(
        RuntimeValue? value,
        FuwenType expectedType,
        IReadOnlyList<ResolvedSchemaDefinition> schemas,
        string path = "value",
        int maximumDiagnostics = CompilationBudget.DefaultMaximumDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(schemas);
        if (schemas.Count > MaximumSchemaDefinitions)
            throw new ArgumentOutOfRangeException(nameof(schemas));
        var builder = new DiagnosticBuilder(maximumDiagnostics);
        var state = new ValidationState(schemas, builder);
        state.Validate(value, expectedType, TextPath(path, "value"), 0);
        return new RuntimeValueValidationResult(builder.ToImmutable());
    }

    private static string TextPath(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.Length > MaximumDiagnosticPathLength || value.Any(char.IsControl))
            throw new ArgumentException("The diagnostic path is invalid or too long.", nameof(value));
        return value;
    }

    private sealed class ValidationState
    {
        private readonly IReadOnlyList<ResolvedSchemaDefinition> schemas;
        private readonly DiagnosticBuilder diagnostics;
        private int jsonNodes;

        internal ValidationState(IReadOnlyList<ResolvedSchemaDefinition> schemas, DiagnosticBuilder diagnostics)
        {
            this.schemas = schemas;
            this.diagnostics = diagnostics;
        }

        internal void Validate(RuntimeValue? value, FuwenType expected, string path, int depth)
        {
            if (depth > MaximumJsonDepth)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueDepthExceeded, path, $"Runtime value depth exceeds {MaximumJsonDepth}.");
                return;
            }

            if (value is null)
            {
                if (expected is OptionalType)
                    return;
                Add(CompilerDiagnosticCodes.RuntimeValueMissing, path, "A runtime value is required.");
                return;
            }

            if (value is JsonRuntimeValue json)
            {
                ValidateJson(json.BorrowedValue, expected, path, depth, inspect: true);
                return;
            }

            if (value is ListRuntimeValue list)
            {
                if (expected is OptionalType optional)
                    Validate(value, optional.ValueType, path, depth + 1);
                else if (expected is ListType listType)
                    ValidateList(list, listType, path, depth);
                else
                    Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a detached JSON runtime value of the requested type.");
                return;
            }

            if (value is ObjectRuntimeValue @object)
            {
                if (expected is OptionalType optional)
                    Validate(value, optional.ValueType, path, depth + 1);
                else if (expected is NamedTypeReference named)
                    ValidateNamed(@object, named.Schema, path, depth);
                else
                    Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a detached JSON runtime value of the requested type.");
                return;
            }

            switch (expected)
            {
                case OptionalType optional:
                    Validate(value, optional.ValueType, path, depth + 1);
                    break;
                case ArtifactType artifact:
                    ValidateArtifact(value, artifact, path);
                    break;
                default:
                    Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a detached JSON runtime value.");
                    break;
            }
        }

        private void ValidateJson(JsonElement element, FuwenType expected, string path, int depth, bool inspect)
        {
            if (inspect && !InspectJson(element, path, depth)) return;
            switch (expected)
            {
                case OptionalType optional:
                    if (element.ValueKind == JsonValueKind.Null) return;
                    ValidateJson(element, optional.ValueType, path, depth + 1, inspect: false);
                    return;
                case PrimitiveType primitive:
                    ValidatePrimitive(element, primitive.Primitive, path);
                    return;
                case ListType list:
                    ValidateList(element, list, path, depth);
                    return;
                case NamedTypeReference named:
                    ValidateNamed(element, named.Schema, path, depth);
                    return;
                case ArtifactType:
                    Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected an artifact-reference runtime value.");
                    return;
                default:
                    Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The expected Fuwen type is unsupported.");
                    return;
            }
        }

        private void ValidatePrimitive(JsonElement element, FuwenPrimitiveKind primitive, string path)
        {
            if (primitive == FuwenPrimitiveKind.Json) return;

            var valid = primitive switch
            {
                FuwenPrimitiveKind.String => element.ValueKind == JsonValueKind.String,
                FuwenPrimitiveKind.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                FuwenPrimitiveKind.Integer => IsStrictInteger(element),
                FuwenPrimitiveKind.Number => element.ValueKind == JsonValueKind.Number,
                FuwenPrimitiveKind.Duration => IsDuration(element),
                _ => false,
            };
            if (!valid)
                Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, $"JSON value is not an exact '{primitive}' value.");
        }

        private void ValidateList(JsonElement element, ListType list, string path, int depth)
        {
            if (list.MaxItems <= 0)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "List type must have a positive maximum item count.");
                return;
            }
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a JSON array.");
                return;
            }
            if (element.GetArrayLength() > list.MaxItems)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueListLimitExceeded, path, $"List contains more than its maximum of {list.MaxItems} items.");
                return;
            }
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateJson(item, list.ItemType, IndexPath(path, index), depth + 1, inspect: false);
                index++;
            }
        }

        private void ValidateList(ListRuntimeValue value, ListType list, string path, int depth)
        {
            if (list.MaxItems <= 0)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "List type must have a positive maximum item count.");
                return;
            }
            if (value.Items.Count > list.MaxItems)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueListLimitExceeded, path, $"List contains more than its maximum of {list.MaxItems} items.");
                return;
            }
            for (var index = 0; index < value.Items.Count; index++)
                Validate(value.Items[index], list.ItemType, IndexPath(path, index), depth + 1);
        }

        private void ValidateNamed(JsonElement element, DescriptorReference schema, string path, int depth)
        {
            if (schema is null || schema.Kind != DescriptorKind.Schema)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "Named runtime values require a schema descriptor.");
                return;
            }
            var matches = schemas.Where(item => item is not null && DescriptorEqual(item.Descriptor, schema)).ToArray();
            if (matches.Length == 0)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueSchemaNotFound, path, "The exact named schema descriptor was not supplied.");
                return;
            }
            if (matches.Length > 1)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The schema set contains duplicate exact descriptors.");
                return;
            }
            switch (matches[0])
            {
                case ObjectSchemaDefinition @object:
                    ValidateObject(element, @object, path, depth);
                    break;
                case EnumSchemaDefinition @enum:
                    ValidateEnum(element, @enum, path);
                    break;
                default:
                    Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The named schema definition is unsupported.");
                    break;
            }
        }

        private void ValidateNamed(ObjectRuntimeValue value, DescriptorReference schema, string path, int depth)
        {
            if (schema is null || schema.Kind != DescriptorKind.Schema)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "Named runtime values require a schema descriptor.");
                return;
            }
            var matches = schemas.Where(item => item is not null && DescriptorEqual(item.Descriptor, schema)).ToArray();
            if (matches.Length == 0)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueSchemaNotFound, path, "The exact named schema descriptor was not supplied.");
                return;
            }
            if (matches.Length > 1)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The schema set contains duplicate exact descriptors.");
                return;
            }
            switch (matches[0])
            {
                case ObjectSchemaDefinition @object:
                    ValidateObject(value, @object, path, depth);
                    break;
                case EnumSchemaDefinition:
                    Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a detached JSON string enum value.");
                    break;
                default:
                    Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The named schema definition is unsupported.");
                    break;
            }
        }

        private void ValidateObject(JsonElement element, ObjectSchemaDefinition schema, string path, int depth)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected a JSON object.");
                return;
            }
            if (schema.Fields is null)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema has no field collection.");
                return;
            }
            if (schema.Fields.Count > MaximumSchemaFields ||
                schema.Fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.Name) || field.Type is null))
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema contains invalid or excessive fields.");
                return;
            }
            Dictionary<string, SchemaField> fields;
            try
            {
                fields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema contains duplicate field names.");
                return;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (!fields.TryGetValue(property.Name, out var field))
                {
                    Add(CompilerDiagnosticCodes.RuntimeValueUnknownField, PropertyPath(path, property.Name), "Object contains an unknown field.");
                    continue;
                }
                ValidateJson(property.Value, field.Type, PropertyPath(path, property.Name), depth + 1, inspect: false);
            }
            foreach (var field in schema.Fields)
            {
                if (!element.TryGetProperty(field.Name, out _) && field.Type is not OptionalType)
                    Add(CompilerDiagnosticCodes.RuntimeValueRequiredFieldMissing, PropertyPath(path, field.Name), "Required object field is missing.");
            }
        }

        private void ValidateObject(ObjectRuntimeValue value, ObjectSchemaDefinition schema, string path, int depth)
        {
            if (schema.Fields is null)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema has no field collection.");
                return;
            }
            if (schema.Fields.Count > MaximumSchemaFields ||
                schema.Fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.Name) || field.Type is null))
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema contains invalid or excessive fields.");
                return;
            }
            Dictionary<string, SchemaField> fields;
            try
            {
                fields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The object schema contains duplicate field names.");
                return;
            }
            foreach (var property in value.Properties)
            {
                if (!fields.TryGetValue(property.Key, out var field))
                {
                    Add(CompilerDiagnosticCodes.RuntimeValueUnknownField, PropertyPath(path, property.Key), "Object contains an unknown field.");
                    continue;
                }
                Validate(property.Value, field.Type, PropertyPath(path, property.Key), depth + 1);
            }
            foreach (var field in schema.Fields)
            {
                if (!value.Properties.ContainsKey(field.Name) && field.Type is not OptionalType)
                    Add(CompilerDiagnosticCodes.RuntimeValueRequiredFieldMissing, PropertyPath(path, field.Name), "Required object field is missing.");
            }
        }

        private void ValidateEnum(JsonElement element, EnumSchemaDefinition schema, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Enum values must be JSON strings.");
                return;
            }
            if (schema.Members is null || schema.Members.Count == 0 ||
                schema.Members.Count > MaximumSchemaFields ||
                schema.Members.Any(member => member is null || member.Value is null))
            {
                Add(CompilerDiagnosticCodes.RuntimeValueMalformed, path, "The enum schema contains invalid or excessive members.");
                return;
            }
            var literal = element.GetString();
            if (!schema.Members.Any(member => string.Equals(member.Value, literal, StringComparison.Ordinal)))
                Add(CompilerDiagnosticCodes.RuntimeValueEnumLiteralInvalid, path, "Enum literal is not declared by the exact schema.");
        }

        private void ValidateArtifact(RuntimeValue value, ArtifactType expected, string path)
        {
            if (value is not ArtifactRuntimeValue artifact)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueKindMismatch, path, "Expected an artifact-reference runtime value.");
                return;
            }
            if (!DescriptorEqual(artifact.Artifact.ArtifactDescriptor, expected.ArtifactDescriptor))
                Add(CompilerDiagnosticCodes.RuntimeValueArtifactDescriptorMismatch, path, "Artifact descriptor does not exactly match the expected descriptor.");
        }

        private bool InspectJson(JsonElement element, string path, int depth)
        {
            if (depth > MaximumJsonDepth)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueDepthExceeded, path, $"Runtime JSON depth exceeds {MaximumJsonDepth}.");
                return false;
            }
            if (++jsonNodes > MaximumJsonNodes)
            {
                Add(CompilerDiagnosticCodes.RuntimeValueNodeLimitExceeded, path, $"Runtime JSON nodes exceed {MaximumJsonNodes}.");
                return false;
            }
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!properties.Add(property.Name))
                        {
                            Add(CompilerDiagnosticCodes.RuntimeValueCanonicalJsonInvalid, path, "JSON object contains a duplicate property.");
                            return false;
                        }
                        if (!InspectJson(property.Value, PropertyPath(path, property.Name), depth + 1)) return false;
                    }
                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                        if (!InspectJson(item, IndexPath(path, index++), depth + 1)) return false;
                    break;
                case JsonValueKind.Number:
                    try
                    {
                        _ = CanonicalJson.Canonicalize(element);
                    }
                    catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
                    {
                        Add(CompilerDiagnosticCodes.RuntimeValueCanonicalJsonInvalid, path, "JSON number is not representable by canonical JSON v1.");
                        return false;
                    }
                    break;
            }
            return true;
        }

        private static bool IsStrictInteger(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Number) return false;
            var raw = element.GetRawText();
            if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E')) return false;
            return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        }

        private static bool IsDuration(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            try { _ = XmlConvert.ToTimeSpan(element.GetString()!); return true; }
            catch (FormatException) { return false; }
        }

        private void Add(string code, string path, string message) => diagnostics.Add(new CompilerDiagnostic(
            code,
            DiagnosticSeverity.Error,
            DiagnosticPhase.Typing,
            message,
            path));

        private static string IndexPath(string path, int index) =>
            AppendPath(path, $"[{index.ToString(CultureInfo.InvariantCulture)}]");

        private static string PropertyPath(string path, string propertyName) =>
            AppendPath(path, ".", propertyName);

        private static string AppendPath(string path, string suffix) => AppendPath(path, string.Empty, suffix);

        private static string AppendPath(string path, string separator, string segment)
        {
            if (path.Length >= MaximumDiagnosticPathLength - 3)
                return path[..(MaximumDiagnosticPathLength - 3)] + "...";

            var builder = new StringBuilder(MaximumDiagnosticPathLength);
            builder.Append(path);
            builder.Append(separator);
            var remaining = MaximumDiagnosticPathLength - builder.Length;
            var truncated = segment.Length > remaining;
            var take = truncated ? Math.Max(0, remaining - 3) : segment.Length;
            for (var index = 0; index < take; index++)
                builder.Append(char.IsControl(segment[index]) ? '?' : segment[index]);
            if (truncated) builder.Append("...");
            return builder.ToString();
        }

        private static bool DescriptorEqual(DescriptorReference left, DescriptorReference right) =>
            left is not null && right is not null &&
            left.Kind == right.Kind &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
            DigestEqual(left.ContentDigest, right.ContentDigest);

        private static bool DigestEqual(ContentDigest left, ContentDigest right) =>
            left is not null && right is not null &&
            string.Equals(left.Algorithm, right.Algorithm, StringComparison.Ordinal) &&
            string.Equals(left.Contract, right.Contract, StringComparison.Ordinal) &&
            string.Equals(left.Value, right.Value, StringComparison.Ordinal);
    }
}
