using System.Text;

namespace Penghou.Fuwen;

/// <summary>Role of one normalized inference conversation message.</summary>
public enum InferenceTurnRole
{
    /// <summary>Host-provided system instruction.</summary>
    System,
    /// <summary>Rendered workflow prompt or prior user content.</summary>
    User,
    /// <summary>Prior model content.</summary>
    Assistant,
    /// <summary>Prior tool result content.</summary>
    Tool,
}

/// <summary>One bounded, normalized inference conversation message.</summary>
public sealed class InferenceConversationMessage
{
    /// <summary>Maximum UTF-8 bytes in one message text.</summary>
    public const int MaximumTextUtf8Bytes = 65_536;
    /// <summary>Maximum UTF-8 bytes in a tool-call identity reference.</summary>
    public const int MaximumToolCallIdUtf8Bytes = 256;

    /// <summary>Creates a detached conversation message.</summary>
    public InferenceConversationMessage(InferenceTurnRole role, string text, string? toolCallId = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        Text = RuntimeValueSnapshot.Text(text, nameof(text), MaximumTextUtf8Bytes);
        if (role == InferenceTurnRole.Tool && string.IsNullOrWhiteSpace(toolCallId))
            throw new ArgumentException("A tool message requires the originating tool-call identity.", nameof(toolCallId));
        if (role != InferenceTurnRole.Tool && toolCallId is not null)
            throw new ArgumentException("Only a tool message may carry a tool-call identity.", nameof(toolCallId));
        ToolCallId = RuntimeValueSnapshot.OptionalText(toolCallId, nameof(toolCallId), MaximumToolCallIdUtf8Bytes);
        Role = role;
    }

    /// <summary>The message role.</summary>
    public InferenceTurnRole Role { get; }
    /// <summary>The bounded message text.</summary>
    public string Text { get; }
    /// <summary>The originating tool-call identity, or null for non-tool messages.</summary>
    public string? ToolCallId { get; }
}

/// <summary>One exact model-proposed tool call awaiting host validation and execution.</summary>
public sealed class InferenceToolCallProposal
{
    /// <summary>Maximum UTF-8 bytes in a model-provided tool-call identity.</summary>
    public const int MaximumCallIdUtf8Bytes = 256;
    /// <summary>Maximum UTF-8 bytes in model-provided tool arguments.</summary>
    public const int MaximumArgumentsUtf8Bytes = 65_536;

    /// <summary>Creates a detached tool-call proposal.</summary>
    public InferenceToolCallProposal(string callId, DescriptorReference tool, string argumentsJson)
    {
        CallId = RuntimeValueSnapshot.Text(callId, nameof(callId), MaximumCallIdUtf8Bytes);
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        ArgumentsJson = RuntimeValueSnapshot.Text(argumentsJson, nameof(argumentsJson), MaximumArgumentsUtf8Bytes);
    }

    /// <summary>The stable model-provided call identity within its turn.</summary>
    public string CallId { get; }
    /// <summary>The exact proposed tool descriptor.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The bounded model-provided arguments JSON.</summary>
    public string ArgumentsJson { get; }
}

/// <summary>Normalized per-turn usage and cost evidence. Null members are unknown, never zero.</summary>
public sealed class InferenceTurnUsage
{
    /// <summary>Creates detached normalized turn usage.</summary>
    public InferenceTurnUsage(
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        InferenceCostEvidence? cost = null)
    {
        if (promptTokens is < 0 || completionTokens is < 0 || totalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(totalTokens), "Turn token usage cannot be negative.");
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        Cost = cost is null ? null : new InferenceCostEvidence(
            cost.CurrencyCode, cost.AmountMicrounits, cost.IsEstimated, cost.PricingRevision);
    }

    /// <summary>Prompt tokens reported for this turn, or null when unknown.</summary>
    public int? PromptTokens { get; }
    /// <summary>Completion tokens reported for this turn, or null when unknown.</summary>
    public int? CompletionTokens { get; }
    /// <summary>Total tokens reported for this turn, or null when unknown.</summary>
    public int? TotalTokens { get; }
    /// <summary>Cost attributed to this turn, or null when unknown.</summary>
    public InferenceCostEvidence? Cost { get; }
}

/// <summary>Request sent to a provider-neutral one-turn inference executor.</summary>
public sealed class InferenceTurnRequest
{
    /// <summary>Maximum conversation messages accepted by one turn.</summary>
    public const int MaximumMessages = 64;
    /// <summary>Maximum tool-call proposals the model may return in one turn.</summary>
    public const int MaximumProposals = 16;
    /// <summary>Maximum UTF-8 bytes in a stable interaction identity.</summary>
    public const int MaximumInteractionIdUtf8Bytes = 256;

