using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

/// <summary>
/// CI-1 evidence for the provider-neutral admission seam. The fake deliberately
/// has no provider behavior: its execution counter is the proof that rejected
/// requirements are stopped before any execution work can begin.
/// </summary>
public sealed class DeterministicInferencePreflightMatrixTests
{
    [Theory]
    [InlineData("supported")]
    [InlineData("prompt-form")]
    [InlineData("modality")]
    [InlineData("context-ceiling")]
    [InlineData("tool-effect")]
    [InlineData("tool-binding")]
    [InlineData("limit-exceeds")]
    [InlineData("limit-unsupported")]
    [InlineData("recovery")]
    [InlineData("usage")]
    [InlineData("pricing")]
    [InlineData("profile-binding")]
    [InlineData("prompt-binding")]
    [InlineData("workflow-prompt-binding")]
    public async Task Preflight_matrix_is_deterministic_and_rejections_do_zero_execution_work(string caseName)
    {
        var testCase = CreateCase(caseName);
        var executor = new DeterministicFakeExecutor(testCase.Manifest);

        var result = await executor.ExecuteAdmittedAsync(testCase.Requirement);

        executor.LastReport.Should().NotBeNull();
        var report = executor.LastReport!;
        report.IsExecutable.Should().Be(testCase.ExpectedCode is null);
        if (testCase.ExpectedCode is null)
        {
            result.IsSuccess.Should().BeTrue();
            executor.ExecutionCalls.Should().Be(1);
            report.Diagnostics.Should().BeEmpty();
        }
        else
        {
            result.IsSuccess.Should().BeFalse();
            report.Diagnostics.Select(diagnostic => diagnostic.Code)
                .Should().Contain(testCase.ExpectedCode.Value);
            executor.ExecutionCalls.Should().Be(0);
        }

        executor.PreflightCalls.Should().Be(1);
        executor.Preflight(testCase.Requirement)?.Code
            .Should().Be(testCase.ExpectedCode is null ? null : ExecutionFailureCode.NotAdmitted);
        executor.PreflightCalls.Should().Be(2);
        executor.ExecutionCalls.Should().Be(testCase.ExpectedCode is null ? 1 : 0);
    }

    [Fact]
    public async Task Supported_matrix_case_can_execute_only_after_exact_admission()
    {
        var testCase = CreateCase("supported");
        var executor = new DeterministicFakeExecutor(testCase.Manifest);

        var result = await executor.ExecuteAdmittedAsync(testCase.Requirement);

        result.IsSuccess.Should().BeTrue();
        executor.LastRequest.Should().NotBeNull();
        executor.LastRequest!.Profile.Should().Be(testCase.Requirement.Profile);
        executor.LastRequest.PromptTemplate.Should().Be(testCase.Requirement.PromptTemplate);
    }

