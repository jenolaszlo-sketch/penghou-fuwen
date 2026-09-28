using System.Text;
using System.Text.Json;
using Penghou.Baize;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Baize;

/// <summary>Executes one normalized Fuwen model turn through a configured Baize endpoint.</summary>
/// <remarks>
/// This adapter binds one exact profile/prompt contract and uses the first configured endpoint.
/// It does not retry or route after submission. Baize usage can be absent and host token prices
/// are post-call estimates, so token and monetary ceilings are not advertised as preflightable.
/// </remarks>
public sealed class BaizeInferenceTurnExecutor : IInferenceTurnExecutor, IInferenceTurnExecutorManifest
{
    private readonly BaizeInferenceBinding binding;
    private readonly BaizeEndpointBinding endpoint;
    private readonly IReadOnlyList<BaizeToolBinding> tools;
    private readonly InferenceFeatureManifest manifest;

    /// <summary>Creates a turn executor for one exact Baize binding, with no hidden endpoint fallback.</summary>
    public BaizeInferenceTurnExecutor(BaizeInferenceBinding binding)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        endpoint = binding.Endpoints[0];
        tools = binding.Tools;
        var workflowOwned = binding.Prompt is not null;
        var supportsNativeTools = endpoint.Client.Capabilities.NativeToolCalling;
        manifest = new InferenceFeatureManifest(
            supportsHardCompletionTokenLimit: false,
            supportedPromptForms: [workflowOwned ? InferencePromptForm.WorkflowOwned : InferencePromptForm.RegisteredTemplate],
            supportedModalities: [InferenceModality.StructuredText],
            // Fuwen delivers context as bounded conversation messages before this seam.
            supportsContextDelivery: true,
            maximumContextPayloadUtf8Bytes: InferenceTurnRequest.MaximumMessages * InferenceConversationMessage.MaximumTextUtf8Bytes,
            supportedToolEffects: supportsNativeTools ? [InferenceToolEffect.ReadOnly] : [],
            supportedLimits:
            [
                // This adapter can be called repeatedly by a bounded workflow; each
                // invocation still submits exactly one request and performs no retry.
                new(InferenceLimitDimension.Turns, 1_000_000),
                new(InferenceLimitDimension.ModelCalls, 1_000_000),
                new(InferenceLimitDimension.ToolCalls, InferenceTurnRequest.MaximumProposals),
                new(InferenceLimitDimension.DurationMilliseconds, 3_600_000),
                new(InferenceLimitDimension.ToolArgumentBytes, InferenceToolCallProposal.MaximumArgumentsUtf8Bytes),
                new(InferenceLimitDimension.RetainedConversationBytes,
                    InferenceTurnRequest.MaximumMessages * InferenceConversationMessage.MaximumTextUtf8Bytes),
            ],
            recoveryQuality: InferenceRecoveryQuality.Unsupported,
            usageQuality: InferenceUsageQuality.Unknown,
            pricingQuality: endpoint.Pricing is null ? InferencePricingQuality.Unknown : InferencePricingQuality.Estimated,
            supportsStructuredOutput: endpoint.Client.Capabilities.NativeStructuredOutput,
            supportsSyntheticStructuredOutput: false,
            profiles: [binding.Profile],
            promptTemplates: binding.PromptTemplate is null ? [] : [binding.PromptTemplate],
            tools: supportsNativeTools ? tools.Select(static tool => tool.Descriptor).ToArray() : [],
            workflowPromptDigests: binding.PromptDigest is null ? [] : [binding.PromptDigest]);
    }

    /// <inheritdoc />
    public InferenceFeatureManifest TurnFeatureManifest => manifest;

    /// <inheritdoc />
    public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        // Evaluate against this complete binding so descriptors from unrelated bindings
        // cannot be combined into an accidentally executable requirement.
        var exactMatch = requirement.Profile == binding.Profile &&
            requirement.PromptTemplate == binding.PromptTemplate &&
            string.Equals(requirement.PromptDigest, binding.PromptDigest, StringComparison.Ordinal);
        var eligibleTools = requirement.ToolRequirements
            .Where(IsExactModelContract)
            .Select(static tool => tool.Descriptor)
            .ToArray();
        var selectedManifest = exactMatch ? WithTools(manifest, eligibleTools) : WithoutBindings(manifest);
        return InferencePreflight.Evaluate(requirement, selectedManifest);
    }

    /// <inheritdoc />
    public async ValueTask<InferenceTurnResult> ExecuteTurnAsync(
        InferenceTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var budgetFailure = ValidateRequestBounds(request);
        if (budgetFailure is not null)
            throw new InferenceTurnFailureException(budgetFailure);

        var visibleBindings = new List<BaizeToolBinding>(request.VisibleTools.Count);
        foreach (var visible in request.VisibleTools)
        {
            var match = tools.FirstOrDefault(candidate => candidate.Descriptor == visible.Descriptor);
            if (match is null || visible.Effect != InferenceToolEffect.ReadOnly || !IsExactModelContract(visible))
                throw new InferenceTurnFailureException(new ExecutionFailure(
                    ExecutionFailureKind.Admission,
                    ExecutionFailureCode.NotAdmitted,
                    "A requested turn tool is not bound as an admitted read-only Baize tool."));
            visibleBindings.Add(match);
        }

        var messages = request.Conversation.Select(message => ToBaizeMessage(message, tools)).ToArray();
        var selectedTools = visibleBindings.Select(static tool => tool.Tool).ToList();
        var tokenLimit = request.MaxCompletionTokens;

        var providerRequest = new LlmRequest(
            messages,
            maxTokens: tokenLimit,
            tools: selectedTools,
            responseFormat: selectedTools.Count == 0 ? LlmResponseFormat.Json() : null,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fuwen.interaction.id"] = request.InteractionId,
                ["fuwen.turn.ordinal"] = request.TurnOrdinal,
            });
        var requirements = LlmRequestRequirements.From(providerRequest);
        if (!requirements.IsSatisfiedBy(endpoint.Client.Capabilities, out _))
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.PolicyRejected,
                "The configured Baize endpoint does not support the requested turn features."));

        LlmResponse response;
        try
        {
            if (request.TimeoutSeconds is int seconds)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    response = await endpoint.Client.CompleteAsync(providerRequest, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InferenceTurnFailureException(new ExecutionFailure(
                        ExecutionFailureKind.Timeout,
                        ExecutionFailureCode.Timeout,
                        "The Baize turn exceeded its configured time limit.",
                        mayHaveCommittedEffect: true));
                }
            }
            else
                response = await endpoint.Client.CompleteAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (InferenceTurnFailureException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmClientException exception)
        {
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError,
                "The Baize provider could not complete the turn.",
                mayHaveCommittedEffect: true,
                providerCode: exception.FailureKind.ToString()));
        }
        catch
        {
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError,
                "The Baize provider could not complete the turn.",
                mayHaveCommittedEffect: true));
        }

        var usage = BaizeOneTurnMapper.MapUsage(response.Usage);
        if (usage.PromptTokens is not null && usage.CompletionTokens is not null && endpoint.Pricing is not null)
            usage = new InferenceTurnUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
                endpoint.Pricing.Calculate(response.Usage));

        if (response.FinishReasonKind == LlmFinishReasonKind.LengthLimit)
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.TruncatedOutput,
                "The Baize response was truncated by its output limit.",
                mayHaveCommittedEffect: true));
        if (response.FinishReasonKind is LlmFinishReasonKind.ContentFilter or LlmFinishReasonKind.Error)
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.SchemaMismatch,
                "The Baize provider did not return an executable turn result.",
                mayHaveCommittedEffect: true));

        if (response.ToolCalls is { Count: > 0 })
        {
            if (response.ReasoningContinuation is not null || response.ContentContinuation is not null ||
                response.ToolCalls.Any(static call => call is not null && call.Continuation is not null))
                throw new InferenceTurnFailureException(new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    "The Baize response requires provider continuation data that the normalized turn contract cannot preserve.",
                    mayHaveCommittedEffect: true));
            var maxBytes = (int)Math.Min(
                request.RemainingLimits.GetMaximum(InferenceLimitDimension.ToolArgumentBytes) ?? InferenceToolCallProposal.MaximumArgumentsUtf8Bytes,
                InferenceToolCallProposal.MaximumArgumentsUtf8Bytes);
            var (proposals, failure) = BaizeOneTurnMapper.MapToolCalls(response.ToolCalls, visibleBindings, request.VisibleTools, maxBytes);
            if (failure is not null || proposals is null)
                throw new InferenceTurnFailureException(AfterSubmission(failure ?? MappingFailure()));
            var maximum = request.MaximumNewToolCalls ??
                (int?)Math.Min(request.RemainingLimits.GetMaximum(InferenceLimitDimension.ToolCalls) ?? int.MaxValue, int.MaxValue);
            if (maximum is int maximumCalls && proposals.Count > maximumCalls)
                throw new InferenceTurnFailureException(new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    "The Baize turn proposed more tool calls than the admitted allowance.",
                    mayHaveCommittedEffect: true));
            if (Encoding.UTF8.GetByteCount(response.Content ?? string.Empty) > InferenceConversationMessage.MaximumTextUtf8Bytes)
                throw new InferenceTurnFailureException(new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.TruncatedOutput,
                    "The Baize turn assistant text exceeds the bounded conversation-message limit.",
                    mayHaveCommittedEffect: true));
            return new InferenceToolCallTurnResult(proposals, usage, response.Content);
        }

        if (string.IsNullOrWhiteSpace(response.Content) || Encoding.UTF8.GetByteCount(response.Content) > InferenceFinalCandidateResult.MaximumCandidateUtf8Bytes)
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                string.IsNullOrWhiteSpace(response.Content) ? ExecutionFailureCode.MalformedOutput : ExecutionFailureCode.TruncatedOutput,
                "The Baize turn did not return a bounded structured candidate.",
                mayHaveCommittedEffect: true));
        try
        {
            using var json = JsonDocument.Parse(response.Content);
        }
        catch (JsonException)
        {
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.MalformedOutput,
                "The Baize turn candidate was not valid JSON.",
                mayHaveCommittedEffect: true));
        }
        return new InferenceFinalCandidateResult(response.Content, usage);
    }

    private ExecutionFailure? ValidateRequestBounds(InferenceTurnRequest request)
    {
        if (request.Conversation.Count > InferenceTurnRequest.MaximumMessages || request.MaximumNewToolCalls is > InferenceTurnRequest.MaximumProposals)
            return ContractFailure(ExecutionFailureCode.PayloadLimitExceeded, "The turn exceeds a supported protocol bound.");

        long conversationBytes = 0;
        foreach (var message in request.Conversation)
        {
            conversationBytes = checked(conversationBytes + Encoding.UTF8.GetByteCount(message.Text));
            conversationBytes = checked(conversationBytes + Encoding.UTF8.GetByteCount(message.ToolCallId ?? string.Empty));
            if (message.ToolCalls is not null)
                foreach (var call in message.ToolCalls)
                    conversationBytes = checked(conversationBytes +
                        Encoding.UTF8.GetByteCount(call.CallId) +
                        Encoding.UTF8.GetByteCount(call.Tool.Name) +
                        Encoding.UTF8.GetByteCount(call.ArgumentsJson));
        }
        if (request.RemainingLimits.GetMaximum(InferenceLimitDimension.RetainedConversationBytes) is long retainedMaximum &&
            conversationBytes > retainedMaximum)
            return ContractFailure(ExecutionFailureCode.PayloadLimitExceeded, $"The turn conversation exceeds its {retainedMaximum}-byte retained limit.");

        if (request.RemainingLimits.GetMaximum(InferenceLimitDimension.Turns) is < 1)
            return ContractFailure(ExecutionFailureCode.TurnLimitExceeded, "No model turn remains in the admitted budget.");
        if (request.RemainingLimits.GetMaximum(InferenceLimitDimension.ModelCalls) is < 1)
            return ContractFailure(ExecutionFailureCode.ModelCallLimitExceeded, "No model call remains in the admitted budget.");
        if (request.RemainingLimits.GetMaximum(InferenceLimitDimension.ToolCalls) is < 1 && request.VisibleTools.Count != 0)
            return ContractFailure(ExecutionFailureCode.ToolCallLimitExceeded, "No model tool call remains in the admitted budget.");

        // The generic Baize contract has no trustworthy pre-call token or cost quote.
        // Only explicitly advisory prompt, total-token, and cost allowances may be
        // observed after submission. Completion-token ceilings remain fail-closed.
        foreach (var dimension in new[]
                 {
                     InferenceLimitDimension.PromptTokens,
                     InferenceLimitDimension.CompletionTokens,
                     InferenceLimitDimension.TotalTokens,
                     InferenceLimitDimension.CostMicrounits,
                 })
        {
            var isAdvisoryMonitor = request.BudgetEnforcement == InferenceBudgetEnforcement.Advisory &&
                dimension is InferenceLimitDimension.PromptTokens or InferenceLimitDimension.TotalTokens or InferenceLimitDimension.CostMicrounits;
            if (request.RemainingLimits.GetMaximum(dimension) is not null && !isAdvisoryMonitor)
                return ContractFailure(ExecutionFailureCode.BudgetUnknown,
                    $"The configured Baize endpoint cannot reserve a trustworthy {dimension} maximum before submission.");
        }
        return null;
    }

    private LlmMessage ToBaizeMessage(InferenceConversationMessage message, IReadOnlyList<BaizeToolBinding> boundTools) => message.Role switch
    {
        InferenceTurnRole.System => LlmMessage.Text("system", message.Text),
        InferenceTurnRole.User => LlmMessage.Text("user", message.Text),
        InferenceTurnRole.Assistant => MapAssistantHistory(message, boundTools),
        InferenceTurnRole.Tool => MapToolResultHistory(message, boundTools),
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };

    private static LlmMessage MapAssistantHistory(InferenceConversationMessage message, IReadOnlyList<BaizeToolBinding> boundTools)
    {
        if (message.ToolCalls is not { Count: > 0 } calls)
            return LlmMessage.Text("assistant", message.Text);
        var mapped = new List<LlmToolCall>(calls.Count);
        foreach (var call in calls)
        {
            var tool = boundTools.FirstOrDefault(candidate => candidate.Descriptor == call.Tool);
            if (tool is null)
                throw new InferenceTurnFailureException(new ExecutionFailure(
                    ExecutionFailureKind.Admission,
                    ExecutionFailureCode.NotAdmitted,
                    "A prior assistant tool call is unavailable from the configured Baize binding."));
            mapped.Add(new LlmToolCall(call.CallId, tool.Tool.Name, call.ArgumentsJson));
        }
        return LlmMessage.Assistant(mapped, message.Text);
    }

    private static LlmMessage MapToolResultHistory(InferenceConversationMessage message, IReadOnlyList<BaizeToolBinding> boundTools)
    {
        var descriptor = message.Tool;
        var tool = descriptor is null ? null : boundTools.FirstOrDefault(candidate => candidate.Descriptor == descriptor);
        if (tool is null)
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.NotAdmitted,
                "A prior tool result is unavailable from the configured Baize binding."));
        return LlmMessage.ToolResult(message.ToolCallId!, tool.Tool.Name, message.Text, succeeded: true);
    }

    private static InferenceFeatureManifest WithoutBindings(InferenceFeatureManifest source) => new(
        supportsHardCompletionTokenLimit: source.SupportsHardCompletionTokenLimit,
        supportedPromptForms: source.SupportedPromptForms,
        supportedModalities: source.SupportedModalities,
        supportsContextDelivery: source.SupportsContextDelivery,
        maximumContextPayloadUtf8Bytes: source.MaximumContextPayloadUtf8Bytes,
        supportedToolEffects: source.SupportedToolEffects,
        supportedLimits: source.SupportedLimits.Limits,
        recoveryQuality: source.RecoveryQuality,
        usageQuality: source.UsageQuality,
        pricingQuality: source.PricingQuality,
        supportsStructuredOutput: source.SupportsStructuredOutput,
        supportsSyntheticStructuredOutput: source.SupportsSyntheticStructuredOutput);

    private bool IsExactModelContract(InferenceToolRequirement requirement)
    {
        if (requirement.ModelContract is not { } contract)
            return false;
        var binding = tools.FirstOrDefault(candidate => candidate.Descriptor == requirement.Descriptor);
        if (binding is null || !string.Equals(binding.Tool.Name, contract.ProviderName, StringComparison.Ordinal))
            return false;
        return string.Equals(CanonicalSchema(binding.Tool.InputSchemaJson), CanonicalSchema(contract.ParametersSchemaJson), StringComparison.Ordinal);
    }

    private static string CanonicalSchema(string value)
    {
        using var document = JsonDocument.Parse(value);
        return Encoding.UTF8.GetString(Penghou.Fuwen.CanonicalJson.Canonicalize(document.RootElement));
    }

    private static InferenceFeatureManifest WithTools(InferenceFeatureManifest source, IReadOnlyList<DescriptorReference> admittedTools) => new(
        supportsHardCompletionTokenLimit: source.SupportsHardCompletionTokenLimit,
        supportedPromptForms: source.SupportedPromptForms,
        supportedModalities: source.SupportedModalities,
        supportsContextDelivery: source.SupportsContextDelivery,
        maximumContextPayloadUtf8Bytes: source.MaximumContextPayloadUtf8Bytes,
        supportedToolEffects: source.SupportedToolEffects,
        supportedLimits: source.SupportedLimits.Limits,
        recoveryQuality: source.RecoveryQuality,
        usageQuality: source.UsageQuality,
        pricingQuality: source.PricingQuality,
        supportsStructuredOutput: source.SupportsStructuredOutput,
        supportsSyntheticStructuredOutput: source.SupportsSyntheticStructuredOutput,
        profiles: source.Profiles,
        promptTemplates: source.PromptTemplates,
        tools: admittedTools,
        workflowPromptDigests: source.WorkflowPromptDigests);

    private static ExecutionFailure ContractFailure(ExecutionFailureCode code, string message) =>
        new(ExecutionFailureKind.Contract, code, message);

    private static ExecutionFailure MappingFailure() => new(
        ExecutionFailureKind.ProviderOutput,
        ExecutionFailureCode.ToolMappingFailure,
        "The Baize turn tool calls did not match their admitted contracts.");

    private static ExecutionFailure AfterSubmission(ExecutionFailure failure) => new(
        failure.Kind,
        failure.Code,
        failure.Message,
        failure.RetryDisposition,
        mayHaveCommittedEffect: true,
        failure.ProviderCode);
}