    /// <summary>Creates a detached one-turn inference request.</summary>
    public InferenceTurnRequest(
        string interactionId,
        int turnOrdinal,
        IReadOnlyList<InferenceConversationMessage> conversation,
        IReadOnlyList<InferenceToolRequirement> visibleTools,
        InferenceLimitSet? remainingLimits = null,
        int? maximumNewToolCalls = null,
        int? maxCompletionTokens = null,
        int? timeoutSeconds = null)
    {
        InteractionId = RuntimeValueSnapshot.Text(interactionId, nameof(interactionId), MaximumInteractionIdUtf8Bytes);
        if (turnOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(turnOrdinal), "Turn ordinals cannot be negative.");
        TurnOrdinal = turnOrdinal;
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Count == 0 || conversation.Count > MaximumMessages)
            throw new ArgumentOutOfRangeException(nameof(conversation), $"A turn carries 1 to {MaximumMessages} conversation messages.");
        Conversation = Array.AsReadOnly(conversation.Select(static message =>
            new InferenceConversationMessage(
                (message ?? throw new ArgumentException("Conversation messages cannot contain null values.", nameof(conversation))).Role,
                message.Text,
                message.ToolCallId)).ToArray());
        ArgumentNullException.ThrowIfNull(visibleTools);
        var tools = visibleTools.Select(static tool =>
            new InferenceToolRequirement(
                (tool ?? throw new ArgumentException("Visible tools cannot contain null values.", nameof(visibleTools))).Descriptor,
                tool.Effect)).ToArray();
        if (tools.Select(static tool => tool.Descriptor).Distinct().Count() != tools.Length)
            throw new ArgumentException("Visible tools must be unique.", nameof(visibleTools));
        VisibleTools = Array.AsReadOnly(tools);
        RemainingLimits = remainingLimits ?? new InferenceLimitSet();
        if (maximumNewToolCalls is <= 0 || maximumNewToolCalls > MaximumProposals)
            throw new ArgumentOutOfRangeException(nameof(maximumNewToolCalls));
        MaximumNewToolCalls = maximumNewToolCalls;
        if (maxCompletionTokens is <= 0 || maxCompletionTokens > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens), "Per-call completion bound must be between 1 and 1000000.");
        MaxCompletionTokens = maxCompletionTokens;
        if (timeoutSeconds is <= 0 || timeoutSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Per-call timeout must be between 1 and 3600 seconds.");
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The stable logical-activity interaction identity for this turn.</summary>
    public string InteractionId { get; }
    /// <summary>The zero-based turn ordinal within the interaction.</summary>
    public int TurnOrdinal { get; }
    /// <summary>The bounded normalized conversation visible to the model.</summary>
    public IReadOnlyList<InferenceConversationMessage> Conversation { get; }
    /// <summary>The exact model-visible tool requirements, no broader than the admitted plan.</summary>
    public IReadOnlyList<InferenceToolRequirement> VisibleTools { get; }
    /// <summary>The remaining aggregate bounds the turn must respect.</summary>
    public InferenceLimitSet RemainingLimits { get; }
    /// <summary>Maximum tool-call proposals accepted from this turn, when bounded below the default.</summary>
    public int? MaximumNewToolCalls { get; }
    /// <summary>Maximum completion tokens for this turn, when the plan declares a per-call bound.</summary>
    public int? MaxCompletionTokens { get; }
    /// <summary>Wall-clock ceiling in seconds for this turn, when the plan declares a per-call timeout.</summary>
    public int? TimeoutSeconds { get; }
}

/// <summary>Base contract for a normalized one-turn inference outcome.</summary>
public abstract class InferenceTurnResult
{
    private protected InferenceTurnResult(InferenceTurnUsage? usage)
    {
        Usage = usage is null ? null : new InferenceTurnUsage(
            usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.Cost);
    }

    /// <summary>Whether the model proposed tool calls.</summary>
    public abstract bool IsToolCall { get; }
    /// <summary>Normalized usage for this turn, or null when entirely unknown.</summary>
    public InferenceTurnUsage? Usage { get; }
}

/// <summary>A normalized final model candidate awaiting typed validation.</summary>
public sealed class InferenceFinalCandidateResult : InferenceTurnResult
{
    /// <summary>Maximum UTF-8 bytes in a final candidate payload.</summary>
    public const int MaximumCandidateUtf8Bytes = 262_144;

