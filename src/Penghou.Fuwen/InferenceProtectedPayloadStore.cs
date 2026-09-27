namespace Penghou.Fuwen;

/// <summary>A request to durably retain one coordinated-inference payload outside workflow state.</summary>
public sealed class InferenceProtectedPayloadWriteRequest
{
    private readonly byte[] content;

    /// <summary>Creates a detached, idempotent write request for one exact tool result.</summary>
    public InferenceProtectedPayloadWriteRequest(
        string interactionId,
        string scope,
        DescriptorReference tool,
        string operationKey,
        ReadOnlyMemory<byte> content)
    {
        InteractionId = RuntimeValueSnapshot.Text(
            interactionId, nameof(interactionId), InferenceProtocolEvidence.MaximumInteractionIdUtf8Bytes);
        Scope = RuntimeValueSnapshot.Text(scope, nameof(scope), InferenceReadToolRequest.MaximumScopeUtf8Bytes);
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        OperationKey = RuntimeValueSnapshot.Text(
            operationKey, nameof(operationKey), InferenceReadToolRequest.MaximumOperationKeyUtf8Bytes);
        if (content.IsEmpty || content.Length > JsonRuntimeValue.MaximumJsonUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(content));
        this.content = content.ToArray();
    }

    /// <summary>The stable inference interaction owning this payload.</summary>
    public string InteractionId { get; }
    /// <summary>The exact capability/resource scope used for the tool call.</summary>
    public string Scope { get; }
    /// <summary>The exact admitted tool that produced the payload.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The stable idempotency key for retrying the same durable put.</summary>
    public string OperationKey { get; }
    /// <summary>The canonical UTF-8 JSON bytes to retain.</summary>
    public ReadOnlyMemory<byte> Content => content;
}

/// <summary>An authorized read request for a payload referenced by durable inference state.</summary>
public sealed class InferenceProtectedPayloadReadRequest
{
    /// <summary>Creates a detached read request whose scope is re-authorized by the host store.</summary>
    public InferenceProtectedPayloadReadRequest(
        string interactionId,
        string scope,
        DescriptorReference tool,
        ProtectedPayloadReference payload)
    {
        InteractionId = RuntimeValueSnapshot.Text(
            interactionId, nameof(interactionId), InferenceProtocolEvidence.MaximumInteractionIdUtf8Bytes);
        Scope = RuntimeValueSnapshot.Text(scope, nameof(scope), InferenceReadToolRequest.MaximumScopeUtf8Bytes);
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        Payload = payload is null
            ? throw new ArgumentNullException(nameof(payload))
            : new ProtectedPayloadReference(
                payload.Provider,
                payload.PayloadId,
                payload.Digest,
                payload.Descriptor,
                payload.ByteLength,
                payload.RetentionPolicyRevision,
                payload.StorageIdentity);
    }

    /// <summary>The stable inference interaction owning this payload.</summary>
    public string InteractionId { get; }
    /// <summary>The exact capability/resource scope used for the tool call.</summary>
    public string Scope { get; }
    /// <summary>The exact admitted tool that produced the payload.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The explicit protected reference to retrieve.</summary>
    public ProtectedPayloadReference Payload { get; }
}

/// <summary>
/// Host-owned durable storage for sensitive inference payloads. Implementations
/// must authorize every read, enforce idempotent puts by operation key, and
/// reject reuse of one key for different content. Missing and unauthorized
/// reads should return null or the same non-revealing failure.
/// </summary>
public interface IInferenceProtectedPayloadStore
{
    /// <summary>Durably stores the bytes and returns a reference suitable for workflow state.</summary>
    ValueTask<ProtectedPayloadReference> PutAsync(
        InferenceProtectedPayloadWriteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one authorized payload, or returns null when it is unavailable.</summary>
    ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        InferenceProtectedPayloadReadRequest request,
        CancellationToken cancellationToken = default);
}
