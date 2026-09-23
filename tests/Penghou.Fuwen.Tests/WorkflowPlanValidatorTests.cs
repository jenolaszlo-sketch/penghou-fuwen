using FluentAssertions;
using System.Text.Json;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowPlanValidatorTests
{
    [Fact]
    public void Descriptor_identity_validation_does_not_use_delimiters()
    {
        var plan = PlanFixture.Create() with
        {
            CatalogueBindings =
            [
                .. PlanFixture.Create().CatalogueBindings,
                PlanFixture.Descriptor(DescriptorKind.Schema, "delimiter|name") with { Version = "version" },
                PlanFixture.Descriptor(DescriptorKind.Schema, "delimiter") with { Version = "name|version" },
            ],
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().NotThrow();
    }

    [Fact]
    public void Capability_identity_validation_does_not_use_delimiters()
    {
        var plan = PlanFixture.Create() with
        {
            CapabilityManifest = new CapabilityManifest(
            [
                new CapabilityRequirement("capability|name", "scope"),
                new CapabilityRequirement("capability", "name|scope"),
            ]),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("sha-256", "descriptor/v1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256", "descriptor/v1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256", "descriptor/v1", "aaaaaaaa")]
    [InlineData("sha256", "", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Descriptor_admission_requires_canonical_sha256_identity(
        string algorithm,
        string contract,
        string value)
    {
        var source = PlanFixture.Create();
        var descriptor = source.CatalogueBindings[0] with
        {
            ContentDigest = new ContentDigest(algorithm, contract, value),
        };
        var plan = source with { CatalogueBindings = [descriptor, .. source.CatalogueBindings.Skip(1)] };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*descriptor content digest*");
    }

    [Fact]
    public void Descriptor_admission_rejects_missing_digest_value_without_leaking_a_null_reference_failure()
    {
        var plan = PlanFixture.Create();
        var malformedDescriptor = plan.CatalogueBindings[0] with
        {
            ContentDigest = new ContentDigest("sha256", "descriptor/v1", null!),
        };
        plan = plan with
        {
            CatalogueBindings = [malformedDescriptor, .. plan.CatalogueBindings.Skip(1)],
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*descriptor content digest*");
    }

    [Fact]
    public void Descriptor_admission_accepts_lowercase_sha256_identity()
    {
        var act = () => WorkflowPlanValidator.Validate(PlanFixture.Create());

        act.Should().NotThrow();
    }

    [Fact]
    public void Schema_validation_rejects_recursive_named_references_with_stable_cycle_text()
    {
        var source = PlanFixture.Create();
        var request = source.Schemas.OfType<ObjectSchemaDefinition>().Single(schema => schema.Descriptor.Name == "sample.request");
        var recursive = request with
        {
            Fields = [.. request.Fields, new SchemaField("next", new NamedTypeReference(request.Descriptor))],
        };
        var plan = source with
        {
            Schemas = [recursive, .. source.Schemas.Where(schema => schema != request)],
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Recursive schema reference is not supported: schema:sample.request@1 -> schema:sample.request@1.*");
    }

    [Fact]
    public void Schema_validation_rejects_indirect_cycles_in_deterministic_order()
    {
        var source = PlanFixture.Create();
        var request = source.Schemas.OfType<ObjectSchemaDefinition>().Single(schema => schema.Descriptor.Name == "sample.request");
        var answer = source.Schemas.OfType<ObjectSchemaDefinition>().Single(schema => schema.Descriptor.Name == "sample.answer");
        var plan = source with
        {
            Schemas = source.Schemas.Select(schema => schema switch
            {
                ObjectSchemaDefinition value when value.Descriptor == request.Descriptor =>
                    value with { Fields = [.. value.Fields, new SchemaField("answer", new NamedTypeReference(answer.Descriptor))] },
                ObjectSchemaDefinition value when value.Descriptor == answer.Descriptor =>
                    value with { Fields = [.. value.Fields, new SchemaField("request", new NamedTypeReference(request.Descriptor))] },
                _ => schema,
            }).ToArray(),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*schema:sample.answer@1 -> schema:sample.request@1 -> schema:sample.answer@1.*");
    }

    [Theory]
    [InlineData("high", true)]
    [InlineData("unknown", false)]
    public void Enum_output_literals_are_checked_against_serialized_member_values(string value, bool valid)
    {
        var source = PlanFixture.CreateV2();
        var severity = source.Schemas.OfType<EnumSchemaDefinition>().Single().Descriptor;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var plan = source with
        {
            OutputType = new NamedTypeReference(severity),
            Nodes = source.Nodes.Select(node => node is ReturnNode result
                ? result with { Value = new LiteralBinding(document.RootElement.Clone()) }
                : node).ToArray(),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        if (valid)
            act.Should().NotThrow();
        else
            act.Should().Throw<ArgumentException>().WithMessage("*incompatible with the declared output type*");
    }

    [Fact]
    public void Enum_output_literals_reject_non_string_json_values()
    {
        var source = PlanFixture.CreateV2();
        var severity = source.Schemas.OfType<EnumSchemaDefinition>().Single().Descriptor;
        using var document = JsonDocument.Parse("1");
        var plan = source with
        {
            OutputType = new NamedTypeReference(severity),
            Nodes = source.Nodes.Select(node => node is ReturnNode result
                ? result with { Value = new LiteralBinding(document.RootElement.Clone()) }
                : node).ToArray(),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*incompatible with the declared output type*");
    }

    [Fact]
    public void Value_producing_conditionals_are_accepted_on_current_ir_with_interaction_gates()
    {
        // R13: a current-IR plan combining a conditional merge with checkpoint and
        // wait nodes must validate.
        var source = PlanFixture.CreateV5();
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var checkpointPath = StructuralNodeIdentity.Create(source.Name, "saved");
        var waitPath = StructuralNodeIdentity.Create(source.Name, "approval");
        var checkPath = source.Nodes.OfType<ConditionalNode>().Single().StructuralPath;
        var returnNode = source.Nodes.OfType<ReturnNode>().Single();
        var plan = source with
        {
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
            Nodes = [
                .. source.Nodes.Where(node => node is not ReturnNode),
                new CheckpointNode("saved", checkpointPath, new NodeOutputBinding(checkPath, []), str),
                new WaitNode("approval", waitPath, "approval_request", str, 3600),
                returnNode with { Value = new NodeOutputBinding(waitPath, []) },
            ],
            ExecutionOrder = new WorkflowExecutionOrder([
                new WorkflowExecutionRegion(source.Name, [
                    new WorkflowExecutionPhase([checkPath]),
                    new WorkflowExecutionPhase([checkpointPath]),
                    new WorkflowExecutionPhase([waitPath]),
                    new WorkflowExecutionPhase([returnNode.StructuralPath]),
                ]),
                .. source.ExecutionOrder!.Regions.Where(region => !string.Equals(region.RegionPath, source.Name, StringComparison.Ordinal)),
            ]),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().NotThrow();
    }

    [Fact]
    public void Typed_context_requirements_are_accepted_on_current_ir()
    {
        // R14: inference nodes with typed context requirements must validate
        // on the current IR.
        var plan = PlanFixture.CreateV3() with
        {
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("fuwen-ir/v2")]
    [InlineData("fuwen-ir/v99")]
    public void Unsupported_ir_versions_are_rejected_without_silent_upgrade(
        string irVersion)
    {
        var plan = PlanFixture.CreateV3() with
        {
            IrVersion = irVersion,
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<NotSupportedException>().WithMessage($"*{irVersion}*");
    }
}
