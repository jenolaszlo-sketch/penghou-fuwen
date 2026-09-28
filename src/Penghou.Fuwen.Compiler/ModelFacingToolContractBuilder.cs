using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>Builds provider schemas from trusted Fuwen callable types using the runtime validator's type rules.</summary>
public static class ModelFacingToolContractBuilder
{
    /// <summary>Versioned identity of the strict runtime value validator contract.</summary>
    public const string ValidatorIdentity = "fuwen.runtime-value-validator/v1";

    /// <summary>Creates a provider-facing contract whose schemas describe the exact admitted callable signature.</summary>
    public static InferenceModelToolContract Build(
        DescriptorReference tool,
        CallableContract callable,
        IReadOnlyList<ResolvedSchemaDefinition> schemas) =>
        Build(tool, callable?.Signature ?? throw new ArgumentNullException(nameof(callable)), schemas);

    /// <summary>Creates a provider-facing contract from an admitted callable signature.</summary>
    public static InferenceModelToolContract Build(
        DescriptorReference tool,
        CallableSignature signature,
        IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(schemas);
        if (tool.Kind != DescriptorKind.Tool || string.IsNullOrWhiteSpace(tool.ContentDigest?.Value))
            throw new ArgumentException("A model-facing contract requires a versioned trusted tool descriptor.", nameof(tool));
        if (signature.Parameters is null || signature.OutputType is null)
            throw new ArgumentException("A complete trusted callable signature is required.", nameof(signature));
        if (signature.Parameters.Count > 128 || schemas.Count > 4096)
            throw new ArgumentOutOfRangeException(nameof(signature));

        var closure = schemas.ToDictionary(static schema => SchemaKey(schema.Descriptor), StringComparer.Ordinal);
        var parameterProperties = new JsonObject();
        var required = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in signature.Parameters)
        {
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name) || parameter.Name.Length > 128 ||
                parameter.Name.Any(char.IsControl) || !names.Add(parameter.Name))
                throw new ArgumentException("Callable parameter names must be unique bounded text.", nameof(signature));
            parameterProperties.Add(parameter.Name, TypeSchema(parameter.Type, closure, 0));
            required.Add(parameter.Name);
        }

        var parametersRoot = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = parameterProperties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        var resultRoot = TypeSchema(signature.OutputType, closure, 0);
        var parametersJson = AddDefinitions(parametersRoot, closure).ToJsonString();
        var resultJson = AddDefinitions(resultRoot, closure).ToJsonString();
        var material = string.Join("\n", "fuwen.model-tool-contract/v1", tool.Name, tool.Version,
            tool.ContentDigest.Value, parametersJson, resultJson, ValidatorIdentity);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return new InferenceModelToolContract(tool.Name, parametersJson, resultJson,
            tool.ContentDigest.Value, ValidatorIdentity, digest);
    }

    private static JsonNode TypeSchema(FuwenType type, IReadOnlyDictionary<string, ResolvedSchemaDefinition> schemas, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > 32) throw new ArgumentException("Callable schemas exceed the supported nesting depth.", nameof(type));
        return type switch
        {
            PrimitiveType { Primitive: FuwenPrimitiveKind.String } => new JsonObject { ["type"] = "string" },
            PrimitiveType { Primitive: FuwenPrimitiveKind.Boolean } => new JsonObject { ["type"] = "boolean" },
            PrimitiveType { Primitive: FuwenPrimitiveKind.Integer } => new JsonObject { ["type"] = "integer" },
            PrimitiveType { Primitive: FuwenPrimitiveKind.Number } => new JsonObject { ["type"] = "number" },
            PrimitiveType { Primitive: FuwenPrimitiveKind.Duration } => new JsonObject { ["type"] = "string", ["format"] = "duration" },
            PrimitiveType { Primitive: FuwenPrimitiveKind.Json } => new JsonObject(),
            OptionalType optional => new JsonObject { ["anyOf"] = new JsonArray(TypeSchema(optional.ValueType, schemas, depth + 1), new JsonObject { ["type"] = "null" }) },
            ListType list when list.MaxItems > 0 => new JsonObject { ["type"] = "array", ["items"] = TypeSchema(list.ItemType, schemas, depth + 1), ["maxItems"] = list.MaxItems },
            NamedTypeReference named when schemas.ContainsKey(SchemaKey(named.Schema)) => new JsonObject { ["$ref"] = "#/$defs/" + EscapePointer(SchemaKey(named.Schema)) },
            ArtifactType => throw new ArgumentException("Artifact references cannot be model-constructed tool arguments or results.", nameof(type)),
            _ => throw new ArgumentException("The callable type is invalid or its trusted schema is missing.", nameof(type)),
        };
    }

    private static JsonObject AddDefinitions(JsonNode root, IReadOnlyDictionary<string, ResolvedSchemaDefinition> schemas)
    {
        var document = root as JsonObject ?? new JsonObject { ["allOf"] = new JsonArray(root) };
        if (schemas.Count == 0) return document;
        var definitions = new JsonObject();
        foreach (var pair in schemas.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            JsonNode definition = pair.Value switch
            {
                ObjectSchemaDefinition obj => ObjectSchema(obj, schemas),
                EnumSchemaDefinition enm => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(enm.Members.Select(static member => JsonValue.Create(member.Value)).ToArray()) },
                _ => throw new ArgumentException("Unknown trusted schema definition.", nameof(schemas)),
            };
            definitions.Add(pair.Key, definition);
        }
        document["$defs"] = definitions;
        return document;
    }

    private static JsonObject ObjectSchema(ObjectSchemaDefinition schema, IReadOnlyDictionary<string, ResolvedSchemaDefinition> schemas)
    {
        if (schema.Fields is null || schema.Fields.Count > 10_000) throw new ArgumentException("Invalid trusted object schema.", nameof(schema));
        var properties = new JsonObject();
        var required = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Name.Any(char.IsControl) || !seen.Add(field.Name))
                throw new ArgumentException("Trusted schema fields must have unique valid names.", nameof(schema));
            properties.Add(field.Name, TypeSchema(field.Type, schemas, 0));
            required.Add(field.Name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    private static string SchemaKey(DescriptorReference descriptor) => descriptor.Kind + ":" + descriptor.Name + "@" + descriptor.Version;
    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
