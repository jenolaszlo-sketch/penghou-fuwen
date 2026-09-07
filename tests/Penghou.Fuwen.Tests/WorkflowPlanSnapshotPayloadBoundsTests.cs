using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowPlanSnapshotPayloadBoundsTests
{
    [Fact]
    public void An_individual_text_value_is_rejected_before_snapshotting()
    {
        var plan = PlanFixture.Create() with
        {
            Revision = new string('r', WorkflowPlanSnapshotLimits.MaximumTextUtf8Bytes + 1),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<WorkflowPlanSnapshotException>()
            .WithMessage("*bounded text size*");
    }

    [Fact]
    public void Aggregate_text_is_rejected_even_when_each_value_is_individually_bounded()
    {
        var descriptors = Enumerable.Range(0, 3)
            .Select(index => PlanFixture.Descriptor(
                DescriptorKind.Tool,
                $"tool-{index}-{new string('t', WorkflowPlanSnapshotLimits.MaximumTextUtf8Bytes - 16)}"))
            .ToArray();
        var plan = PlanFixture.Create() with { CatalogueBindings = descriptors };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<WorkflowPlanSnapshotException>()
            .WithMessage("*aggregate text size*");
    }

    [Fact]
    public void An_individual_json_literal_is_rejected_before_json_clone()
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new string('j', WorkflowPlanSnapshotLimits.MaximumJsonLiteralBytes)));
        var plan = PlanFixture.Create() with
        {
            Nodes = PlanFixture.Create().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new LiteralBinding(document.RootElement) }
                    : node)
                .ToArray(),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<WorkflowPlanSnapshotException>()
            .WithMessage("*JSON literal content exceeds the bounded size*");
    }

    [Fact]
    public void Aggregate_json_literal_content_is_bounded()
    {
        var literals = Enumerable.Range(0, 5)
            .Select(_ =>
            {
                var document = JsonDocument.Parse(
                    JsonSerializer.Serialize(new string('j', 900_000)));
                return new LiteralBinding(document.RootElement.Clone());
            })
            .ToArray();
        var plan = PlanFixture.Create() with
        {
            Nodes = PlanFixture.Create().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new ListBinding(literals) }
                    : node)
                .ToArray(),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<WorkflowPlanSnapshotException>()
            .WithMessage("*aggregate JSON literal content*");
    }

    [Fact]
    public void Json_literal_node_count_is_bounded_independently_of_bytes()
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(Enumerable.Repeat(0, WorkflowPlanSnapshotLimits.MaximumJsonLiteralNodes)));
        var plan = PlanFixture.Create() with
        {
            Nodes = PlanFixture.Create().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new LiteralBinding(document.RootElement) }
                    : node)
                .ToArray(),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<WorkflowPlanSnapshotException>()
            .WithMessage("*JSON literal content exceeds the bounded size*");
    }
}
