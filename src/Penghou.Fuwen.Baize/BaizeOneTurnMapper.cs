using System.Text;
using Penghou.Baize;

namespace Penghou.Fuwen.Baize;

/// <summary>
/// Maps one Baize provider tool-call shape to normalized exact proposals.
/// No provider work occurs; every rejection happens before any tool execution.
/// </summary>
public static class BaizeOneTurnMapper
{
    /// <summary>
    /// Normalizes provider tool calls against declared bindings and visible requirements.
    /// Returns either exact proposals or a stable <see cref="ExecutionFailureCode.ToolMappingFailure"/>.
    /// </summary>
    public static (IReadOnlyList<InferenceToolCallProposal>? Proposals, ExecutionFailure? Failure) MapToolCalls(
        IReadOnlyList<LlmToolCall>? toolCalls,
        IReadOnlyList<BaizeToolBinding> declaredTools,
        IReadOnlyList<InferenceToolRequirement> visibleRequirements,
        int maximumArgumentBytes)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(visibleRequirements);
        if (maximumArgumentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumArgumentBytes));
        if (toolCalls is null || toolCalls.Count == 0)
            return (null, new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.ToolMappingFailure,
                "The provider turn contained no tool call."));
        if (toolCalls.Count > InferenceTurnRequest.MaximumProposals)
            return (null, new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.ToolMappingFailure,
                "The provider turn proposed an unsupported number of tool calls."));

        var byName = new Dictionary<string, BaizeToolBinding>(StringComparer.Ordinal);
        foreach (var binding in declaredTools)
        {
            ArgumentNullException.ThrowIfNull(binding);
            byName.TryAdd(binding.Tool.Name, binding);
        }

        var proposals = new List<InferenceToolCallProposal>(toolCalls.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolCall in toolCalls)
        {
            if (toolCall is null)
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    "The provider turn contained an empty tool call."));
            // Provider shapes are untrusted: missing identities, names, or
            // arguments map to a typed failure, never an unhandled throw.
            if (string.IsNullOrWhiteSpace(toolCall.Id) ||
                string.IsNullOrWhiteSpace(toolCall.Name) ||
                toolCall.ArgumentsJson is null)
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    "The provider turn contained a tool call without a stable identity."));
            // Failure diagnostics must never embed raw provider text: control
            // characters would themselves violate the bounded message contract.
            var safeId = SafeToken(toolCall.Id);
            var safeName = SafeToken(toolCall.Name);
            if (!seenIds.Add(toolCall.Id))
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"The provider turn proposed duplicate tool-call identity '{safeId}'."));
            if (toolCall.NormalizationStatus is LlmToolCallNormalizationStatus.UnknownTool)
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{safeName}' was not mapped to its admitted contract."));
            if (toolCall.NormalizationStatus is LlmToolCallNormalizationStatus.EmptyArguments or LlmToolCallNormalizationStatus.InvalidArguments)
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{safeName}' arguments were not mapped to their admitted contract."));
            if (!byName.TryGetValue(toolCall.Name, out var binding))
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"The provider proposed undeclared tool '{safeName}'."));
            var argumentsJson = toolCall.ArgumentsJson ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(argumentsJson) > maximumArgumentBytes)
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{safeId}' exceeds the {maximumArgumentBytes}-byte argument ceiling."));
            try
            {
                proposals.Add(new InferenceToolCallProposal(toolCall.Id, binding.Descriptor, argumentsJson));
            }
            catch (ArgumentException)
            {
                return (null, new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.ToolMappingFailure,
                    $"Tool call '{safeId}' was not mapped to its admitted contract."));
            }
        }

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visibleRequirements, maximumArgumentBytes);
        return failure is null ? (proposals.AsReadOnly(), null) : (null, failure);
    }

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "?";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsControl(character) ? '?' : character);
        var cleaned = builder.ToString();
        return cleaned.Length > 128 ? cleaned[..128] : cleaned;
    }

    /// <summary>Maps provider usage to normalized turn usage. Missing counts stay unknown, never zero.</summary>
    public static InferenceTurnUsage MapUsage(LlmUsage? usage)
    {
        if (usage is null)
            return new InferenceTurnUsage();
        return new InferenceTurnUsage(
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens);
    }
}
