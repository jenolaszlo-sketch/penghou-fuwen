using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>Content identities for the requirements a plan revision intends to satisfy.</summary>
public sealed record PlanRevisionSemantics(
    ContentDigest Objective,
    ContentDigest AcceptanceCriteria,
    ContentDigest ValidationRequirements);

/// <summary>The role of an immutable artifact in a plan-revision decision.</summary>
public enum PlanRevisionReferenceKind
{
    /// <summary>An artifact containing the proposed plan or requested change.</summary>
    Proposal,
    /// <summary>An artifact containing a bounded rationale suitable for retention.</summary>
    Reason,
    /// <summary>An artifact containing validation or decision evidence.</summary>
    Evidence,
}

/// <summary>An immutable artifact supporting one aspect of a plan-revision decision.</summary>
public sealed record PlanRevisionReference(
    PlanRevisionReferenceKind Kind,
    ArtifactReference Artifact);

/// <summary>Canonical lineage payload for one exact executable-plan identity.</summary>
public sealed record PlanRevisionEnvelope(
    string EnvelopeVersion,
    string PlanRevisionId,
    string ExecutionFingerprint,
    string? ParentRevisionId,
    PlanRevisionSemantics Semantics,
    IReadOnlyList<PlanRevisionReference> References);

/// <summary>
/// Verified immutable lineage metadata for one exact executable plan. This is
/// integrity evidence, not an execution authorization or deployment pointer.
/// </summary>
public sealed class PlanRevisionDocument
{
    /// <summary>The canonical lineage-envelope contract emitted by this version.</summary>
    public const string EnvelopeVersion = "fuwen-revision/v1";
    /// <summary>The maximum number of retained immutable artifact references.</summary>
    public const int MaximumReferences = 64;
    /// <summary>The maximum canonical persisted envelope size.</summary>
    public const int MaximumCanonicalBytes = 262_144;
    private const string FingerprintPrefix = "sha256:fuwen-revision/v1:";
    private readonly byte[] canonicalBytes;

    private PlanRevisionDocument(string envelopeFingerprint, byte[] canonicalBytes)
    {
        EnvelopeFingerprint = envelopeFingerprint;
        this.canonicalBytes = canonicalBytes;
    }

    /// <summary>Content identity of the complete canonical lineage envelope.</summary>
    public string EnvelopeFingerprint { get; }

    /// <summary>A defensive copy of the canonical persisted envelope bytes.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes => canonicalBytes.ToArray();

    /// <summary>Creates lineage bound to an already verified immutable definition.</summary>
    public static PlanRevisionDocument Create(
        WorkflowDefinitionDocument definition,
        string planRevisionId,
        string? parentRevisionId,
        PlanRevisionSemantics semantics,
        IEnumerable<PlanRevisionReference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var envelope = Snapshot(
            planRevisionId,
            definition.ExecutionFingerprint,
            parentRevisionId,
            semantics,
            references ?? []);
        var bytes = CanonicalJson.Serialize(envelope);
        if (bytes.Length > MaximumCanonicalBytes)
            throw new ArgumentException($"Plan-revision envelope exceeds {MaximumCanonicalBytes} canonical bytes.", nameof(references));
        return new PlanRevisionDocument(ComputeFingerprint(bytes), bytes);
    }

    /// <summary>Loads canonical persisted lineage bytes and verifies their claimed identity.</summary>
    public static PlanRevisionDocument LoadVerified(
        string envelopeFingerprint,
        ReadOnlySpan<byte> persistedBytes)
    {
        ValidateEnvelopeFingerprint(envelopeFingerprint);
        if (persistedBytes.Length > MaximumCanonicalBytes)
            throw new PlanRevisionIntegrityException("Persisted plan-revision envelope exceeds its size limit.");

        try
        {
            var parsed = CanonicalJson.Deserialize<PlanRevisionEnvelope>(persistedBytes);
            if (!string.Equals(parsed.EnvelopeVersion, EnvelopeVersion, StringComparison.Ordinal))
                throw new PlanRevisionIntegrityException("Persisted plan-revision envelope version is unsupported.");
            var normalized = Snapshot(
                parsed.PlanRevisionId,
                parsed.ExecutionFingerprint,
                parsed.ParentRevisionId,
                parsed.Semantics,
                parsed.References);
            var canonical = CanonicalJson.Serialize(normalized);
            if (!persistedBytes.SequenceEqual(canonical))
                throw new PlanRevisionIntegrityException("Persisted plan-revision envelope is not canonical.");
            if (!string.Equals(envelopeFingerprint, ComputeFingerprint(canonical), StringComparison.Ordinal))
                throw new PlanRevisionIntegrityException("Persisted plan-revision envelope fingerprint does not match its content.");
            return new PlanRevisionDocument(envelopeFingerprint, canonical);
        }
        catch (PlanRevisionIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or JsonException or NullReferenceException)
        {
            throw new PlanRevisionIntegrityException("Persisted plan-revision envelope is invalid.");
        }
    }

    /// <summary>Deserializes a defensive copy of the verified canonical lineage payload.</summary>
    public PlanRevisionEnvelope ReadEnvelope() => CanonicalJson.Deserialize<PlanRevisionEnvelope>(canonicalBytes);

