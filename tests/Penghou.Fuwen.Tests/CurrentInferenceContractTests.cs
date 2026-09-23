using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class CurrentInferenceContractTests
{
    [Fact]
    public void Aggregate_limits_are_immutable_and_part_of_current_identity()
    {
        var limits = new InferenceProtocolLimits(
            maxTurns: 4,
            maxModelCalls: 5,
            maxPromptTokens: 2_000,
            maxCompletionTokens: 1_000,
            maxTotalTokens: 3_000,
            cost: new InferenceCostLimit("USD", 12_500));
        var protocol = new InferenceProtocol(limits);
        var plan = PlanFixture.Create() with
        {
            Nodes = PlanFixture.Create().Nodes.Select(node => node is InferenceNode inference
                ? inference with { Protocol = protocol }
                : node).ToArray(),
        };

        var canonical = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        var restored = CanonicalJson.Deserialize<WorkflowPlan>(canonical);

        restored.Nodes.OfType<InferenceNode>().Single().Protocol!.Limits.Should().Be(limits);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
    }

    [Fact]
    public void Aggregate_limits_reject_non_positive_and_inconsistent_bounds()
    {
        ((Action)(() => new InferenceProtocolLimits(maxTurns: 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new InferenceProtocolLimits(maxPromptTokens: 2, maxTotalTokens: 1)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new InferenceCostLimit("USD", 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
