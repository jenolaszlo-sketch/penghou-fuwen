using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>The kind of one internal operation in the inference protocol.</summary>
public enum InferenceOperationKind
{
    /// <summary>One bounded model turn.</summary>
    ModelTurn,
    /// <summary>One exact read-tool execution.</summary>
    ToolCall,
    /// <summary>One final output representation validation.</summary>
    Validation,
}

/// <summary>The durable disposition of one internal inference operation.</summary>
public enum InferenceOperationDisposition
{
    /// <summary>The operation completed and its result was committed.</summary>
    Succeeded,
    /// <summary>The operation failed terminally.</summary>
    Failed,
    /// <summary>A previously committed operation was reused on replay.</summary>
    Reused,
    /// <summary>The operation may have committed remotely but the outcome is unknown.</summary>
    Ambiguous,
}

/// <summary>How the coordinator reached its terminal outcome relative to prior work.</summary>
public enum InferenceRecoveryDisposition
{
    /// <summary>The logical activity ran without reusing prior operations.</summary>
    Fresh,
    /// <summary>At least one completed operation was reused from durable state.</summary>
    Replayed,
    /// <summary>An ambiguous operation was reconciled through provider/host evidence.</summary>
    Reconciled,
    /// <summary>An operation may have committed and requires operator action.</summary>
    Ambiguous,
}

/// <summary>
/// A host-controlled reference to a sensitive payload held outside ordinary
/// workflow evidence. It is an identity, not an implicit dereference; the host
/// owns storage, access control, retention, and byte verification.
/// </summary>
public sealed class ProtectedPayloadReference
{
    /// <summary>Maximum UTF-8 bytes in a provider or storage identity.</summary>
    public const int MaximumIdentityUtf8Bytes = 256;
    /// <summary>Maximum UTF-8 bytes in an opaque payload identity.</summary>
    public const int MaximumPayloadIdUtf8Bytes = 512;

    /// <summary>Creates a detached protected-payload reference.</summary>
    public ProtectedPayloadReference(
        string provider,
        string payloadId,
        ContentDigest digest,
        DescriptorReference? descriptor = null,
        long? byteLength = null,
        string? retentionPolicyRevision = null,
        string? storageIdentity = null)
    {
        Provider = RuntimeValueSnapshot.Text(provider, nameof(provider), MaximumIdentityUtf8Bytes);
        PayloadId = RuntimeValueSnapshot.Text(payloadId, nameof(payloadId), MaximumPayloadIdUtf8Bytes);
        Digest = RuntimeValueSnapshot.CloneDigest(digest, nameof(digest));
        if (descriptor is not null && !Enum.IsDefined(descriptor.Kind))
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        Descriptor = descriptor is null ? null : RuntimeValueSnapshot.CloneDescriptor(descriptor);
        if (byteLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        ByteLength = byteLength;
        RetentionPolicyRevision = RuntimeValueSnapshot.OptionalText(
            retentionPolicyRevision, nameof(retentionPolicyRevision), MaximumIdentityUtf8Bytes);
        StorageIdentity = RuntimeValueSnapshot.OptionalText(
            storageIdentity, nameof(storageIdentity), MaximumIdentityUtf8Bytes);
    }

    /// <summary>The host/provider that holds the payload.</summary>
    public string Provider { get; }
    /// <summary>An opaque host-assigned payload identity.</summary>
    public string PayloadId { get; }
    /// <summary>The verified or claimed content digest of the payload.</summary>
    public ContentDigest Digest { get; }
    /// <summary>The optional descriptor of the payload's admitted type.</summary>
    public DescriptorReference? Descriptor { get; }
    /// <summary>The payload length in bytes, when known.</summary>
    public long? ByteLength { get; }
    /// <summary>The host retention-policy revision governing the payload, when known.</summary>
    public string? RetentionPolicyRevision { get; }
    /// <summary>The opaque storage identity, when different from the provider/payload pair.</summary>
    public string? StorageIdentity { get; }
}

/// <summary>A bounded summary of one internal inference operation.</summary>
public sealed class InferenceOperationEvidence
{
    /// <summary>Creates a detached operation summary.</summary>
    public InferenceOperationEvidence(
        int ordinal,
        InferenceOperationKind kind,
        string operationId,
        InferenceOperationDisposition disposition,
        bool mayHaveCommittedEffect = false,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        InferenceCostEvidence? cost = null,
        InferenceUsageQuality usageQuality = InferenceUsageQuality.Unknown,
        InferencePricingQuality pricingQuality = InferencePricingQuality.Unknown,
        long? durationMilliseconds = null,
        string? failureCode = null)
    {
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        if (promptTokens is < 0 || completionTokens is < 0 || totalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(totalTokens), "Operation token usage cannot be negative.");
        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        if (!Enum.IsDefined(usageQuality))
            throw new ArgumentOutOfRangeException(nameof(usageQuality));
        if (!Enum.IsDefined(pricingQuality))
            throw new ArgumentOutOfRangeException(nameof(pricingQuality));
        Ordinal = ordinal;
        Kind = kind;
        OperationId = RuntimeValueSnapshot.Text(
            operationId, nameof(operationId), InferenceProtocolEvidence.MaximumOperationIdUtf8Bytes);
        Disposition = disposition;
        MayHaveCommittedEffect = mayHaveCommittedEffect;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        Cost = cost is null ? null : new InferenceCostEvidence(
            cost.CurrencyCode, cost.AmountMicrounits, cost.IsEstimated, cost.PricingRevision);
        UsageQuality = usageQuality;
        PricingQuality = pricingQuality;
        DurationMilliseconds = durationMilliseconds;
        FailureCode = RuntimeValueSnapshot.OptionalText(
            failureCode, nameof(failureCode), InferenceProtocolEvidence.MaximumFailureCodeUtf8Bytes);
    }

