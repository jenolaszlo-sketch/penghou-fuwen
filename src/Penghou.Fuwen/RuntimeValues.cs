using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>A provider-neutral runtime value owned by the Fuwen boundary.</summary>
/// <remarks>
/// Runtime values deliberately contain either a detached JSON value or an
/// immutable artifact identity. They do not contain CLR objects, provider
/// handles, credentials, paths, or dereferenced artifact content.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(JsonRuntimeValue), "json")]
[JsonDerivedType(typeof(ArtifactRuntimeValue), "artifact")]
public abstract class RuntimeValue
{
    /// <summary>Initializes the provider-neutral runtime-value contract.</summary>
    private protected RuntimeValue() { }

    /// <summary>Creates a detached JSON runtime value.</summary>
    public static RuntimeValue FromJson(JsonElement value) => new JsonRuntimeValue(value);

    /// <summary>Creates an owned artifact-reference runtime value.</summary>
    public static RuntimeValue FromArtifact(ArtifactReference artifact) => new ArtifactRuntimeValue(artifact);
}

/// <summary>A detached JSON runtime value.</summary>
public sealed class JsonRuntimeValue : RuntimeValue
{
    /// <summary>The maximum UTF-8 size of a detached JSON runtime value.</summary>
    public const int MaximumJsonUtf8Bytes = FuwenContracts.MaximumCanonicalPlanBytes;
    /// <summary>The maximum number of JSON value nodes in a detached value.</summary>
    public const int MaximumJsonNodes = 100_000;
    /// <summary>The maximum nesting depth of a detached JSON value.</summary>
    public const int MaximumJsonDepth = 128;

    private readonly JsonElement value;

    /// <summary>Creates a value by cloning the caller-owned JSON tree.</summary>
    [JsonConstructor]
    public JsonRuntimeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("An undefined JSON value is not a runtime value.", nameof(value));
        ValidateBounds(value);
        this.value = value.Clone();
    }

    /// <summary>Returns a detached clone of the JSON tree.</summary>
    public JsonElement Value => value.Clone();

    // Compiler-side validation must walk the already-detached tree without
    // cloning each child. The public property remains defensive.
    internal JsonElement BorrowedValue => value;

    private static void ValidateBounds(JsonElement element)
    {
        var nodes = 0;
        ValidateShape(element, 0, ref nodes);
        var raw = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > MaximumJsonUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(element), $"JSON runtime value exceeds {MaximumJsonUtf8Bytes} UTF-8 bytes.");
    }

    private static void ValidateShape(JsonElement element, int depth, ref int nodes)
    {
        if (depth > MaximumJsonDepth)
            throw new ArgumentException($"JSON runtime value exceeds the maximum depth of {MaximumJsonDepth}.", nameof(element));
        if (++nodes > MaximumJsonNodes)
            throw new ArgumentException($"JSON runtime value exceeds the maximum of {MaximumJsonNodes} nodes.", nameof(element));
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    ValidateShape(property.Value, depth + 1, ref nodes);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateShape(item, depth + 1, ref nodes);
                break;
        }
    }
}

/// <summary>A detached runtime value containing only an artifact identity.</summary>
public sealed class ArtifactRuntimeValue : RuntimeValue
{
    private readonly ArtifactReference artifact;

    /// <summary>Creates a value by deeply snapshotting the artifact identity.</summary>
    [JsonConstructor]
    public ArtifactRuntimeValue(ArtifactReference artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        this.artifact = RuntimeValueSnapshot.CloneArtifact(artifact);
    }

    /// <summary>Returns a detached copy of the artifact identity.</summary>
    public ArtifactReference Artifact => RuntimeValueSnapshot.CloneArtifact(artifact);
}

/// <summary>A bounded immutable source revision used by a context snapshot.</summary>
public sealed record ContextSourceRevisionReference(
    string SourceId,
    string Revision,
    ContentDigest ContentDigest);

/// <summary>Bounded truncation and budget evidence for a context snapshot.</summary>
public sealed record ContextSnapshotBudgetEvidence(
    bool WasTruncated,
    long? MaximumItems,
    long? MaximumBytes,
    long? ObservedItems,
    long? ObservedBytes);

/// <summary>
/// Immutable metadata identifying one host-owned context snapshot. The context
/// payload itself remains outside Fuwen and is never carried by this contract.
/// </summary>
public sealed class ContextSnapshotReference
{
    /// <summary>The maximum number of source revisions retained in one reference.</summary>
    public const int MaximumSourceRevisions = 64;
    /// <summary>The maximum UTF-8 length of an opaque snapshot identifier.</summary>
    public const int MaximumSnapshotIdUtf8Bytes = 512;
    /// <summary>The maximum UTF-8 length of an opaque provenance receipt.</summary>
    public const int MaximumProvenanceReceiptUtf8Bytes = 2048;

    private readonly IReadOnlyList<ContextSourceRevisionReference> sourceRevisions;

