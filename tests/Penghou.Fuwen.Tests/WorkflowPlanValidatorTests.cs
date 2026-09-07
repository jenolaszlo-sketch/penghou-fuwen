using FluentAssertions;

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
}
