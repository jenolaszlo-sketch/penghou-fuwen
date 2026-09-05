using System.Text;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowDefinitionStoreTests
{
    [Fact]
    public async Task Store_is_idempotent_and_read_revalidates_the_plan()
    {
        IWorkflowDefinitionStore store = new InMemoryWorkflowDefinitionStore();
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.StoreAsync(definition, cancellationToken);
        var replay = await store.StoreAsync(definition, cancellationToken);
        var loaded = await store.ReadAsync(definition.ExecutionFingerprint, cancellationToken);

        first.Should().Be(WorkflowDefinitionWriteDisposition.Created);
        replay.Should().Be(WorkflowDefinitionWriteDisposition.AlreadyExists);
        loaded.Should().NotBeNull();
        loaded!.ExecutionFingerprint.Should().Be(definition.ExecutionFingerprint);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(loaded.ReadPlan())
            .Should().Be(definition.ExecutionFingerprint);
    }

    [Fact]
    public void Load_rejects_noncanonical_persisted_JSON()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var noncanonical = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(definition.CanonicalBytes.Span));

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            definition.ExecutionFingerprint,
            noncanonical);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*not canonical*");
    }

    [Fact]
    public void Load_rejects_a_false_fingerprint_claim()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            definition.CanonicalBytes.Span);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*fingerprint mismatch*");
    }

    [Fact]
    public void Load_rejects_malformed_fingerprint_keys_before_reading_JSON()
    {
        var act = () => WorkflowDefinitionDocument.LoadVerified("../../definition", []);

        act.Should().Throw<ArgumentException>().WithMessage("*canonical sha256*");
    }

    [Fact]
    public void Load_rejects_oversized_persisted_definitions()
    {
        var oversized = new byte[FuwenContracts.MaximumCanonicalPlanBytes + 1];

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            oversized);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*exceeds*");
    }

    [Fact]
    public void Exposed_bytes_are_defensive_copies()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var first = definition.CanonicalBytes.ToArray();
        first[0] ^= 0xff;

        definition.CanonicalBytes.Span[0].Should().NotBe(first[0]);
        definition.ReadPlan().Should().NotBeNull();
    }

    [Fact]
    public async Task Read_returns_null_for_an_unknown_fingerprint()
    {
        IWorkflowDefinitionStore store = new InMemoryWorkflowDefinitionStore();

        var loaded = await store.ReadAsync(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            TestContext.Current.CancellationToken);

        loaded.Should().BeNull();
    }
}