    private static MatrixCase CreateCase(string name)
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var otherProfile = Descriptor(DescriptorKind.InferenceProfile, "other-chat");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "answer");
        var otherPrompt = Descriptor(DescriptorKind.PromptTemplate, "other-answer");
        var tool = Descriptor(DescriptorKind.Tool, "lookup");
        var otherTool = Descriptor(DescriptorKind.Tool, "other-lookup");

        var requirement = new InferenceExecutionRequirement(
            profile,
            prompt,
            promptDigest: null,
            tools: [tool],
            hasContextInputs: false,
            modality: InferenceModality.StructuredText,
            toolRequirements: [new InferenceToolRequirement(tool)],
            limits: new InferenceLimitSet([new(InferenceLimitDimension.ModelCalls, 2)]),
            requiresStructuredOutput: true);

        var manifest = Manifest(profile, prompt, tool);
        InferencePreflightDiagnosticCode? expected = null;

        switch (name)
        {
            case "supported":
                break;
            case "prompt-form":
                manifest = Manifest(profile, prompt, tool, supportedPromptForms: [InferencePromptForm.WorkflowOwned]);
                expected = InferencePreflightDiagnosticCode.UnsupportedPromptForm;
                break;
            case "modality":
                requirement = Requirement(profile, prompt, tool, modality: InferenceModality.Image);
                expected = InferencePreflightDiagnosticCode.UnsupportedModality;
                break;
            case "context-ceiling":
                requirement = Requirement(profile, prompt, tool, hasContextInputs: true, maximumContextPayloadUtf8Bytes: 65);
                expected = InferencePreflightDiagnosticCode.ContextPayloadTooLarge;
                break;
            case "tool-effect":
                requirement = Requirement(profile, prompt, tool,
                    toolEffect: InferenceToolEffect.IdempotentWrite);
                expected = InferencePreflightDiagnosticCode.UnsupportedToolEffect;
                break;
            case "tool-binding":
                requirement = Requirement(profile, prompt, tool);
                manifest = Manifest(profile, prompt, otherTool);
                expected = InferencePreflightDiagnosticCode.MissingToolBinding;
                break;
            case "limit-exceeds":
                requirement = Requirement(profile, prompt, tool,
                    limits: new InferenceLimitSet([new(InferenceLimitDimension.ModelCalls, 5)]));
                expected = InferencePreflightDiagnosticCode.LimitExceedsHostMaximum;
                break;
            case "limit-unsupported":
                requirement = Requirement(profile, prompt, tool,
                    limits: new InferenceLimitSet([new(InferenceLimitDimension.CostMicrounits, 1)]));
                expected = InferencePreflightDiagnosticCode.UnsupportedLimit;
                break;
            case "recovery":
                requirement = Requirement(profile, prompt, tool,
                    minimumRecoveryQuality: InferenceRecoveryQuality.Resumable);
                expected = InferencePreflightDiagnosticCode.RecoveryUnsupported;
                break;
            case "usage":
                requirement = Requirement(profile, prompt, tool,
                    requiresExactUsageEvidence: true);
                expected = InferencePreflightDiagnosticCode.UsageEvidenceUnavailable;
                break;
            case "pricing":
                requirement = Requirement(profile, prompt, tool,
                    requiresExactPricingEvidence: true);
                expected = InferencePreflightDiagnosticCode.PricingEvidenceUnavailable;
                break;
            case "profile-binding":
                manifest = Manifest(otherProfile, prompt, tool);
                expected = InferencePreflightDiagnosticCode.MissingProfileBinding;
                break;
            case "prompt-binding":
                manifest = Manifest(profile, otherPrompt, tool);
                expected = InferencePreflightDiagnosticCode.MissingPromptTemplateBinding;
                break;
            case "workflow-prompt-binding":
                requirement = new InferenceExecutionRequirement(
                    profile, promptTemplate: null, promptDigest: "prompt-digest",
                    tools: [tool], hasContextInputs: false, modality: InferenceModality.StructuredText,
                    toolRequirements: [new InferenceToolRequirement(tool)]);
                manifest = Manifest(profile, prompt, tool,
                    supportedPromptForms: [InferencePromptForm.WorkflowOwned]);
                expected = InferencePreflightDiagnosticCode.MissingWorkflowPromptBinding;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown preflight matrix case.");
        }

        return new MatrixCase(requirement, manifest, expected);
    }

    private static InferenceExecutionRequirement Requirement(
        DescriptorReference profile,
        DescriptorReference prompt,
        DescriptorReference tool,
        bool hasContextInputs = false,
        int? maximumContextPayloadUtf8Bytes = null,
        InferenceModality modality = InferenceModality.StructuredText,
        InferenceToolEffect toolEffect = InferenceToolEffect.ReadOnly,
        InferenceLimitSet? limits = null,
        InferenceRecoveryQuality minimumRecoveryQuality = InferenceRecoveryQuality.Unsupported,
        bool requiresExactUsageEvidence = false,
        bool requiresExactPricingEvidence = false) => new(
            profile, prompt, promptDigest: null, tools: [tool], hasContextInputs: hasContextInputs, modality: modality,
            toolRequirements: [new InferenceToolRequirement(tool, toolEffect)],
            limits: limits ?? new InferenceLimitSet([new(InferenceLimitDimension.ModelCalls, 2)]),
            minimumRecoveryQuality: minimumRecoveryQuality,
            maximumContextPayloadUtf8Bytes: maximumContextPayloadUtf8Bytes,
            requiresExactUsageEvidence: requiresExactUsageEvidence,
            requiresExactPricingEvidence: requiresExactPricingEvidence,
            requiresStructuredOutput: true);

    private static InferenceFeatureManifest Manifest(
        DescriptorReference profile,
        DescriptorReference prompt,
        DescriptorReference tool,
        IReadOnlyList<InferencePromptForm>? supportedPromptForms = null) => new(
            supportedPromptForms ?? [InferencePromptForm.RegisteredTemplate],
            [InferenceModality.StructuredText],
            supportsContextDelivery: true,
            maximumContextPayloadUtf8Bytes: 64,
            supportedToolEffects: [InferenceToolEffect.ReadOnly],
            supportedLimits: [new(InferenceLimitDimension.ModelCalls, 4)],
            recoveryQuality: InferenceRecoveryQuality.Unsupported,
            usageQuality: InferenceUsageQuality.Unknown,
            pricingQuality: InferencePricingQuality.Unknown,
            profiles: [profile],
            promptTemplates: [prompt],
            tools: [tool]);

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    private sealed record MatrixCase(
        InferenceExecutionRequirement Requirement,
        InferenceFeatureManifest Manifest,
        InferencePreflightDiagnosticCode? ExpectedCode);

    private sealed class DeterministicFakeExecutor : IInferenceExecutor, IInferenceExecutorManifest, IInferenceExecutorPreflight
    {
        public DeterministicFakeExecutor(InferenceFeatureManifest featureManifest) => FeatureManifest = featureManifest;

        public InferenceFeatureManifest FeatureManifest { get; }
        public int PreflightCalls { get; private set; }
        public int ExecutionCalls { get; private set; }
        public InferencePreflightReport? LastReport { get; private set; }
        public InferenceExecutionRequest? LastRequest { get; private set; }

        public InferencePreflightReport PreflightDetailed(InferenceExecutionRequirement requirement)
        {
            PreflightCalls++;
            return LastReport = FeatureManifest.Preflight(requirement);
        }

        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement) =>
            PreflightDetailed(requirement).Failure;

        public async ValueTask<InferenceExecutionResult> ExecuteAdmittedAsync(InferenceExecutionRequirement requirement)
        {
            var report = PreflightDetailed(requirement);
            if (!report.IsExecutable)
                return InferenceExecutionResult.Failed(report.Failure!);

            return await ExecuteAsync(CreateRequest(requirement));
        }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCalls++;
            LastRequest = request;
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement("deterministic"))));
        }

        private static InferenceExecutionRequest CreateRequest(InferenceExecutionRequirement requirement) =>
            new(
                new ExecutionInvocation(
                    $"sha256:fuwen-execution/v1:{new string('a', 64)}",
                    "ci1/inference",
                    "ci1/inference",
                    "1",
                    $"sha256:fuwen-request/v1:{new string('b', 64)}"),
                requirement.Profile,
                requirement.PromptTemplate,
                [],
                [],
                new PrimitiveType(FuwenPrimitiveKind.String),
                tools: requirement.Tools);
    }
}