    /// <summary>Creates a detached final-candidate result.</summary>
    public InferenceFinalCandidateResult(string candidateJson, InferenceTurnUsage? usage = null)
        : base(usage)
    {
        CandidateJson = RuntimeValueSnapshot.Text(candidateJson, nameof(candidateJson), MaximumCandidateUtf8Bytes);
    }

    /// <inheritdoc />
    public override bool IsToolCall => false;
    /// <summary>The bounded candidate JSON text.</summary>
    public string CandidateJson { get; }
}

/// <summary>Normalized exact model-proposed tool calls awaiting host validation.</summary>
public sealed class InferenceToolCallTurnResult : InferenceTurnResult
{
    /// <summary>Creates a detached tool-call result.</summary>
    public InferenceToolCallTurnResult(
        IReadOnlyList<InferenceToolCallProposal> proposals,
        InferenceTurnUsage? usage = null)
        : base(usage)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count == 0 || proposals.Count > InferenceTurnRequest.MaximumProposals)
            throw new ArgumentOutOfRangeException(nameof(proposals));
        Proposals = Array.AsReadOnly(proposals.Select(static proposal =>
            new InferenceToolCallProposal(
                (proposal ?? throw new ArgumentException("Proposals cannot contain null values.", nameof(proposals))).CallId,
                proposal.Tool,
                proposal.ArgumentsJson)).ToArray());
    }

    /// <inheritdoc />
    public override bool IsToolCall => true;
    /// <summary>The detached tool-call proposals in model order.</summary>
    public IReadOnlyList<InferenceToolCallProposal> Proposals { get; }
}

/// <summary>Executes one bounded, normalized model turn without durable coordination.</summary>
public interface IInferenceTurnExecutor
{
    /// <summary>Executes one turn without embedding retry, authorization, or scheduling policy.</summary>
    ValueTask<InferenceTurnResult> ExecuteTurnAsync(InferenceTurnRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Optional provider-neutral feature manifest and structured preflight for a turn executor.</summary>
public interface IInferenceTurnExecutorManifest
{
    /// <summary>The immutable features and exact bindings advertised by this turn executor.</summary>
    InferenceFeatureManifest TurnFeatureManifest { get; }

    /// <summary>Returns a structured, provider-neutral report without starting provider work.</summary>
    InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement);
}

/// <summary>Validates exact model-proposed tool calls before any tool execution.</summary>
public static class InferenceTurnValidation
{
    /// <summary>
    /// Rejects undeclared, write-capable, duplicate, oversized, or mistyped proposals.
    /// Returns null when every proposal is executable; otherwise returns a stable
    /// <see cref="ExecutionFailureCode.ToolMappingFailure"/> failure. No tool work occurs.
    /// </summary>
    public static ExecutionFailure? ValidateProposals(
        IReadOnlyList<InferenceToolCallProposal> proposals,
        IReadOnlyList<InferenceToolRequirement> visibleTools,
        int maximumArgumentBytes)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(visibleTools);
        if (maximumArgumentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumArgumentBytes));
        if (proposals.Count == 0 || proposals.Count > InferenceTurnRequest.MaximumProposals)
            return new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.ToolMappingFailure,
                "The model turn proposed an unsupported number of tool calls.");

        var admitted = new HashSet<DescriptorReference>(visibleTools.Select(static tool =>
            (tool ?? throw new ArgumentException("Visible tools cannot contain null values.", nameof(visibleTools))).Descriptor));
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            ArgumentNullException.ThrowIfNull(proposal);
            if (!seenIds.Add(proposal.CallId))
                return new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"The model turn proposed duplicate tool-call identity '{proposal.CallId}'.");
            var requirement = visibleTools.FirstOrDefault(candidate => candidate.Descriptor == proposal.Tool);
            if (requirement is null || !admitted.Contains(proposal.Tool))
                return new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"The model proposed undeclared tool '{proposal.Tool.Name}@{proposal.Tool.Version}'.");
            if (requirement.Effect != InferenceToolEffect.ReadOnly)
                return new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"The model proposed tool '{proposal.Tool.Name}@{proposal.Tool.Version}' with unsupported effect '{requirement.Effect}'.");
            if (Encoding.UTF8.GetByteCount(proposal.ArgumentsJson) > maximumArgumentBytes)
                return new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{proposal.CallId}' exceeds the {maximumArgumentBytes}-byte argument ceiling.");
            if (!IsJsonObject(proposal.ArgumentsJson))
                return new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{proposal.CallId}' arguments are not a JSON object.");
        }

        return null;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
