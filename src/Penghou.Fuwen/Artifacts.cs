namespace Penghou.Fuwen;

/// <summary>A provider-neutral immutable artifact identity.</summary>
public sealed record ArtifactReference(
    string Provider,
    string ArtifactId,
    DescriptorReference ArtifactDescriptor,
    ContentDigest ContentDigest,
    long? ByteLength = null,
    string? LogicalName = null);

/// <summary>An opaque, host-issued scoped access capability.</summary>
public sealed record ResourceHandle(string Kind, string HandleId);

/// <summary>Whether publication created a value or replayed an idempotent request.</summary>
public enum PublicationDisposition
{
    /// <summary>The provider created a new publication.</summary>
    Created,
    /// <summary>The provider returned the prior result for the idempotency key.</summary>
    Replayed,
}

/// <summary>Provider evidence returned after a host verifies artifact publication.</summary>
public sealed record ArtifactPublicationReceipt(
    string IdempotencyKey,
    ArtifactReference Artifact,
    string ProviderReceiptId,
    PublicationDisposition Disposition);
