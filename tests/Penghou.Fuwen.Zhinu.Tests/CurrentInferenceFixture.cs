namespace Penghou.Fuwen.Zhinu.Tests;

internal static class CurrentInferenceFixture
{
    public static InferenceProtocol OneCallProtocol { get; } = new(
        new InferenceProtocolLimits(maxTurns: 1, maxModelCalls: 1));

    public static InferencePreflightReport Accept(InferenceExecutionRequirement requirement) =>
        InferencePreflight.Evaluate(requirement, new InferenceFeatureManifest(
            supportedPromptForms: [requirement.PromptForm],
            supportedModalities: requirement.Modality is null ? [] : [requirement.Modality.Value],
            supportsContextDelivery: requirement.HasContextInputs,
            maximumContextPayloadUtf8Bytes: requirement.MaximumContextPayloadUtf8Bytes
                ?? (requirement.HasContextInputs ? int.MaxValue : null),
            supportedToolEffects: requirement.ToolRequirements.Select(static tool => tool.Effect).Distinct().ToArray(),
            supportedLimits: requirement.Limits.Limits,
            recoveryQuality: requirement.MinimumRecoveryQuality,
            usageQuality: requirement.RequiresExactUsageEvidence
                ? InferenceUsageQuality.Exact
                : InferenceUsageQuality.Unknown,
            pricingQuality: requirement.RequiresExactPricingEvidence
                ? InferencePricingQuality.Exact
                : InferencePricingQuality.Unknown,
            supportsStructuredOutput: requirement.RequiresStructuredOutput,
            profiles: [requirement.Profile],
            promptTemplates: requirement.PromptTemplate is null ? [] : [requirement.PromptTemplate],
            tools: requirement.Tools,
            workflowPromptDigests: requirement.PromptDigest is null ? [] : [requirement.PromptDigest]));

    public static IInferenceExecutor WithPreflight(IInferenceExecutor executor) =>
        executor is IInferenceExecutorManifest or IInferenceExecutorPreflight
            ? executor
            : new PreflightExecutor(executor);

    private sealed class PreflightExecutor(IInferenceExecutor inner) : IInferenceExecutor, IInferenceExecutorManifest
    {
        private static readonly InferenceFeatureManifest EmptyManifest = new(
            supportedPromptForms: [],
            supportedModalities: [],
            supportsContextDelivery: false,
            maximumContextPayloadUtf8Bytes: null,
            supportedToolEffects: [],
            supportedLimits: [],
            recoveryQuality: InferenceRecoveryQuality.Unsupported,
            usageQuality: InferenceUsageQuality.Unknown,
            pricingQuality: InferencePricingQuality.Unknown);

        public InferenceFeatureManifest FeatureManifest => EmptyManifest;

        public InferencePreflightReport PreflightDetailed(InferenceExecutionRequirement requirement) => Accept(requirement);

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) => inner.ExecuteAsync(request, cancellationToken);
    }
}