    /// <summary>
    /// The zero-based global operation sequence number within its interaction.
    /// It orders every recorded operation across kinds; kinds also carry their
    /// own counters in operation identities.
    /// </summary>
    public int Ordinal { get; }
    /// <summary>The operation kind.</summary>
    public InferenceOperationKind Kind { get; }
    /// <summary>The stable operation identity.</summary>
    public string OperationId { get; }
    /// <summary>The durable disposition.</summary>
    public InferenceOperationDisposition Disposition { get; }
    /// <summary>Whether the operation may have committed an external effect.</summary>
    public bool MayHaveCommittedEffect { get; }
    /// <summary>Prompt tokens charged to this operation, when known.</summary>
    public int? PromptTokens { get; }
    /// <summary>Completion tokens charged to this operation, when known.</summary>
    public int? CompletionTokens { get; }
    /// <summary>Total tokens charged to this operation, when known.</summary>
    public int? TotalTokens { get; }
    /// <summary>Cost attributed to this operation, when known.</summary>
    public InferenceCostEvidence? Cost { get; }
    /// <summary>Quality of the usage evidence.</summary>
    public InferenceUsageQuality UsageQuality { get; }
    /// <summary>Quality of the pricing evidence.</summary>
    public InferencePricingQuality PricingQuality { get; }
    /// <summary>Elapsed time for this operation, when known.</summary>
    public long? DurationMilliseconds { get; }
    /// <summary>The stable failure code, when the operation failed.</summary>
    public string? FailureCode { get; }
}

/// <summary>A bounded summary of one exact read-tool outcome.</summary>
public sealed class InferenceToolOutcomeEvidence
{
    /// <summary>Creates a detached tool-outcome summary without raw payloads.</summary>
    public InferenceToolOutcomeEvidence(
        DescriptorReference tool,
        string operationKey,
        InferenceOperationDisposition disposition,
        bool mayHaveCommittedEffect = false,
        ContentDigest? resultDigest = null,
        int? resultUtf8Bytes = null,
        InferenceReadToolRetrySafety retrySafety = InferenceReadToolRetrySafety.Safe,
        long? durationMilliseconds = null,
        string? failureCode = null)
    {
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        OperationKey = RuntimeValueSnapshot.Text(
            operationKey, nameof(operationKey), InferenceReadToolRequest.MaximumOperationKeyUtf8Bytes);
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        if (resultUtf8Bytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(resultUtf8Bytes));
        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        if (!Enum.IsDefined(retrySafety))
            throw new ArgumentOutOfRangeException(nameof(retrySafety));
        Disposition = disposition;
        MayHaveCommittedEffect = mayHaveCommittedEffect;
        ResultDigest = resultDigest is null ? null : RuntimeValueSnapshot.CloneDigest(resultDigest, nameof(resultDigest));
        ResultUtf8Bytes = resultUtf8Bytes;
        RetrySafety = retrySafety;
        DurationMilliseconds = durationMilliseconds;
        FailureCode = RuntimeValueSnapshot.OptionalText(
            failureCode, nameof(failureCode), InferenceProtocolEvidence.MaximumFailureCodeUtf8Bytes);
    }

