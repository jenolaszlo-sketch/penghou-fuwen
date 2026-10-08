using FluentAssertions;
using Fuwen.Inspect;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Tests;

/// <summary>
/// Explanatory comparison over real files: two revision lineages and their
/// bound verified definitions, including structural rename, plan-level
/// semantic change, and fail-closed tamper/mismatch/missing-file refusal.
/// A successful comparison never authorizes reuse.
/// </summary>
public sealed class PlanCompareTests
{
    [Fact]
    public void Compare_IdenticalRevisions_IsUnchangedAndDeterministic()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var revision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        using var files = WriteCompareFiles(definition, revision, definition, revision);

        var record = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, revision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, revision.EnvelopeFingerprint);

        record.Status.Should().Be("Succeeded");
        record.ExecutionFingerprintEqual.Should().BeTrue();
        record.Changes.Should().NotBeEmpty();
        record.Changes.Should().OnlyContain(change => change.Kind == "Unchanged");

        var repeated = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, revision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, revision.EnvelopeFingerprint);
        repeated.Changes.Should().Equal(record.Changes);
    }

    [Fact]
    public void Compare_StructuralRename_ReportsRemovedAndAdded()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterPlan = Rename(beforePlan, "answer/audit", "audit_v2");
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var beforeRevision = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var afterRevision = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());
        using var files = WriteCompareFiles(
            beforeDefinition, beforeRevision, afterDefinition, afterRevision);

        var record = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, beforeRevision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, afterRevision.EnvelopeFingerprint);

        record.Status.Should().Be("Succeeded");
        record.ExecutionFingerprintEqual.Should().BeFalse();
        record.Changes.Should().Contain(new InspectedChange("answer/audit", "Removed"));
        record.Changes.Should().Contain(new InspectedChange("answer/audit_v2", "Added"));
    }

    [Fact]
    public void Compare_RevisionSemanticChange_ReportsPlanLevelChange()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var beforeRevision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        var afterRevision = PlanRevisionDocument.Create(
            definition, "revision/2", "revision/1", Semantics(objective: 'f'));
        using var files = WriteCompareFiles(definition, beforeRevision, definition, afterRevision);

        var record = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, beforeRevision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, afterRevision.EnvelopeFingerprint);

        record.Status.Should().Be("Succeeded");
        record.ExecutionFingerprintEqual.Should().BeTrue();
        record.Changes.Should().Contain(new InspectedChange(null, "ObjectiveChanged"));
    }

    [Fact]
    public void Compare_WrongRevisionFingerprint_FailsClosed()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var revision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        using var files = WriteCompareFiles(definition, revision, definition, revision);
        var wrongFingerprint = "sha256:fuwen-revision/v1:" + new string('0', 64);

        var record = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, wrongFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, revision.EnvelopeFingerprint);

        record.Status.Should().Be("Failed");
        record.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Compare_DefinitionNotBoundToRevision_FailsClosed()
    {
        var boundDefinition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var otherDefinition = WorkflowDefinitionDocument.Create(
            Rename(PlanFixture.CreateV2(), "answer/audit", "audit_v2"));
        var revision = PlanRevisionDocument.Create(boundDefinition, "revision/1", null, Semantics());
        using var files = WriteCompareFiles(
            otherDefinition, revision, boundDefinition, revision);

        var record = PlanInspector.Compare(
            files.BeforeDefinitionPath, files.BeforeRevisionPath, revision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, revision.EnvelopeFingerprint);

        record.Status.Should().Be("Failed");
        record.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Compare_MissingFile_FailsClosed()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var revision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        using var files = WriteCompareFiles(definition, revision, definition, revision);
        var missing = Path.Combine(Path.GetTempPath(), "fuwen-compare-" + Guid.NewGuid().ToString("N") + ".json");

        var record = PlanInspector.Compare(
            missing, files.BeforeRevisionPath, revision.EnvelopeFingerprint,
            files.AfterDefinitionPath, files.AfterRevisionPath, revision.EnvelopeFingerprint);

        record.Status.Should().Be("Failed");
    }

    private static WorkflowPlan Rename(WorkflowPlan plan, string oldPath, string newName)
    {
        var newPath = "answer/" + newName;
        return plan with
        {
            Nodes = plan.Nodes.Select(node => node.StructuralPath == oldPath
                ? node with { Name = newName, StructuralPath = newPath }
                : node).ToArray(),
            ExecutionOrder = plan.ExecutionOrder! with
            {
                Regions = plan.ExecutionOrder.Regions.Select(region => region with
                {
                    Phases = region.Phases.Select(phase => phase with
                    {
                        NodePaths = phase.NodePaths.Select(path => path == oldPath ? newPath : path).ToArray(),
                    }).ToArray(),
                }).ToArray(),
            },
        };
    }

    private static CompareFixtures WriteCompareFiles(
        WorkflowDefinitionDocument beforeDefinition,
        PlanRevisionDocument beforeRevision,
        WorkflowDefinitionDocument afterDefinition,
        PlanRevisionDocument afterRevision) =>
        new(
            new TempPlanFile(beforeDefinition.CanonicalBytes.ToArray()),
            new TempPlanFile(beforeRevision.CanonicalBytes.ToArray()),
            new TempPlanFile(afterDefinition.CanonicalBytes.ToArray()),
            new TempPlanFile(afterRevision.CanonicalBytes.ToArray()));

    private static PlanRevisionSemantics Semantics(
        char objective = 'a',
        char acceptance = 'b',
        char validation = 'c') => new(
        Digest("objective/v1", objective),
        Digest("acceptance/v1", acceptance),
        Digest("validation/v1", validation));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, new string(value, 64));

    private sealed class TempPlanFile : IDisposable
    {
        public string Path { get; }

        public TempPlanFile(byte[] bytes)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "fuwen-compare-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(Path, bytes);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class CompareFixtures : IDisposable
    {
        private readonly TempPlanFile beforeDefinition;
        private readonly TempPlanFile beforeRevision;
        private readonly TempPlanFile afterDefinition;
        private readonly TempPlanFile afterRevision;

        public CompareFixtures(
            TempPlanFile beforeDefinition, TempPlanFile beforeRevision,
            TempPlanFile afterDefinition, TempPlanFile afterRevision)
        {
            this.beforeDefinition = beforeDefinition;
            this.beforeRevision = beforeRevision;
            this.afterDefinition = afterDefinition;
            this.afterRevision = afterRevision;
        }

        public string BeforeDefinitionPath => beforeDefinition.Path;
        public string BeforeRevisionPath => beforeRevision.Path;
        public string AfterDefinitionPath => afterDefinition.Path;
        public string AfterRevisionPath => afterRevision.Path;

        public void Dispose()
        {
            beforeDefinition.Dispose();
            beforeRevision.Dispose();
            afterDefinition.Dispose();
            afterRevision.Dispose();
        }
    }
}
