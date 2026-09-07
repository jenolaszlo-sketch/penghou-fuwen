namespace Penghou.Fuwen;

/// <summary>A provider-neutral immutable artifact identity.</summary>
public sealed record ArtifactReference
{
    private DescriptorReference artifactDescriptor = null!;

    /// <summary>Creates an immutable provider-neutral artifact identity.</summary>
    public ArtifactReference(
        string Provider,
        string ArtifactId,
        DescriptorReference ArtifactDescriptor,
        ContentDigest ContentDigest,
        long? ByteLength = null,
        string? LogicalName = null)
    {
        this.Provider = Provider;
        this.ArtifactId = ArtifactId;
        this.ArtifactDescriptor = ArtifactDescriptor;
        this.ContentDigest = ContentDigest;
        this.ByteLength = ByteLength;
        this.LogicalName = LogicalName;
    }

    /// <summary>The provider that owns or serves the artifact.</summary>
    public string Provider { get; init; }
    /// <summary>The provider-local artifact identifier.</summary>
    public string ArtifactId { get; init; }
    /// <summary>The descriptor that establishes the artifact's nominal type.</summary>
    public DescriptorReference ArtifactDescriptor
    {
        get => artifactDescriptor;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Kind != DescriptorKind.Artifact)
                throw new ArgumentException(
                    $"Artifact descriptor must be of kind '{DescriptorKind.Artifact}', not '{value.Kind}'.",
                    nameof(ArtifactDescriptor));
            artifactDescriptor = value;
        }
    }
    /// <summary>The content identity of the artifact bytes.</summary>
    public ContentDigest ContentDigest { get; init; }
    /// <summary>The artifact byte length when known.</summary>
    public long? ByteLength { get; init; }
    /// <summary>The optional provider-independent logical name.</summary>
    public string? LogicalName { get; init; }
}

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
