using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class InferenceLimitTests
{
    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, new string(value, 64));

    private static PlanRevisionSemantics Semantics() => new(
        Digest("objective/v1", 'a'),
        Digest("acceptance/v1", 'b'),
        Digest("validation/v1", 'c'));

    private static WorkflowPlan WithLimits(WorkflowPlan plan, int? maxTokens, int? timeoutSeconds) =>
        plan with
        {
            Nodes = plan.Nodes.Select(node => node is InferenceNode inference &&
                inference.StructuralPath == "answer/infer"
                    ? inference with { Limits = new InferenceLimits(maxTokens, timeoutSeconds) }
                    : node).ToArray(),
        };

    [Fact]
    public void Limits_participate_in_fingerprints()
    {
        var basePlan = PlanFixture.CreateV2();

        var limited = WithLimits(basePlan, 400, 30);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(limited)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(basePlan));

        var retimed = WithLimits(basePlan, 400, 60);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(retimed)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(limited));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(limited)
            .Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(WithLimits(basePlan, 400, 30)));
    }

    [Fact]
    public void Comparison_detects_limit_changes()
    {
        var basePlan = PlanFixture.CreateV2();
        foreach (var (maxTokens, timeoutSeconds) in new (int?, int?)[]
        {
            (800, 30),
            (400, 60),
        })
        {
            var before = WithLimits(basePlan, 400, 30);
            var after = WithLimits(basePlan, maxTokens, timeoutSeconds);
            var beforeDefinition = WorkflowDefinitionDocument.Create(before);
            var afterDefinition = WorkflowDefinitionDocument.Create(after);

            var comparison = PlanRevisionComparer.Compare(
                PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics()),
                beforeDefinition,
                PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics()),
                afterDefinition);

            comparison.ExecutionFingerprintEqual.Should().BeFalse();
            comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.Changed));
        }
    }

    [Fact]
    public void Limits_survive_definition_round_trips()
    {
        var plan = WithLimits(PlanFixture.CreateV2(), 400, 30);

        var reread = WorkflowDefinitionDocument.Create(plan).ReadPlan();
        var inference = reread.Nodes.OfType<InferenceNode>()
            .Single(node => node.StructuralPath == "answer/infer");

        inference.Limits.Should().Be(new InferenceLimits(400, 30));
    }
}