    /// <summary>Creates an immutable, bounded context snapshot reference.</summary>
    [JsonConstructor]
    public ContextSnapshotReference(
        DescriptorReference provider,
        string snapshotId,
        ContentDigest requestDigest,
        ContentDigest contentDigest,
        IReadOnlyList<ContextSourceRevisionReference> sourceRevisions,
        string policyRevision,
        ContextSnapshotBudgetEvidence budget,
        DateTimeOffset createdAt,
        string? provenanceReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Kind != DescriptorKind.ContextProvider)
            throw new ArgumentException("Context snapshot provider must be a ContextProvider descriptor.", nameof(provider));
        Provider = RuntimeValueSnapshot.CloneDescriptor(provider);
        SnapshotId = RuntimeValueSnapshot.Text(snapshotId, nameof(snapshotId), MaximumSnapshotIdUtf8Bytes);
        RequestDigest = RuntimeValueSnapshot.CloneDigest(requestDigest, nameof(requestDigest));
        ContentDigest = RuntimeValueSnapshot.CloneDigest(contentDigest, nameof(contentDigest));
        ArgumentNullException.ThrowIfNull(sourceRevisions);
        if (sourceRevisions.Count > MaximumSourceRevisions)
            throw new ArgumentOutOfRangeException(nameof(sourceRevisions), $"A context snapshot supports at most {MaximumSourceRevisions} source revisions.");
        this.sourceRevisions = Array.AsReadOnly(sourceRevisions.Select(RuntimeValueSnapshot.CloneSourceRevision).ToArray());
        PolicyRevision = RuntimeValueSnapshot.Text(policyRevision, nameof(policyRevision), 512);
        Budget = RuntimeValueSnapshot.CloneBudget(budget);
        CreatedAt = createdAt.ToUniversalTime();
        if (CreatedAt == DateTimeOffset.MinValue || CreatedAt == DateTimeOffset.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(createdAt));
        ProvenanceReceipt = RuntimeValueSnapshot.OptionalText(provenanceReceipt, nameof(provenanceReceipt), MaximumProvenanceReceiptUtf8Bytes);
    }

    /// <summary>The exact trusted context-provider descriptor used.</summary>
    public DescriptorReference Provider { get; }
    /// <summary>An opaque host-issued snapshot identity.</summary>
    public string SnapshotId { get; }
    /// <summary>Identity of the normalized context request.</summary>
    public ContentDigest RequestDigest { get; }
    /// <summary>Identity of the selected context content.</summary>
    public ContentDigest ContentDigest { get; }
    /// <summary>Deeply snapshotted source and revision identities.</summary>
    public IReadOnlyList<ContextSourceRevisionReference> SourceRevisions => sourceRevisions;
    /// <summary>The policy revision used to select and redact context.</summary>
    public string PolicyRevision { get; }
    /// <summary>Truncation and budget evidence reported by the host.</summary>
    public ContextSnapshotBudgetEvidence Budget { get; }
    /// <summary>The UTC time at which the snapshot was created.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>An optional bounded opaque host provenance receipt.</summary>
    public string? ProvenanceReceipt { get; }
}

internal static class RuntimeValueSnapshot
{
    internal static ArtifactReference CloneArtifact(ArtifactReference artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new ArtifactReference(
            Text(artifact.Provider, nameof(artifact.Provider), 256),
            Text(artifact.ArtifactId, nameof(artifact.ArtifactId), 1024),
            CloneDescriptor(artifact.ArtifactDescriptor),
            CloneDigest(artifact.ContentDigest, nameof(artifact.ContentDigest)),
            artifact.ByteLength is < 0 ? throw new ArgumentOutOfRangeException(nameof(artifact.ByteLength)) : artifact.ByteLength,
            OptionalText(artifact.LogicalName, nameof(artifact.LogicalName), 1024));
    }

    internal static DescriptorReference CloneDescriptor(DescriptorReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        return new DescriptorReference(
            descriptor.Kind,
            Text(descriptor.Name, nameof(descriptor.Name), 512),
            Text(descriptor.Version, nameof(descriptor.Version), 256),
            CloneDigest(descriptor.ContentDigest, nameof(descriptor.ContentDigest)));
    }

    internal static ContentDigest CloneDigest(ContentDigest digest, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(digest);
        DescriptorDigestValidation.Validate(digest, parameterName);
        return new ContentDigest(digest.Algorithm, digest.Contract, digest.Value);
    }

    internal static ContextSourceRevisionReference CloneSourceRevision(ContextSourceRevisionReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ContextSourceRevisionReference(
            Text(source.SourceId, nameof(source.SourceId), 512),
            Text(source.Revision, nameof(source.Revision), 512),
            CloneDigest(source.ContentDigest, nameof(source.ContentDigest)));
    }

    internal static ContextSnapshotBudgetEvidence CloneBudget(ContextSnapshotBudgetEvidence budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ValidateCount(budget.MaximumItems, nameof(budget.MaximumItems));
        ValidateCount(budget.MaximumBytes, nameof(budget.MaximumBytes));
        ValidateCount(budget.ObservedItems, nameof(budget.ObservedItems));
        ValidateCount(budget.ObservedBytes, nameof(budget.ObservedBytes));
        if (!budget.WasTruncated && budget.MaximumItems is not null && budget.ObservedItems > budget.MaximumItems)
            throw new ArgumentException("Observed item count cannot exceed its maximum.", nameof(budget));
        if (!budget.WasTruncated && budget.MaximumBytes is not null && budget.ObservedBytes > budget.MaximumBytes)
            throw new ArgumentException("Observed byte count cannot exceed its maximum.", nameof(budget));
        return new ContextSnapshotBudgetEvidence(
            budget.WasTruncated,
            budget.MaximumItems,
            budget.MaximumBytes,
            budget.ObservedItems,
            budget.ObservedBytes);
    }

    internal static string Text(string value, string parameterName, int maximumUtf8Bytes) =>
        OptionalText(value, parameterName, maximumUtf8Bytes) ?? throw new ArgumentNullException(parameterName);

    internal static string? OptionalText(string? value, string parameterName, int maximumUtf8Bytes)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("The value cannot contain control characters.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentOutOfRangeException(parameterName, $"The value exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        return new string(value.ToCharArray());
    }

    private static void ValidateCount(long? value, string parameterName)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(parameterName);
    }
}