    /// <summary>The exact executed tool descriptor.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The stable operation key.</summary>
    public string OperationKey { get; }
    /// <summary>The durable disposition.</summary>
    public InferenceOperationDisposition Disposition { get; }
    /// <summary>Whether the tool may have committed an external effect.</summary>
    public bool MayHaveCommittedEffect { get; }
    /// <summary>Digest of the canonical tool result, when a result was produced.</summary>
    public ContentDigest? ResultDigest { get; }
    /// <summary>UTF-8 length of the canonical tool result, when known.</summary>
    public int? ResultUtf8Bytes { get; }
    /// <summary>The host-attested retry safety.</summary>
    public InferenceReadToolRetrySafety RetrySafety { get; }
    /// <summary>Elapsed host time, when known.</summary>
    public long? DurationMilliseconds { get; }
    /// <summary>The stable failure code, when the tool failed.</summary>
    public string? FailureCode { get; }
}

/// <summary>
/// Bounded, provider-neutral evidence for one coordinated logical inference.
/// It carries identities, limits, usage quality, per-operation summaries, and
/// digests, but never raw context, tool payloads, credentials, or
/// chain-of-thought. Sensitive payloads appear only as host-controlled
/// <see cref="ProtectedPayloadReference"/> values.
/// </summary>
public sealed class InferenceProtocolEvidence
{
    /// <summary>The current coordinated-inference semantics identity.</summary>
    public const string CurrentSemantics = "fuwen-inference-protocol/v1";
    /// <summary>Maximum retained operation summaries.</summary>
    public const int MaximumOperations = 256;
    /// <summary>Maximum retained tool-outcome summaries.</summary>
    public const int MaximumToolOutcomes = 256;
    /// <summary>Maximum retained protected-payload references.</summary>
    public const int MaximumProtectedPayloads = 64;
    /// <summary>Maximum UTF-8 bytes in an interaction identity.</summary>
    public const int MaximumInteractionIdUtf8Bytes = 256;
    /// <summary>Maximum UTF-8 bytes in an operation identity.</summary>
    public const int MaximumOperationIdUtf8Bytes = 512;
    /// <summary>Maximum UTF-8 bytes in a stable failure code.</summary>
    public const int MaximumFailureCodeUtf8Bytes = 256;
    /// <summary>Maximum UTF-8 bytes in a semantics identity.</summary>
    public const int MaximumSemanticsUtf8Bytes = 128;

