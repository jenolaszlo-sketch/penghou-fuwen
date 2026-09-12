using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed class FuwenZhinuWorkflowFactoryTests
{
    [Theory]
    [InlineData(null, "descriptors/1")]
    [InlineData("", "descriptors/1")]
    [InlineData("catalogue/1", null)]
    [InlineData("catalogue/1", "")]
    public void ProviderRuntimeIdentity_rejects_missing_identity_parts(
        string? catalogueRevision,
        string? descriptorFingerprint)
    {
        var action = () => new FuwenZhinuProviderRuntimeIdentity(
            catalogueRevision!,
            descriptorFingerprint!);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_StoresAdmittedDefinitionAndRegistersItsExactFingerprint()
    {
        var admission = await AdmitAsync();
        var identity = IdentityFor(admission);
        var store = new InMemoryWorkflowDefinitionStore();
        var factory = new FuwenZhinuWorkflowFactory(store, identity);

        var registration = await factory.CreateAsync(
            "fuwen.echo",
            "1",
            admission,
            TestContext.Current.CancellationToken);
        var registry = registration.Register(new WorkflowRegistry());

        registration.Definition.ExecutionFingerprint.Should().Be(admission.Receipt!.ExecutionFingerprint);
        registration.ZhinuRegistration.DefinitionFingerprint.Should().Be(admission.Receipt.ExecutionFingerprint);
        registry.Get("fuwen.echo", "1").DefinitionFingerprint.Should().Be(admission.Receipt.ExecutionFingerprint);
        (await store.ReadAsync(admission.Receipt.ExecutionFingerprint, TestContext.Current.CancellationToken))
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_RejectsMismatchedProviderRuntimeIdentity(bool catalogueMismatch)
    {
        var admission = await AdmitAsync();
        var receipt = admission.Receipt!;
        var identity = catalogueMismatch
            ? new FuwenZhinuProviderRuntimeIdentity("catalogue/other", receipt.ResolvedDescriptorSetFingerprint)
            : new FuwenZhinuProviderRuntimeIdentity(receipt.CatalogueSnapshotRevision, "descriptors/other");
        var factory = new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(), identity);

        var action = async () => await factory.CreateAsync(
            "fuwen.echo",
            "1",
            admission,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
    }

    [Fact]
    public async Task CreateAsync_RejectsResultWithoutAdmissionReceipt()
    {
        var admission = await AdmitAsync(CapabilityGrantPolicy.AllowAll);
        admission.Compilation.Succeeded.Should().BeTrue();
        admission.Succeeded.Should().BeFalse();
        admission.Receipt.Should().BeNull();
        var factory = new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity("catalogue/unused", "descriptors/unused"));

        var action = async () => await factory.CreateAsync(
            "fuwen.echo",
            "1",
            admission,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
    }

    [Fact]
    public async Task CreateAsync_RejectsStoreThatCannotLoadTheDefinitionItAccepted()
    {
        var admission = await AdmitAsync();
        var factory = new FuwenZhinuWorkflowFactory(new MissingReadStore(), IdentityFor(admission));

        var action = async () => await factory.CreateAsync(
            "fuwen.echo",
            "1",
            admission,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
    }

    [Fact]
    public async Task CreateAsync_IsFingerprintStableAcrossIndependentStores()
    {
        var admission = await AdmitAsync();
        var first = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var second = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);

        first.Definition.ExecutionFingerprint.Should().Be(second.Definition.ExecutionFingerprint);
        first.Definition.CanonicalBytes.Span.SequenceEqual(second.Definition.CanonicalBytes.Span).Should().BeTrue();
        first.ZhinuRegistration.DefinitionFingerprint.Should().Be(second.ZhinuRegistration.DefinitionFingerprint);
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(CapabilityGrantPolicy? policy = null)
    {
        var plan = CreatePlan();
        return await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([]),
                capabilityPolicy: policy ?? new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static WorkflowPlan CreatePlan()
    {
        var outputType = new PrimitiveType(FuwenPrimitiveKind.String);
        var returnPath = StructuralNodeIdentity.Create("echo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersionV2,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV2,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV2,
            "echo",
            "1",
            outputType,
            outputType,
            "routing/1",
            [],
            [],
            new CapabilityManifest([]),
            [new ReturnNode("return_result", returnPath, new InputBinding([]))],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("echo", [new WorkflowExecutionPhase([returnPath])]),
            ]));
    }

    private static FuwenZhinuProviderRuntimeIdentity IdentityFor(WorkflowAdmissionResult admission) => new(
        admission.Receipt!.CatalogueSnapshotRevision,
        admission.Receipt.ResolvedDescriptorSetFingerprint);

    private sealed class MissingReadStore : IWorkflowDefinitionStore
    {
        public ValueTask<WorkflowDefinitionWriteDisposition> StoreAsync(
            WorkflowDefinitionDocument definition,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(WorkflowDefinitionWriteDisposition.Created);

        public ValueTask<WorkflowDefinitionDocument?> ReadAsync(
            string executionFingerprint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorkflowDefinitionDocument?>(null);
    }
}
