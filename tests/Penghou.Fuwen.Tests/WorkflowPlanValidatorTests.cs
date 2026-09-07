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
}