    /// <summary>Creates detached coordinated-inference evidence.</summary>
    public InferenceProtocolEvidence(
        string interactionId,
        InferenceLimitSet effectiveLimits,
        IReadOnlyList<InferenceOperationEvidence> operations,
        IReadOnlyList<InferenceToolOutcomeEvidence> toolOutcomes,
        int validationAttempts = 0,
        InferenceRecoveryDisposition recoveryDisposition = InferenceRecoveryDisposition.Fresh,
        bool commitmentUncertainty = false,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        InferenceUsageQuality usageQuality = InferenceUsageQuality.Unknown,
        InferenceCostEvidence? cost = null,
        InferencePricingQuality pricingQuality = InferencePricingQuality.Unknown,
        long? durationMilliseconds = null,
        ExecutionFailure? failure = null,
        IReadOnlyList<ProtectedPayloadReference>? protectedPayloads = null,
        string semantics = CurrentSemantics,
        bool operationsTruncated = false,
        bool toolOutcomesTruncated = false)
    {
        InteractionId = RuntimeValueSnapshot.Text(
            interactionId, nameof(interactionId), MaximumInteractionIdUtf8Bytes);
        Semantics = RuntimeValueSnapshot.Text(semantics, nameof(semantics), MaximumSemanticsUtf8Bytes);
        EffectiveLimits = effectiveLimits ?? throw new ArgumentNullException(nameof(effectiveLimits));
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count > MaximumOperations)
            throw new ArgumentOutOfRangeException(nameof(operations));
        Operations = Array.AsReadOnly(operations.Select(static operation =>
            operation ?? throw new ArgumentException("Operations cannot contain null values.", nameof(operations))).ToArray());
        ArgumentNullException.ThrowIfNull(toolOutcomes);
        if (toolOutcomes.Count > MaximumToolOutcomes)
            throw new ArgumentOutOfRangeException(nameof(toolOutcomes));
        ToolOutcomes = Array.AsReadOnly(toolOutcomes.Select(static outcome =>
            outcome ?? throw new ArgumentException("Tool outcomes cannot contain null values.", nameof(toolOutcomes))).ToArray());
        OperationsTruncated = operationsTruncated;
        ToolOutcomesTruncated = toolOutcomesTruncated;
        if (validationAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(validationAttempts));
        ValidationAttempts = validationAttempts;
        if (!Enum.IsDefined(recoveryDisposition))
            throw new ArgumentOutOfRangeException(nameof(recoveryDisposition));
        RecoveryDisposition = recoveryDisposition;
        CommitmentUncertainty = commitmentUncertainty;
        if (promptTokens is < 0 || completionTokens is < 0 || totalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(totalTokens), "Aggregate token usage cannot be negative.");
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        if (!Enum.IsDefined(usageQuality))
            throw new ArgumentOutOfRangeException(nameof(usageQuality));
        UsageQuality = usageQuality;
        Cost = cost is null ? null : new InferenceCostEvidence(
            cost.CurrencyCode, cost.AmountMicrounits, cost.IsEstimated, cost.PricingRevision);
        if (!Enum.IsDefined(pricingQuality))
            throw new ArgumentOutOfRangeException(nameof(pricingQuality));
        PricingQuality = pricingQuality;
        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        DurationMilliseconds = durationMilliseconds;
        Failure = failure;
        var payloads = protectedPayloads ?? [];
        if (payloads.Count > MaximumProtectedPayloads)
            throw new ArgumentOutOfRangeException(nameof(protectedPayloads));
        ProtectedPayloads = Array.AsReadOnly(payloads.Select(static payload =>
            payload ?? throw new ArgumentException("Protected payloads cannot contain null values.", nameof(protectedPayloads))).ToArray());
    }

    /// <summary>The stable logical-activity interaction identity.</summary>
    public string InteractionId { get; }
    /// <summary>The coordinated-inference semantics identity.</summary>
    public string Semantics { get; }
    /// <summary>The effective aggregate limits enforced for this activity.</summary>
    public InferenceLimitSet EffectiveLimits { get; }
    /// <summary>Aggregate prompt tokens across model turns, when known.</summary>
    public int? PromptTokens { get; }
    /// <summary>Aggregate completion tokens across model turns, when known.</summary>
    public int? CompletionTokens { get; }
    /// <summary>Aggregate total tokens across model turns, when known.</summary>
    public int? TotalTokens { get; }
    /// <summary>Quality of the aggregate usage evidence.</summary>
    public InferenceUsageQuality UsageQuality { get; }
    /// <summary>Aggregate cost, when known.</summary>
    public InferenceCostEvidence? Cost { get; }
    /// <summary>Quality of the aggregate pricing evidence.</summary>
    public InferencePricingQuality PricingQuality { get; }
    /// <summary>The bounded per-operation summaries in ordinal order.</summary>
    public IReadOnlyList<InferenceOperationEvidence> Operations { get; }
    /// <summary>Whether operation summaries were dropped at the retention bound.</summary>
    public bool OperationsTruncated { get; }
    /// <summary>The bounded tool-outcome summaries.</summary>
    public IReadOnlyList<InferenceToolOutcomeEvidence> ToolOutcomes { get; }
    /// <summary>Whether tool-outcome summaries were dropped at the retention bound.</summary>
    public bool ToolOutcomesTruncated { get; }
    /// <summary>The number of final-output validation attempts.</summary>
    public int ValidationAttempts { get; }
    /// <summary>How prior durable work participated in this outcome.</summary>
    public InferenceRecoveryDisposition RecoveryDisposition { get; }
    /// <summary>Whether any operation may have committed with an unknown outcome.</summary>
    public bool CommitmentUncertainty { get; }
    /// <summary>Elapsed logical-activity time, when known.</summary>
    public long? DurationMilliseconds { get; }
    /// <summary>The terminal failure, or null for success.</summary>
    public ExecutionFailure? Failure { get; }
    /// <summary>Host-controlled references to sensitive payloads, if any.</summary>
    public IReadOnlyList<ProtectedPayloadReference> ProtectedPayloads { get; }
}

