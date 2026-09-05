using System.Collections.Concurrent;

namespace Penghou.Fuwen;

/// <summary>The outcome of an immutable definition-store write.</summary>
public enum WorkflowDefinitionWriteDisposition
{
    /// <summary>A new immutable definition was stored.</summary>
    Created,
    /// <summary>The exact canonical definition was already stored.</summary>
    AlreadyExists,
}

/// <summary>A verified execution fingerprint and its canonical IR bytes.</summary>
public sealed class WorkflowDefinitionDocument
{
    private readonly byte[] canonicalBytes;

    private WorkflowDefinitionDocument(string executionFingerprint, byte[] canonicalBytes)
    {
        ExecutionFingerprint = executionFingerprint;
        this.canonicalBytes = canonicalBytes;
    }

    /// <summary>The identity derived from the canonical executable plan.</summary>
    public string ExecutionFingerprint { get; }

    /// <summary>A defensive copy of the canonical bytes.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes => canonicalBytes.ToArray();

    /// <summary>Creates a verified immutable document from a resolved plan.</summary>
    public static WorkflowDefinitionDocument Create(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        return new WorkflowDefinitionDocument(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan), bytes);
    }

    /// <summary>
    /// Loads untrusted persisted bytes and verifies compatibility, canonical
    /// representation, and the claimed execution fingerprint.
    /// </summary>
    public static WorkflowDefinitionDocument LoadVerified(
        string executionFingerprint,
        ReadOnlySpan<byte> persistedBytes)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        if (persistedBytes.Length > FuwenContracts.MaximumCanonicalPlanBytes)
            throw new WorkflowDefinitionIntegrityException(
                $"Persisted workflow definition exceeds {FuwenContracts.MaximumCanonicalPlanBytes} bytes.");
        var persistedCopy = persistedBytes.ToArray();
        var plan = CanonicalJson.Deserialize<WorkflowPlan>(persistedCopy);
        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        if (!persistedCopy.AsSpan().SequenceEqual(canonicalBytes))
            throw new WorkflowDefinitionIntegrityException("Persisted workflow definition is not canonical IR.");

        var computed = WorkflowPlanIdentity.ComputeExecutionFingerprint(plan);
        if (!string.Equals(executionFingerprint, computed, StringComparison.Ordinal))
            throw new WorkflowDefinitionIntegrityException(
                $"Workflow definition fingerprint mismatch. Claimed '{executionFingerprint}', computed '{computed}'.");

        return new WorkflowDefinitionDocument(computed, canonicalBytes);
    }

    /// <summary>Deserializes and revalidates the document as a resolved plan.</summary>
    public WorkflowPlan ReadPlan() => CanonicalJson.Deserialize<WorkflowPlan>(canonicalBytes);
}

/// <summary>An immutable content-addressed store for executable Fuwen plans.</summary>
public interface IWorkflowDefinitionStore
{
    /// <summary>Stores a verified definition idempotently.</summary>
    ValueTask<WorkflowDefinitionWriteDisposition> StoreAsync(
        WorkflowDefinitionDocument definition,
        CancellationToken cancellationToken = default);

    /// <summary>Loads and verifies a definition, or returns null when absent.</summary>
    ValueTask<WorkflowDefinitionDocument?> ReadAsync(
        string executionFingerprint,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when persisted definition bytes fail immutable identity checks.</summary>
public sealed class WorkflowDefinitionIntegrityException : IOException
{
    /// <summary>Creates an integrity failure with a safe diagnostic message.</summary>
    public WorkflowDefinitionIntegrityException(string message) : base(message) { }
}

/// <summary>Raised when one fingerprint is associated with different bytes.</summary>
public sealed class WorkflowDefinitionConflictException : InvalidOperationException
{
    /// <summary>Creates an immutable-key conflict.</summary>
    public WorkflowDefinitionConflictException(string executionFingerprint)
        : base($"Execution fingerprint '{executionFingerprint}' is already bound to different canonical bytes.") { }
}

/// <summary>A thread-safe in-memory definition store for hosts and conformance tests.</summary>
public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly ConcurrentDictionary<string, byte[]> definitions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<WorkflowDefinitionWriteDisposition> StoreAsync(
        WorkflowDefinitionDocument definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        // Re-verify at the trust boundary even when the document came from a host helper.
        var verified = WorkflowDefinitionDocument.LoadVerified(
            definition.ExecutionFingerprint,
            definition.CanonicalBytes.Span);
        var bytes = verified.CanonicalBytes.ToArray();

        if (definitions.TryAdd(verified.ExecutionFingerprint, bytes))
            return ValueTask.FromResult(WorkflowDefinitionWriteDisposition.Created);

        var existing = definitions[verified.ExecutionFingerprint];
        if (!existing.AsSpan().SequenceEqual(bytes))
            throw new WorkflowDefinitionConflictException(verified.ExecutionFingerprint);
        return ValueTask.FromResult(WorkflowDefinitionWriteDisposition.AlreadyExists);
    }

    /// <inheritdoc />
    public ValueTask<WorkflowDefinitionDocument?> ReadAsync(
        string executionFingerprint,
        CancellationToken cancellationToken = default)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        cancellationToken.ThrowIfCancellationRequested();
        if (!definitions.TryGetValue(executionFingerprint, out var bytes))
            return ValueTask.FromResult<WorkflowDefinitionDocument?>(null);

        // Reads never trust storage, including this in-memory implementation.
        var verified = WorkflowDefinitionDocument.LoadVerified(executionFingerprint, bytes);
        return ValueTask.FromResult<WorkflowDefinitionDocument?>(verified);
    }
}
