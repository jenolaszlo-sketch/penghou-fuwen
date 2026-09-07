using System.Collections.Concurrent;

namespace Penghou.Fuwen;

/// <summary>The outcome of storing immutable plan-revision lineage.</summary>
public enum PlanRevisionWriteDisposition
{
    /// <summary>A new plan revision was stored.</summary>
    Created,
    /// <summary>The exact plan revision was already stored.</summary>
    AlreadyExists,
}

/// <summary>An immutable store keyed by host-issued plan-revision identity.</summary>
public interface IPlanRevisionStore
{
    /// <summary>Stores a verified revision idempotently.</summary>
    ValueTask<PlanRevisionWriteDisposition> StoreAsync(
        PlanRevisionDocument revision,
        CancellationToken cancellationToken = default);

    /// <summary>Loads and verifies a revision by its stable host-issued identity.</summary>
    ValueTask<PlanRevisionDocument?> ReadAsync(
        string planRevisionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when one plan-revision identity is reused for changed immutable content.</summary>
public sealed class PlanRevisionConflictException : InvalidOperationException
{
    /// <summary>Creates an immutable identity conflict.</summary>
    public PlanRevisionConflictException(string planRevisionId)
        : base($"Plan revision '{planRevisionId}' is already bound to a different immutable envelope.") { }
}

/// <summary>A thread-safe in-memory plan-revision store for hosts and conformance tests.</summary>
public sealed class InMemoryPlanRevisionStore : IPlanRevisionStore
{
    private readonly ConcurrentDictionary<string, StoredRevision> revisions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<PlanRevisionWriteDisposition> StoreAsync(
        PlanRevisionDocument revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        var verified = PlanRevisionDocument.LoadVerified(revision.EnvelopeFingerprint, revision.CanonicalBytes.Span);
        var id = verified.ReadEnvelope().PlanRevisionId;
        var stored = new StoredRevision(verified.EnvelopeFingerprint, verified.CanonicalBytes.ToArray());
        if (revisions.TryAdd(id, stored))
            return ValueTask.FromResult(PlanRevisionWriteDisposition.Created);
        var existing = revisions[id];
        if (!string.Equals(existing.EnvelopeFingerprint, stored.EnvelopeFingerprint, StringComparison.Ordinal) ||
            !existing.CanonicalBytes.AsSpan().SequenceEqual(stored.CanonicalBytes))
            throw new PlanRevisionConflictException(id);
        return ValueTask.FromResult(PlanRevisionWriteDisposition.AlreadyExists);
    }

    /// <inheritdoc />
    public ValueTask<PlanRevisionDocument?> ReadAsync(
        string planRevisionId,
        CancellationToken cancellationToken = default)
    {
        PlanRevisionDocument.ValidatePlanRevisionId(planRevisionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!revisions.TryGetValue(planRevisionId, out var stored))
            return ValueTask.FromResult<PlanRevisionDocument?>(null);
        return ValueTask.FromResult<PlanRevisionDocument?>(
            PlanRevisionDocument.LoadVerified(stored.EnvelopeFingerprint, stored.CanonicalBytes));
    }

    private sealed record StoredRevision(string EnvelopeFingerprint, byte[] CanonicalBytes);
}