/// <summary>
/// Optional, non-authoritative sink for coordinated-inference evidence. A host
/// may forward this to correlation or history systems; a sink failure or a
/// missing sink never alters execution truth.
/// </summary>
public interface IInferenceEvidenceSink
{
    /// <summary>Records one bounded evidence value; the call is best-effort.</summary>
    ValueTask RecordAsync(InferenceProtocolEvidence evidence, CancellationToken cancellationToken = default);
}

/// <summary>Deterministically renders coordinated-inference evidence for operators.</summary>
public static class InferenceProtocolEvidenceRenderer
{
    /// <summary>Renders a compact, stable human-readable evidence report.</summary>
    public static string RenderHuman(InferenceProtocolEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = new StringBuilder();
        builder.Append("Inference protocol: ").Append(evidence.Semantics).AppendLine();
        builder.Append("Interaction: ").AppendLine(evidence.InteractionId);
        builder.Append("Outcome: ").AppendLine(evidence.Failure is null ? "succeeded" : $"failed [{evidence.Failure.Code}]");
        builder.Append("Recovery: ").AppendLine(evidence.RecoveryDisposition.ToString());
        builder.Append("Commitment uncertainty: ").AppendLine(evidence.CommitmentUncertainty ? "yes" : "no");
        builder.Append("Validation attempts: ").AppendLine(evidence.ValidationAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Usage quality: ").AppendLine(evidence.UsageQuality.ToString());
        builder.Append("Pricing quality: ").AppendLine(evidence.PricingQuality.ToString());
        builder.Append("Aggregate tokens: ")
            .Append(Format(evidence.PromptTokens)).Append(" prompt / ")
            .Append(Format(evidence.CompletionTokens)).Append(" completion / ")
            .Append(Format(evidence.TotalTokens)).AppendLine(" total");
        builder.Append("Aggregate cost: ").AppendLine(evidence.Cost is null
            ? "unknown"
            : $"{evidence.Cost.AmountMicrounits} {evidence.Cost.CurrencyCode} microunits{(evidence.Cost.IsEstimated ? " (estimated)" : string.Empty)}");
        builder.AppendLine("Effective limits:");
        foreach (var limit in evidence.EffectiveLimits.Limits)
            builder.Append("- ").Append(limit.Dimension).Append(": ").AppendLine(
                limit.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (evidence.OperationsTruncated)
            builder.AppendLine("(operation summaries truncated at the retention bound)");
        builder.AppendLine("Operations:");
        foreach (var operation in evidence.Operations)
            builder.Append("- ").Append(operation.OperationId).Append(" [").Append(operation.Kind)
                .Append("] ").Append(operation.Disposition)
                .Append(operation.MayHaveCommittedEffect ? " (may have committed)" : string.Empty)
                .Append(operation.FailureCode is null ? string.Empty : " (" + operation.FailureCode + ")")
                .AppendLine();
        if (evidence.ToolOutcomesTruncated)
            builder.AppendLine("(tool-outcome summaries truncated at the retention bound)");
        builder.AppendLine("Tool outcomes:");
        foreach (var outcome in evidence.ToolOutcomes)
            builder.Append("- ").Append(outcome.Tool.Name).Append('@').Append(outcome.Tool.Version)
                .Append(" (").Append(outcome.OperationKey).Append(") ").Append(outcome.Disposition)
                .Append(outcome.ResultUtf8Bytes is null ? string.Empty : $" {outcome.ResultUtf8Bytes}B")
                .AppendLine();
        builder.AppendLine("Protected payloads:");
        foreach (var payload in evidence.ProtectedPayloads)
            builder.Append("- ").Append(payload.Provider).Append('/').Append(payload.PayloadId)
                .Append(" (").Append(payload.Digest.Value).AppendLine(")");
        return builder.ToString();
    }

    /// <summary>Renders a compact, stable JSON evidence report.</summary>
    public static string RenderJson(InferenceProtocolEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("semantics", evidence.Semantics);
        writer.WriteString("interactionId", evidence.InteractionId);
        writer.WriteBoolean("succeeded", evidence.Failure is null);
        if (evidence.Failure is not null)
        {
            writer.WriteString("failureKind", evidence.Failure.Kind.ToString());
            writer.WriteString("failureCode", evidence.Failure.Code.ToString());
        }
        writer.WriteString("recoveryDisposition", evidence.RecoveryDisposition.ToString());
        writer.WriteBoolean("commitmentUncertainty", evidence.CommitmentUncertainty);
        writer.WriteBoolean("operationsTruncated", evidence.OperationsTruncated);
        writer.WriteBoolean("toolOutcomesTruncated", evidence.ToolOutcomesTruncated);
        writer.WriteNumber("validationAttempts", evidence.ValidationAttempts);
        writer.WriteString("usageQuality", evidence.UsageQuality.ToString());
        writer.WriteString("pricingQuality", evidence.PricingQuality.ToString());
        WriteNullableNumber(writer, "promptTokens", evidence.PromptTokens);
        WriteNullableNumber(writer, "completionTokens", evidence.CompletionTokens);
        WriteNullableNumber(writer, "totalTokens", evidence.TotalTokens);
        writer.WriteStartArray("effectiveLimits");
        foreach (var limit in evidence.EffectiveLimits.Limits)
        {
            writer.WriteStartObject();
            writer.WriteString("dimension", limit.Dimension.ToString());
            writer.WriteNumber("maximum", limit.Maximum);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("operations");
        foreach (var operation in evidence.Operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", operation.Ordinal);
            writer.WriteString("kind", operation.Kind.ToString());
            writer.WriteString("operationId", operation.OperationId);
            writer.WriteString("disposition", operation.Disposition.ToString());
            writer.WriteBoolean("mayHaveCommittedEffect", operation.MayHaveCommittedEffect);
            writer.WriteString("usageQuality", operation.UsageQuality.ToString());
            writer.WriteString("pricingQuality", operation.PricingQuality.ToString());
            if (operation.FailureCode is not null)
                writer.WriteString("failureCode", operation.FailureCode);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("toolOutcomes");
        foreach (var outcome in evidence.ToolOutcomes)
        {
            writer.WriteStartObject();
            writer.WriteString("tool", outcome.Tool.Name + "@" + outcome.Tool.Version);
            writer.WriteString("operationKey", outcome.OperationKey);
            writer.WriteString("disposition", outcome.Disposition.ToString());
            writer.WriteBoolean("mayHaveCommittedEffect", outcome.MayHaveCommittedEffect);
            writer.WriteString("retrySafety", outcome.RetrySafety.ToString());
            if (outcome.ResultUtf8Bytes is int bytes)
                writer.WriteNumber("resultUtf8Bytes", bytes);
            if (outcome.ResultDigest is not null)
                writer.WriteString("resultDigest", outcome.ResultDigest.Value);
            if (outcome.FailureCode is not null)
                writer.WriteString("failureCode", outcome.FailureCode);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("protectedPayloads");
        foreach (var payload in evidence.ProtectedPayloads)
        {
            writer.WriteStartObject();
            writer.WriteString("provider", payload.Provider);
            writer.WriteString("payloadId", payload.PayloadId);
            writer.WriteString("digest", payload.Digest.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is int number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    private static string Format(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
}
