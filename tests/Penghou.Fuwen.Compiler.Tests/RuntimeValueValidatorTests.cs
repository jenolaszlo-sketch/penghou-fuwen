using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class RuntimeValueValidatorTests
{
    [Fact]
    public void Validator_distinguishes_absent_optional_values_from_explicit_json_null()
    {
        var optional = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String));
        RuntimeValueValidator.Validate(null, optional, []).Succeeded.Should().BeTrue();
        RuntimeValueValidator.Validate(null, new PrimitiveType(FuwenPrimitiveKind.String), [])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueMissing);

        using var document = JsonDocument.Parse("null");
        RuntimeValueValidator.Validate(new JsonRuntimeValue(document.RootElement), optional, []).Succeeded.Should().BeTrue();
        RuntimeValueValidator.Validate(new JsonRuntimeValue(document.RootElement), new PrimitiveType(FuwenPrimitiveKind.String), [])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueKindMismatch);
    }

    [Theory]
    [InlineData("\"hello\"", true)]
    [InlineData("1", false)]
    [InlineData("1.0", false)]
    [InlineData("1e0", false)]
    public void Validator_is_strict_and_does_not_coerce_primitive_values(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        var result = RuntimeValueValidator.Validate(
            new JsonRuntimeValue(document.RootElement),
            new PrimitiveType(FuwenPrimitiveKind.String),
            []);

        result.Succeeded.Should().Be(expected);
        if (!expected) result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueKindMismatch);
    }

    [Fact]
    public void Validator_checks_required_and_unknown_object_fields()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.request", 's');
        var schema = new ObjectSchemaDefinition(descriptor, [
            new SchemaField("name", new PrimitiveType(FuwenPrimitiveKind.String)),
            new SchemaField("comment", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
        ]);
        using var document = JsonDocument.Parse("{\"unknown\":true}");

        var result = RuntimeValueValidator.Validate(new JsonRuntimeValue(document.RootElement), new NamedTypeReference(descriptor), [schema]);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueUnknownField);
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueRequiredFieldMissing);
    }

    [Fact]
    public void Validator_checks_list_bounds_enum_values_and_exact_artifact_descriptor()
    {
        var enumDescriptor = Descriptor(DescriptorKind.Schema, "sample.kind", 'e');
        var enumSchema = new EnumSchemaDefinition(enumDescriptor, [new EnumMember("Ready", "ready")]);
        using var invalidEnum = JsonDocument.Parse("\"READY\"");
        RuntimeValueValidator.Validate(new JsonRuntimeValue(invalidEnum.RootElement), new NamedTypeReference(enumDescriptor), [enumSchema])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueEnumLiteralInvalid);

        using var list = JsonDocument.Parse("[\"a\",\"b\"]");
        RuntimeValueValidator.Validate(
                new JsonRuntimeValue(list.RootElement),
                new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 1),
                [])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueListLimitExceeded);

        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "image/png", 'a');
        var wrongDescriptor = Descriptor(DescriptorKind.Artifact, "image/jpeg", 'j');
        var artifact = new ArtifactReference("store", "image/1", wrongDescriptor, Digest("artifact/v1", 'x'));
        RuntimeValueValidator.Validate(
                new ArtifactRuntimeValue(artifact),
                new ArtifactType(artifactDescriptor),
                [])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueArtifactDescriptorMismatch);
    }

    [Fact]
    public void Validator_accepts_artifact_runtime_values_inside_lists()
    {
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "image/png", 'a');
        var artifact = new ArtifactReference("store", "image/1", artifactDescriptor, Digest("artifact/v1", 'x'));
        var value = new ListRuntimeValue([new ArtifactRuntimeValue(artifact)]);

        var result = RuntimeValueValidator.Validate(
            value,
            new ListType(new ArtifactType(artifactDescriptor), 2),
            []);

        result.Succeeded.Should().BeTrue();

        using var json = JsonDocument.Parse("[{\"provider\":\"store\"}]");
        RuntimeValueValidator.Validate(
                new JsonRuntimeValue(json.RootElement),
                new ListType(new ArtifactType(artifactDescriptor), 2),
                [])
            .Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueKindMismatch);
    }

    [Fact]
    public void Validator_accepts_artifact_fields_inside_object_runtime_values_and_rejects_json_masquerade()
    {
        var schemaDescriptor = Descriptor(DescriptorKind.Schema, "sample.asset", 's');
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "image/png", 'a');
        var schema = new ObjectSchemaDefinition(schemaDescriptor, [
            new SchemaField("asset", new ArtifactType(artifactDescriptor)),
            new SchemaField("alternates", new OptionalType(new ListType(new ArtifactType(artifactDescriptor), 2))),
        ]);
        var artifact = new ArtifactReference("store", "image/1", artifactDescriptor, Digest("artifact/v1", 'x'));
        var value = new ObjectRuntimeValue(new Dictionary<string, RuntimeValue>
        {
            ["asset"] = new ArtifactRuntimeValue(artifact),
            ["alternates"] = new ListRuntimeValue([new ArtifactRuntimeValue(artifact)]),
        });

        RuntimeValueValidator.Validate(value, new NamedTypeReference(schemaDescriptor), [schema]).Succeeded.Should().BeTrue();

        using var json = JsonDocument.Parse("{\"asset\":{\"provider\":\"store\"}}");
        var masquerade = RuntimeValueValidator.Validate(new JsonRuntimeValue(json.RootElement), new NamedTypeReference(schemaDescriptor), [schema]);
        masquerade.Succeeded.Should().BeFalse();
        masquerade.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueKindMismatch);
    }

    [Fact]
    public void Validator_rejects_noncanonical_or_oversized_numeric_values()
    {
        using var document = JsonDocument.Parse("1e1000");
        var result = RuntimeValueValidator.Validate(
            new JsonRuntimeValue(document.RootElement),
            new PrimitiveType(FuwenPrimitiveKind.Number),
            []);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueCanonicalJsonInvalid);
    }

    [Fact]
    public void Validator_rejects_malformed_collection_contracts_without_throwing()
    {
        using var listDocument = JsonDocument.Parse("[]");
        var list = RuntimeValueValidator.Validate(
            new JsonRuntimeValue(listDocument.RootElement),
            new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 0),
            []);
        list.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueMalformed);

        var descriptor = Descriptor(DescriptorKind.Schema, "sample.malformed", 'm');
        using var objectDocument = JsonDocument.Parse("{}");
        var malformedSchema = new ObjectSchemaDefinition(descriptor, [null!]);
        var result = RuntimeValueValidator.Validate(
            new JsonRuntimeValue(objectDocument.RootElement),
            new NamedTypeReference(descriptor),
            [malformedSchema]);

        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.RuntimeValueMalformed);
    }

    [Fact]
    public void Validator_bounds_diagnostic_paths_derived_from_provider_json()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.empty", 'e');
        var propertyName = new string('x', 2_000);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, bool> { [propertyName] = true }));

        var result = RuntimeValueValidator.Validate(
            new JsonRuntimeValue(document.RootElement),
            new NamedTypeReference(descriptor),
            [new ObjectSchemaDefinition(descriptor, [])]);

        var diagnostic = result.Diagnostics.Single(d => d.Code == CompilerDiagnosticCodes.RuntimeValueUnknownField);
        diagnostic.Path.Should().NotBeNull();
        diagnostic.Path!.Length.Should().BeLessThanOrEqualTo(1_024);
        diagnostic.Path.Should().EndWith("...");
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name, char digest) =>
        new(kind, name, "1", Digest("descriptor/v1", digest));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{contract}:{value}"))).ToLowerInvariant());
}