    /// <summary>Rejects malformed or non-canonical revision-envelope fingerprints.</summary>
    public static void ValidateEnvelopeFingerprint(string envelopeFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeFingerprint);
        if (!envelopeFingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal) ||
            envelopeFingerprint.Length != FingerprintPrefix.Length + 64)
            throw new ArgumentException("Plan-revision fingerprint must use canonical lowercase SHA-256 form.", nameof(envelopeFingerprint));
        foreach (var character in envelopeFingerprint.AsSpan(FingerprintPrefix.Length))
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                throw new ArgumentException("Plan-revision fingerprint must use canonical lowercase SHA-256 form.", nameof(envelopeFingerprint));
    }

    internal static void ValidatePlanRevisionId(string planRevisionId) =>
        _ = Text(planRevisionId, nameof(planRevisionId), 128);

    private static PlanRevisionEnvelope Snapshot(
        string planRevisionId,
        string executionFingerprint,
        string? parentRevisionId,
        PlanRevisionSemantics semantics,
        IEnumerable<PlanRevisionReference> references)
    {
        ValidatePlanRevisionId(planRevisionId);
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        parentRevisionId = OptionalText(parentRevisionId, nameof(parentRevisionId), 128);
        if (string.Equals(planRevisionId, parentRevisionId, StringComparison.Ordinal))
            throw new ArgumentException("A plan revision cannot be its own parent.", nameof(parentRevisionId));
        ArgumentNullException.ThrowIfNull(semantics);
        var semanticSnapshot = new PlanRevisionSemantics(
            Digest(semantics.Objective),
            Digest(semantics.AcceptanceCriteria),
            Digest(semantics.ValidationRequirements));

        ArgumentNullException.ThrowIfNull(references);
        var snapshot = references.Take(MaximumReferences + 1)
            .Select(SnapshotReference)
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Artifact.Provider, StringComparer.Ordinal)
            .ThenBy(static item => item.Artifact.ArtifactId, StringComparer.Ordinal)
            .ThenBy(static item => item.Artifact.ContentDigest.Value, StringComparer.Ordinal)
            .ToArray();
        if (snapshot.Length > MaximumReferences)
            throw new ArgumentException($"A plan revision supports at most {MaximumReferences} references.", nameof(references));
        if (snapshot.Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Plan-revision references must be unique.", nameof(references));
        return new PlanRevisionEnvelope(EnvelopeVersion, planRevisionId, executionFingerprint, parentRevisionId, semanticSnapshot, snapshot);
    }

    private static PlanRevisionReference SnapshotReference(PlanRevisionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Enum.IsDefined(reference.Kind))
            throw new ArgumentOutOfRangeException(nameof(reference), "Plan-revision reference kind is unsupported.");
        ArgumentNullException.ThrowIfNull(reference.Artifact);
        var artifact = reference.Artifact;
        ArgumentNullException.ThrowIfNull(artifact.ArtifactDescriptor);
        var descriptor = artifact.ArtifactDescriptor;
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentOutOfRangeException(nameof(reference), "Artifact descriptor kind is unsupported.");
        if (descriptor.Kind != DescriptorKind.Artifact)
            throw new ArgumentException(
                $"Artifact descriptor must be of kind '{DescriptorKind.Artifact}', not '{descriptor.Kind}'.",
                nameof(reference));
        var descriptorSnapshot = new DescriptorReference(
            descriptor.Kind,
            Text(descriptor.Name, nameof(descriptor.Name), 256)!,
            Text(descriptor.Version, nameof(descriptor.Version), 128)!,
            Digest(descriptor.ContentDigest));
        var artifactSnapshot = new ArtifactReference(
            Text(artifact.Provider, nameof(artifact.Provider), 128)!,
            Text(artifact.ArtifactId, nameof(artifact.ArtifactId), 512)!,
            descriptorSnapshot,
            Digest(artifact.ContentDigest),
            artifact.ByteLength is < 0 ? throw new ArgumentOutOfRangeException(nameof(artifact.ByteLength)) : artifact.ByteLength,
            OptionalText(artifact.LogicalName, nameof(artifact.LogicalName), 512));
        return new PlanRevisionReference(reference.Kind, artifactSnapshot);
    }

    private static ContentDigest Digest(ContentDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return new ContentDigest(
            Text(digest.Algorithm, nameof(digest.Algorithm), 64)!,
            Text(digest.Contract, nameof(digest.Contract), 256)!,
            Text(digest.Value, nameof(digest.Value), 1024)!);
    }

    private static string Text(string value, string parameterName, int maximumUtf8Bytes) =>
        OptionalText(value, parameterName, maximumUtf8Bytes) ?? throw new ArgumentNullException(parameterName);

    private static string? OptionalText(string? value, string parameterName, int maximumUtf8Bytes)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("The value cannot contain control characters.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentOutOfRangeException(parameterName, $"The value exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        return value;
    }

    private static string ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return $"{FingerprintPrefix}{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

/// <summary>Raised when persisted plan-revision lineage fails canonical integrity verification.</summary>
public sealed class PlanRevisionIntegrityException : IOException
{
    /// <summary>Creates an integrity failure with a safe message.</summary>
    public PlanRevisionIntegrityException(string message) : base(message) { }
}
