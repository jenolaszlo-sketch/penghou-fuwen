using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Xunit;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class ModelFacingToolContractBuilderTests
{
    [Fact]
    public void Builder_exports_exact_typed_arguments_and_result_with_descriptor_and_validator_binding()
    {
        var tool = Descriptor(DescriptorKind.Tool, "catalog.lookup");
        var request = Descriptor(DescriptorKind.Schema, "catalog.request");
        var response = Descriptor(DescriptorKind.Schema, "catalog.response");
        var schemas = new ResolvedSchemaDefinition[]
        {
            new ObjectSchemaDefinition(request, [
                new SchemaField("query", new PrimitiveType(FuwenPrimitiveKind.String)),
                new SchemaField("limit", new PrimitiveType(FuwenPrimitiveKind.Integer)),
            ]),
            new ObjectSchemaDefinition(response, [new SchemaField("matches", new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 5))]),
        };
        var callable = new CallableContract(
            new CallableSignature([new CallableParameter("request", new NamedTypeReference(request))], new NamedTypeReference(response)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);

        var contract = ModelFacingToolContractBuilder.Build(tool, callable, schemas);

        contract.ProviderName.Should().Be(tool.Name);
        contract.DescriptorDigest.Should().Be(tool.ContentDigest.Value);
        contract.ValidatorIdentity.Should().Be(ModelFacingToolContractBuilder.ValidatorIdentity);
        contract.ContractDigest.Should().MatchRegex("^[0-9a-f]{64}$");
        using var parameters = JsonDocument.Parse(contract.ParametersSchemaJson);
        var parameter = parameters.RootElement.GetProperty("properties").GetProperty("request");
        parameter.GetProperty("$ref").GetString().Should().Be("#/$defs/Schema:catalog.request@1");
        parameters.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var requestSchema = parameters.RootElement.GetProperty("$defs").GetProperty("Schema:catalog.request@1");
        requestSchema.GetProperty("required").EnumerateArray().Select(static value => value.GetString()).Should().Equal("query", "limit");
        requestSchema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString().Should().Be("string");
        requestSchema.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString().Should().Be("integer");
        using var result = JsonDocument.Parse(contract.ResultSchemaJson);
        result.RootElement.GetProperty("$defs").GetProperty("Schema:catalog.response@1")
            .GetProperty("properties").GetProperty("matches").GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        result.RootElement.GetProperty("$defs").GetProperty("Schema:catalog.response@1")
            .GetProperty("properties").GetProperty("matches").GetProperty("maxItems").GetInt32().Should().Be(5);

        var requirement = new InferenceToolRequirement(tool, InferenceToolEffect.ReadOnly, contract);
        var requestTurn = new InferenceTurnRequest("test", 0, [new InferenceConversationMessage(InferenceTurnRole.User, "go")], [requirement]);
        requestTurn.VisibleTools[0].ModelContract!.ContractDigest.Should().Be(contract.ContractDigest);
    }

    [Fact]
    public void Builder_rejects_untrusted_artifact_construction_and_missing_schema_references()
    {
        var tool = Descriptor(DescriptorKind.Tool, "catalog.lookup");
        var callable = new CallableContract(
            new CallableSignature([new CallableParameter("artifact", new ArtifactType(Descriptor(DescriptorKind.Artifact, "catalog.file")))], new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        Action artifact = () => ModelFacingToolContractBuilder.Build(tool, callable, []);
        artifact.Should().Throw<ArgumentException>().WithMessage("*Artifact references*");

        var missing = new CallableContract(
            new CallableSignature([new CallableParameter("request", new NamedTypeReference(Descriptor(DescriptorKind.Schema, "catalog.missing")))], new PrimitiveType(FuwenPrimitiveKind.String)),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        Action absentSchema = () => ModelFacingToolContractBuilder.Build(tool, missing, []);
        absentSchema.Should().Throw<ArgumentException>().WithMessage("*schema is missing*");
    }

    [Fact]
    public void Model_contract_must_bind_exact_tool_digest_and_contain_json_schemas()
    {
        var tool = Descriptor(DescriptorKind.Tool, "catalog.lookup");
        var contract = new InferenceModelToolContract("lookup", "{}", "{}", "wrong", "validator/v1", "digest");
        Action mismatch = () => new InferenceToolRequirement(tool, modelContract: contract);
        mismatch.Should().Throw<ArgumentException>();
        Action malformed = () => new InferenceModelToolContract("lookup", "not-json", "{}", tool.ContentDigest.Value, "validator/v1", "digest");
        malformed.Should().Throw<ArgumentException>();
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "test/v1", new string('a', 64)));
}
