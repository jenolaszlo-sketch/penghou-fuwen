using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class WorkflowAdmissionTests
{
    [Fact]
    public async Task Admission_IssuesDeterministicReceiptBoundToAllAuthorities()
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var catalogue = WorkflowCompilerTests.Fixture.CreateCatalogue(plan);
        var policy = new CapabilityGrantPolicy("policy/7", []);
        var budget = new CompilationBudget(maxWorkflowNodes: 999);
        var service = new WorkflowAdmissionService(new WorkflowCompiler(catalogue, capabilityPolicy: policy));

        var first = await service.AdmitAsync(plan, budget, TestContext.Current.CancellationToken);
        var second = await service.AdmitAsync(plan, budget, TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeTrue();
        first.Diagnostics.Should().BeEmpty();
        first.Receipt.Should().NotBeNull();
        first.Receipt!.ExecutionFingerprint.Should().Be(first.Compilation.Definition!.ExecutionFingerprint);
        first.Receipt.CatalogueSnapshotRevision.Should().Be(catalogue.SnapshotRevision);
        first.Receipt.CapabilityPolicyRevision.Should().Be("policy/7");
        first.Receipt.CapabilityGrantFingerprint.Should().Be(policy.GrantSetFingerprint);
        first.Receipt.EffectiveBudget.MaxWorkflowNodes.Should().Be(999);
        first.Receipt.ReceiptFingerprint.Should().StartWith("sha256:fuwen-admission/v1:");
        second.Receipt!.ReceiptFingerprint.Should().Be(first.Receipt.ReceiptFingerprint);
    }

    [Fact]
    public async Task Admission_IdentityChangesWithTrustedMetadataPolicyGrantsAndBudget()
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var originalCatalogue = WorkflowCompilerTests.Fixture.CreateCatalogue(plan);
        var changedCatalogue = WorkflowCompilerTests.Fixture.CreateCatalogue(
            plan,
            transformCallable: static (_, contract) => contract is null
                ? null
                : contract with { Effect = CallableEffect.Write });

        var baseline = await AdmitAsync(plan, originalCatalogue, new CapabilityGrantPolicy("policy/1", []));
        var metadataChange = await AdmitAsync(plan, changedCatalogue, new CapabilityGrantPolicy("policy/1", []));
        var policyChange = await AdmitAsync(plan, originalCatalogue, new CapabilityGrantPolicy("policy/2", []));
        var grantChange = await AdmitAsync(
            plan,
            originalCatalogue,
            new CapabilityGrantPolicy("policy/1", [new CapabilityRequirement("unused.host.grant")]));
        var budgetChange = await new WorkflowAdmissionService(new WorkflowCompiler(
                originalCatalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, new CompilationBudget(maxWorkflowNodes: 999), TestContext.Current.CancellationToken);

        var fingerprints = new[]
        {
            baseline.Receipt!.ReceiptFingerprint,
            metadataChange.Receipt!.ReceiptFingerprint,
            policyChange.Receipt!.ReceiptFingerprint,
            grantChange.Receipt!.ReceiptFingerprint,
            budgetChange.Receipt!.ReceiptFingerprint,
        };
        fingerprints.Should().OnlyHaveUniqueItems();
        metadataChange.Receipt.ResolvedDescriptorSetFingerprint
            .Should().NotBe(baseline.Receipt.ResolvedDescriptorSetFingerprint);
        metadataChange.Receipt.ExecutionFingerprint.Should().Be(baseline.Receipt.ExecutionFingerprint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Admission_RejectsPoliciesWithoutStableFiniteRevision(bool allowAll)
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var policy = allowAll
            ? CapabilityGrantPolicy.AllowAll
            : new CapabilityGrantPolicy([]);
        var service = new WorkflowAdmissionService(new WorkflowCompiler(
            WorkflowCompilerTests.Fixture.CreateCatalogue(plan),
            capabilityPolicy: policy));

        var result = await service.AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Compilation.Succeeded.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Receipt.Should().BeNull();
        result.Diagnostics.Should().ContainSingle(d =>
            d.Code == CompilerDiagnosticCodes.AdmissionPolicyRevisionUnavailable);
    }

    [Fact]
    public async Task Admission_RejectsCatalogueWithoutImmutableSnapshotIdentity()
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var catalogue = new UnversionedCatalogue(WorkflowCompilerTests.Fixture.CreateCatalogue(plan));
        var service = new WorkflowAdmissionService(new WorkflowCompiler(
            catalogue,
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));

        var result = await service.AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Compilation.Succeeded.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Receipt.Should().BeNull();
        result.Diagnostics.Should().ContainSingle(d =>
            d.Code == CompilerDiagnosticCodes.AdmissionCatalogueSnapshotUnavailable);
    }

    [Fact]
    public async Task Admission_DoesNotIssueReceiptForFailedCompilation()
    {
        var source = WorkflowCompilerTests.Fixture.CreatePlan();
        var invalid = source with
        {
            OutputType = new PrimitiveType(FuwenPrimitiveKind.Boolean),
        };
        var service = new WorkflowAdmissionService(new WorkflowCompiler(
            WorkflowCompilerTests.Fixture.CreateCatalogue(source),
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));

        var result = await service.AdmitAsync(invalid, cancellationToken: TestContext.Current.CancellationToken);

        result.Compilation.Succeeded.Should().BeFalse();
        result.Succeeded.Should().BeFalse();
        result.Receipt.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void VersionedPolicy_SnapshotsSortsAndValidatesItsAuthorityIdentity()
    {
        var grants = new List<CapabilityRequirement>
        {
            new("zeta", "b"),
            new("alpha"),
        };
        var first = new CapabilityGrantPolicy("policy/1", grants);
        var reordered = new CapabilityGrantPolicy("policy/1", grants.AsEnumerable().Reverse());

        grants[0] = new CapabilityRequirement("changed");

        first.PolicyRevision.Should().Be("policy/1");
        first.GrantSetFingerprint.Should().Be(reordered.GrantSetFingerprint);
        first.IsGranted(new CapabilityRequirement("zeta", "b")).Should().BeTrue();
        first.IsGranted(new CapabilityRequirement("changed")).Should().BeFalse();
        var whitespace = () => new CapabilityGrantPolicy("   ", []);
        whitespace.Should().Throw<ArgumentException>();
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(
        WorkflowPlan plan,
        ITrustedCatalogue catalogue,
        CapabilityGrantPolicy policy) =>
        await new WorkflowAdmissionService(new WorkflowCompiler(catalogue, capabilityPolicy: policy))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

    private sealed class UnversionedCatalogue(ITrustedCatalogue inner) : ITrustedCatalogue
    {
        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(descriptor, cancellationToken);
    }
}
