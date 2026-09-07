using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class PlanRevisionComparerTests
{
    [Fact]
    public void Compare_identical_revisions_is_unchanged_and_deterministic()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var revision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());

        var comparison = PlanRevisionComparer.Compare(revision, definition, revision, definition);

        comparison.ExecutionFingerprintEqual.Should().BeTrue();
        comparison.Changes.Should().Equal(
            new PlanChange("answer/audit", PlanChangeKind.Unchanged),
            new PlanChange("answer/context", PlanChangeKind.Unchanged),
            new PlanChange("answer/infer", PlanChangeKind.Unchanged),
            new PlanChange("answer/return_result", PlanChangeKind.Unchanged),
            new PlanChange("answer/validate", PlanChangeKind.Unchanged));
        var repeated = PlanRevisionComparer.Compare(revision, definition, revision, definition);
        repeated.ExecutionFingerprintEqual.Should().Be(comparison.ExecutionFingerprintEqual);
        repeated.Changes.Should().Equal(comparison.Changes);
    }

    [Theory]
    [InlineData("objective", PlanChangeKind.ObjectiveChanged)]
    [InlineData("acceptance", PlanChangeKind.AcceptanceCriteriaChanged)]
    [InlineData("validation", PlanChangeKind.ValidationRequirementChanged)]
    public void Compare_reports_revision_semantic_changes(string changedSemantic, PlanChangeKind expectedKind)
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var before = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(definition, "revision/2", "revision/1", changedSemantic switch
        {
            "objective" => Semantics(objective: 'f'),
            "acceptance" => Semantics(acceptance: 'f'),
            "validation" => Semantics(validation: 'f'),
            _ => throw new ArgumentOutOfRangeException(nameof(changedSemantic)),
        });

        var comparison = PlanRevisionComparer.Compare(before, definition, after, definition);

        comparison.ExecutionFingerprintEqual.Should().BeTrue();
        comparison.Changes.Should().Contain(new PlanChange(null, expectedKind));
        comparison.Changes.Where(change => change.StructuralPath is null)
            .Should().ContainSingle(change => change.Kind == expectedKind);
        comparison.Changes.Where(change => change.StructuralPath is not null)
            .Should().OnlyContain(change => change.Kind == PlanChangeKind.Unchanged);
    }

    [Fact]
    public void Compare_treats_an_exact_path_rename_as_removed_and_added()
    {
        var beforePlan = PlanFixture.CreateV2();
        var oldPath = "answer/audit";
        var newPath = "answer/audit_v2";
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var renamedPlan = beforePlan with
        {
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == oldPath
                ? node with { Name = "audit_v2", StructuralPath = newPath }
                : node).ToArray(),
            ExecutionOrder = beforePlan.ExecutionOrder! with
            {
                Regions = beforePlan.ExecutionOrder.Regions.Select(region => region with
                {
                    Phases = region.Phases.Select(phase => phase with
                    {
                        NodePaths = phase.NodePaths.Select(path => path == oldPath ? newPath : path).ToArray(),
                    }).ToArray(),
                }).ToArray(),
            },
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(renamedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(oldPath, PlanChangeKind.Removed));
        comparison.Changes.Should().Contain(new PlanChange(newPath, PlanChangeKind.Added));
        comparison.Changes.Should().NotContain(new PlanChange(oldPath, PlanChangeKind.Changed));
        comparison.Changes.Should().NotContain(new PlanChange(newPath, PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_dependency_only_binding_change()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var inferencePath = "answer/infer";
        var changedPlan = beforePlan with
        {
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == inferencePath
                ? ((InferenceNode)node) with
                {
                    Arguments = [new ArgumentBinding("request", new InputBinding(["question"]))],
                }
                : node).ToArray(),
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(inferencePath, PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().NotContain(new PlanChange(inferencePath, PlanChangeKind.Changed));
        comparison.Changes.Where(change => change.StructuralPath is null)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_reports_descriptor_change_as_node_semantic_change()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var inference = (InferenceNode)beforePlan.Nodes.Single(node => node.StructuralPath == "answer/infer");
        var changedProfile = inference.Profile with
        {
            ContentDigest = new ContentDigest("sha256", "descriptor/v1", new string('f', 64)),
        };
        var changedPlan = beforePlan with
        {
            CatalogueBindings = beforePlan.CatalogueBindings.Select(descriptor => descriptor == inference.Profile ? changedProfile : descriptor).ToArray(),
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == inference.StructuralPath
                ? inference with { Profile = changedProfile }
                : node).ToArray(),
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.Changes.Should().Contain(new PlanChange(inference.StructuralPath, PlanChangeKind.Changed));
        comparison.Changes.Should().NotContain(new PlanChange(inference.StructuralPath, PlanChangeKind.DependencyChanged));
    }

    [Fact]
    public void Compare_reports_simultaneous_root_contract_and_execution_order_changes()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var rootRegion = beforePlan.ExecutionOrder!.Regions.Single();
        var changedOrder = beforePlan.ExecutionOrder with
        {
            Regions =
            [
                rootRegion with
                {
                    Phases =
                    [
                        rootRegion.Phases[0],
                        rootRegion.Phases[1],
                        new WorkflowExecutionPhase(["answer/validate"]),
                        new WorkflowExecutionPhase(["answer/audit"]),
                        rootRegion.Phases[3],
                    ],
                },
            ],
        };
        var afterPlan = beforePlan with
        {
            RoutingPolicyRevision = "routing/2",
            ExecutionOrder = changedOrder,
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.Changes.Where(change => change.StructuralPath is null).Should().Equal(
            new PlanChange(null, PlanChangeKind.Changed),
            new PlanChange(null, PlanChangeKind.DependencyChanged));
    }

    [Fact]
    public void Comparison_result_defensively_snapshots_changes()
    {
        var changes = new List<PlanChange> { new("answer/context", PlanChangeKind.Unchanged) };
        var comparison = new PlanRevisionComparison(true, changes);

        changes.Clear();

        comparison.Changes.Should().ContainSingle()
            .Which.Should().Be(new PlanChange("answer/context", PlanChangeKind.Unchanged));
    }

    [Fact]
    public void Compare_rejects_an_integrity_verified_but_structurally_invalid_definition()
    {
        var validDefinition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var malformedPlan = PlanFixture.CreateV2() with
        {
            Nodes = [.. PlanFixture.CreateV2().Nodes, PlanFixture.CreateV2().Nodes[0]],
        };
        var bytes = WorkflowPlanIdentity.GetCanonicalBytesForVerification(malformedPlan);
        var fingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(bytes, malformedPlan.FingerprintVersion);
        var malformedDefinition = WorkflowDefinitionDocument.LoadVerified(fingerprint, bytes);
        var malformedRevision = PlanRevisionDocument.Create(malformedDefinition, "revision/invalid", null, Semantics());
        var validRevision = PlanRevisionDocument.Create(validDefinition, "revision/valid", null, Semantics());

        var act = () => PlanRevisionComparer.Compare(
            malformedRevision,
            malformedDefinition,
            validRevision,
            validDefinition);

        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("beforeDefinition");
    }

    [Fact]
    public void Compare_rejects_a_revision_bound_to_a_different_definition()
    {
        var beforeDefinition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var unrelatedDefinition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(beforeDefinition, "revision/2", "revision/1", Semantics());

        var act = () => PlanRevisionComparer.Compare(before, unrelatedDefinition, after, beforeDefinition);

        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("beforeDefinition");
    }

    private static PlanRevisionSemantics Semantics(
        char objective = 'a',
        char acceptance = 'b',
        char validation = 'c') => new(
        Digest("objective/v1", objective),
        Digest("acceptance/v1", acceptance),
        Digest("validation/v1", validation));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, new string(value, 64));
}
