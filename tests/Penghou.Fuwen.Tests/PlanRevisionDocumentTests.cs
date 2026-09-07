using System.Text;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class PlanRevisionDocumentTests
{
    [Fact]
    public void Create_CanonicalizesReferencesAndSnapshotsCallerOwnedValues()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var references = new List<PlanRevisionReference>
        {
            new(PlanRevisionReferenceKind.Evidence, Artifact("evidence/2", 'b')),
            new(PlanRevisionReferenceKind.Proposal, Artifact("proposal/1", 'a')),
        };

        var first = PlanRevisionDocument.Create(definition, "revision/2", "revision/1", Semantics(), references);
        var reordered = PlanRevisionDocument.Create(definition, "revision/2", "revision/1", Semantics(), references.AsEnumerable().Reverse());
        references.Clear();

        first.ReadEnvelope().References.Should().HaveCount(2);
        first.ReadEnvelope().References.Select(static item => item.Kind)
            .Should().Equal(PlanRevisionReferenceKind.Proposal, PlanRevisionReferenceKind.Evidence);
        first.EnvelopeFingerprint.Should().Be(reordered.EnvelopeFingerprint);
        first.CanonicalBytes.Span.SequenceEqual(reordered.CanonicalBytes.Span).Should().BeTrue();
        first.EnvelopeFingerprint.Should().StartWith("sha256:fuwen-revision/v1:");
    }

    [Fact]
    public void LineageIdentity_IsSeparateFromExecutableSemantics()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var first = PlanRevisionDocument.Create(definition, "revision/a", null, Semantics());
        var second = PlanRevisionDocument.Create(definition, "revision/b", "revision/a", Semantics());
        var branch = PlanRevisionDocument.Create(definition, "revision/c", "revision/a", Semantics());

        second.ReadEnvelope().ExecutionFingerprint.Should().Be(first.ReadEnvelope().ExecutionFingerprint);
        branch.ReadEnvelope().ExecutionFingerprint.Should().Be(first.ReadEnvelope().ExecutionFingerprint);
        second.EnvelopeFingerprint.Should().NotBe(first.EnvelopeFingerprint);
        branch.EnvelopeFingerprint.Should().NotBe(second.EnvelopeFingerprint);
    }

    [Fact]
    public void SemanticAndEvidenceChangesAffectOnlyEnvelopeIdentity()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var baseline = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        var objectiveChanged = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics(objective: 'f'));
        var evidenceChanged = PlanRevisionDocument.Create(
            definition,
            "revision/1",
            null,
            Semantics(),
            [new PlanRevisionReference(PlanRevisionReferenceKind.Evidence, Artifact("validation/1", 'e'))]);

        objectiveChanged.EnvelopeFingerprint.Should().NotBe(baseline.EnvelopeFingerprint);
        evidenceChanged.EnvelopeFingerprint.Should().NotBe(baseline.EnvelopeFingerprint);
        objectiveChanged.ReadEnvelope().ExecutionFingerprint.Should().Be(definition.ExecutionFingerprint);
        evidenceChanged.ReadEnvelope().ExecutionFingerprint.Should().Be(definition.ExecutionFingerprint);
    }

    [Fact]
    public void LoadVerified_RoundTripsAndRejectsTampering()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var created = PlanRevisionDocument.Create(definition, "revision/2", "revision/1", Semantics());
        var loaded = PlanRevisionDocument.LoadVerified(created.EnvelopeFingerprint, created.CanonicalBytes.Span);
        var tampered = created.CanonicalBytes.ToArray();
        tampered[^2] = tampered[^2] == (byte)'1' ? (byte)'2' : (byte)'1';
        var noncanonical = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(created.CanonicalBytes.Span) + " ");
        var falseIdentity = created.EnvelopeFingerprint[..^1] + (created.EnvelopeFingerprint[^1] == '0' ? "1" : "0");

        loaded.ReadEnvelope().Should().BeEquivalentTo(created.ReadEnvelope());
        ((Action)(() => PlanRevisionDocument.LoadVerified(created.EnvelopeFingerprint, tampered)))
            .Should().Throw<PlanRevisionIntegrityException>();
        ((Action)(() => PlanRevisionDocument.LoadVerified(created.EnvelopeFingerprint, noncanonical)))
            .Should().Throw<PlanRevisionIntegrityException>();
        ((Action)(() => PlanRevisionDocument.LoadVerified(falseIdentity, created.CanonicalBytes.Span)))
            .Should().Throw<PlanRevisionIntegrityException>();
    }

    [Fact]
    public void Create_RejectsInvalidLineageDuplicatesAndUnboundedEnumeration()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        IEnumerable<PlanRevisionReference> Infinite()
        {
            var index = 0;
            while (true)
                yield return new PlanRevisionReference(PlanRevisionReferenceKind.Evidence, Artifact($"evidence/{index++}", 'a'));
        }

        ((Action)(() => PlanRevisionDocument.Create(definition, "revision/1", "revision/1", Semantics())))
            .Should().Throw<ArgumentException>();
        ((Action)(() => PlanRevisionDocument.Create(
            definition,
            "revision/1",
            null,
            Semantics(),
            [new(PlanRevisionReferenceKind.Evidence, Artifact("same", 'a')), new(PlanRevisionReferenceKind.Evidence, Artifact("same", 'a'))])))
            .Should().Throw<ArgumentException>().WithMessage("*unique*");
        ((Action)(() => PlanRevisionDocument.Create(definition, "revision/1", null, Semantics(), Infinite())))
            .Should().Throw<ArgumentException>().WithMessage("*at most 64*");
    }

    [Fact]
    public void ArtifactReference_requires_an_artifact_descriptor()
    {
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            "not-an-artifact",
            "1",
            Digest("descriptor/v1", 'd'));

        var act = () => new ArtifactReference(
            "test-artifacts",
            "evidence/1",
            descriptor,
            Digest("artifact/v1", 'a'));

        act.Should().Throw<ArgumentException>().WithMessage("*must be of kind 'Artifact'*");
    }

    [Fact]
    public void ArtifactReference_with_cannot_bypass_the_artifact_descriptor_invariant()
    {
        var artifact = Artifact("evidence/1", 'a');
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            "not-an-artifact",
            "1",
            Digest("descriptor/v1", 'd'));

        var act = () => artifact with { ArtifactDescriptor = descriptor };

        act.Should().Throw<ArgumentException>().WithMessage("*must be of kind 'Artifact'*");
    }

    [Fact]
    public async Task Store_IsIdempotentAndRejectsRevisionIdReuseWithChangedContent()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var first = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        var changed = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics(objective: 'f'));
        var store = new InMemoryPlanRevisionStore();

        (await store.StoreAsync(first, TestContext.Current.CancellationToken))
            .Should().Be(PlanRevisionWriteDisposition.Created);
        (await store.StoreAsync(first, TestContext.Current.CancellationToken))
            .Should().Be(PlanRevisionWriteDisposition.AlreadyExists);
        var conflict = async () => await store.StoreAsync(changed, TestContext.Current.CancellationToken);
        await conflict.Should().ThrowAsync<PlanRevisionConflictException>();

        var loaded = await store.ReadAsync("revision/1", TestContext.Current.CancellationToken);
        loaded.Should().NotBeNull();
        loaded!.EnvelopeFingerprint.Should().Be(first.EnvelopeFingerprint);
        (await store.ReadAsync("revision/missing", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static PlanRevisionSemantics Semantics(char objective = 'a') => new(
        Digest("objective/v1", objective),
        Digest("acceptance/v1", 'b'),
        Digest("validation/v1", 'c'));

    private static ArtifactReference Artifact(string id, char digest) => new(
        "test-artifacts",
        id,
        new DescriptorReference(DescriptorKind.Artifact, "plan-evidence", "1", Digest("descriptor/v1", 'd')),
        Digest("artifact/v1", digest));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, new string(value, 64));
}
