using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class WorkflowExplanationTests
{
    [Fact]
    public void FromCompilation_ExposesDetachedPlanPinsCapabilitiesAndLimits()
    {
        var capability = new CapabilityRequirement("read", "workspace");
        var plan = WorkflowCompilerTests.Fixture.CreatePlan() with
        {
            CapabilityManifest = new CapabilityManifest([capability]),
        };
        var catalogue = WorkflowCompilerTests.Fixture.CreateCatalogue(plan, capability);
        var result = new WorkflowCompiler(
                catalogue,
                capabilityPolicy: new CapabilityGrantPolicy([capability]))
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        var explanation = WorkflowExplanation.Create(result);

        explanation.Compilation.Should().BeSameAs(result);
        explanation.CompilationSucceeded.Should().BeTrue();
        explanation.AdmissionEvaluated.Should().BeFalse();
        explanation.AdmissionSucceeded.Should().BeFalse();
        explanation.ExecutionFingerprint.Should().Be(result.Definition!.ExecutionFingerprint);
        explanation.Plan.Should().NotBeNull();
        explanation.ResolvedDescriptorPins.Should().Equal(
            explanation.ResolvedDescriptorPins
                .OrderBy(static descriptor => descriptor.Kind)
                .ThenBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.Version, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Algorithm, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Contract, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Value, StringComparer.Ordinal));
        explanation.RequiredCapabilities.Should().ContainSingle().Which.Should().Be(capability);
        explanation.EffectiveBudget.Should().BeSameAs(result.Budget);
        explanation.Usage.Should().BeSameAs(result.Usage);
        explanation.Diagnostics.Should().BeSameAs(result.Diagnostics);
    }

    [Fact]
    public void FromCompilation_FailedCompilationHasNoExecutablePlanOrFingerprint()
    {
        var source = WorkflowCompilerTests.Fixture.CreatePlan();
        var invalid = source with
        {
            OutputType = new PrimitiveType(FuwenPrimitiveKind.Boolean),
        };
        var result = new WorkflowCompiler(WorkflowCompilerTests.Fixture.CreateCatalogue(source))
            .Compile(invalid, cancellationToken: TestContext.Current.CancellationToken);

        var explanation = WorkflowExplanation.Create(result);

        explanation.CompilationSucceeded.Should().BeFalse();
        explanation.AdmissionEvaluated.Should().BeFalse();
        explanation.AdmissionSucceeded.Should().BeFalse();
        explanation.ExecutionFingerprint.Should().BeNull();
        explanation.Plan.Should().BeNull();
        explanation.ResolvedDescriptorPins.Should().BeEmpty();
        explanation.RequiredCapabilities.Should().BeEmpty();
        explanation.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task FromAdmission_DistinguishesCompilationFromHostAdmissionWithoutExposingReceipt()
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var policy = new CapabilityGrantPolicy("policy/1", []);
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                WorkflowCompilerTests.Fixture.CreateCatalogue(plan),
                capabilityPolicy: policy))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        var explanation = WorkflowExplanation.Create(admission);

        admission.Succeeded.Should().BeTrue();
        explanation.CompilationSucceeded.Should().BeTrue();
        explanation.AdmissionEvaluated.Should().BeTrue();
        explanation.AdmissionSucceeded.Should().BeTrue();
        explanation.Diagnostics.Should().BeEmpty();
        typeof(WorkflowExplanation).GetProperty(nameof(WorkflowAdmissionResult.Receipt)).Should().BeNull();
    }

    [Fact]
    public async Task FromAdmission_ReportsAdmissionRejectionSeparately()
    {
        var plan = WorkflowCompilerTests.Fixture.CreatePlan();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                WorkflowCompilerTests.Fixture.CreateCatalogue(plan),
                capabilityPolicy: CapabilityGrantPolicy.AllowAll))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        var explanation = WorkflowExplanation.Create(admission);

        admission.Compilation.Succeeded.Should().BeTrue();
        admission.Succeeded.Should().BeFalse();
        explanation.CompilationSucceeded.Should().BeTrue();
        explanation.AdmissionEvaluated.Should().BeTrue();
        explanation.AdmissionSucceeded.Should().BeFalse();
        explanation.Diagnostics.Should().ContainSingle(d =>
            d.Code == CompilerDiagnosticCodes.AdmissionPolicyRevisionUnavailable);
    }

    [Fact]
    public void FromCompilation_DefensivelySnapshotsPlanAccessAndKeepsCollectionsDeterministic()
    {
        var capability = new CapabilityRequirement("read", "workspace");
        var plan = WorkflowCompilerTests.Fixture.CreatePlan() with
        {
            CapabilityManifest = new CapabilityManifest([capability]),
        };
        var result = new WorkflowCompiler(
                WorkflowCompilerTests.Fixture.CreateCatalogue(plan, capability),
                capabilityPolicy: new CapabilityGrantPolicy([capability]))
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);
        var first = WorkflowExplanation.Create(result);
        var second = WorkflowExplanation.Create(result);

        first.ResolvedDescriptorPins.Should().Equal(second.ResolvedDescriptorPins);
        first.RequiredCapabilities.Should().Equal(second.RequiredCapabilities);
        first.Plan.Should().NotBeSameAs(second.Plan);

        var firstPlan = first.Plan!;
        var nodes = (IList<WorkflowNode>)firstPlan.Nodes;
        var originalNode = nodes[0];
        nodes[0] = new ReturnNode("mutated", "answer.mutated", new InputBinding([]));
        first.Plan!.Nodes[0].Should().BeEquivalentTo(originalNode);

        var addCapability = () => ((IList<CapabilityRequirement>)first.RequiredCapabilities)
            .Add(new CapabilityRequirement("mutated"));
        addCapability.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_RejectsNullResults()
    {
        Action compilation = () => WorkflowExplanation.Create((CompilationResult)null!);
        Action admission = () => WorkflowExplanation.Create((WorkflowAdmissionResult)null!);

        compilation.Should().Throw<ArgumentNullException>();
        admission.Should().Throw<ArgumentNullException>();
    }
}
