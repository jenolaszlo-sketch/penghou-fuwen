namespace Penghou.Fuwen;

/// <summary>Optional storage of the entire pinned group expansion alongside ordinary IR.</summary>
public interface IGroupedWorkflowDefinitionStore
{
    /// <summary>Atomically stores an immutable verified bundle.</summary>
    ValueTask<WorkflowDefinitionWriteDisposition> StoreGroupedAsync(GroupedWorkflowDefinitionDocument definition, CancellationToken cancellationToken = default);
    /// <summary>Loads and verifies the exact bundle; never re-expands source.</summary>
    ValueTask<GroupedWorkflowDefinitionDocument?> ReadGroupedAsync(string fingerprint, CancellationToken cancellationToken = default);
}

/// <summary>Atomic content-addressed file storage for expanded definitions and hierarchy companions.</summary>
public sealed class DirectoryWorkflowDefinitionStore : IWorkflowDefinitionStore, IGroupedWorkflowDefinitionStore
{
    private readonly string directory;
    /// <summary>Uses a host-owned directory; file identities are validated digests, never source paths.</summary>
    public DirectoryWorkflowDefinitionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }
    /// <inheritdoc />
    public ValueTask<WorkflowDefinitionWriteDisposition> StoreAsync(WorkflowDefinitionDocument definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var verified = WorkflowDefinitionDocument.LoadVerified(definition.ExecutionFingerprint, definition.CanonicalBytes.Span);
        return WriteAsync(PlanPath(verified.ExecutionFingerprint), verified.CanonicalBytes.ToArray(), cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask<WorkflowDefinitionDocument?> ReadAsync(string executionFingerprint, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBytesAsync(PlanPath(executionFingerprint), FuwenContracts.MaximumCanonicalPlanBytes, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : WorkflowDefinitionDocument.LoadVerified(executionFingerprint, bytes);
    }
    /// <inheritdoc />
    public ValueTask<WorkflowDefinitionWriteDisposition> StoreGroupedAsync(GroupedWorkflowDefinitionDocument definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var verified = GroupedWorkflowDefinitionDocument.LoadVerified(definition.Fingerprint, definition.CanonicalBytes.Span);
        return WriteAsync(GroupPath(verified.Fingerprint), verified.CanonicalBytes.ToArray(), cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask<GroupedWorkflowDefinitionDocument?> ReadGroupedAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBytesAsync(GroupPath(fingerprint), FuwenContracts.MaximumCanonicalPlanBytes + 1_048_576, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : GroupedWorkflowDefinitionDocument.LoadVerified(fingerprint, bytes);
    }
    private string PlanPath(string fingerprint)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(fingerprint);
        return Path.Combine(directory, "plan-" + fingerprint[^64..] + ".json");
    }
    private string GroupPath(string fingerprint)
    {
        const string prefix = "sha256:fuwen-grouped-definition/v1:";
        if (fingerprint is null || fingerprint.Length != prefix.Length + 64 || !fingerprint.StartsWith(prefix, StringComparison.Ordinal)
            || fingerprint.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")) throw new ArgumentException("Invalid grouped definition fingerprint.", nameof(fingerprint));
        return Path.Combine(directory, "group-" + fingerprint[^64..] + ".json");
    }
    private static async ValueTask<byte[]?> ReadBytesAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (stream.Length > maximum) throw new WorkflowDefinitionIntegrityException("Stored definition exceeds its byte budget.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }
    private static async ValueTask<WorkflowDefinitionWriteDisposition> WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(staged, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(staged, path, overwrite: false); return WorkflowDefinitionWriteDisposition.Created; }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await ReadBytesAsync(path, bytes.Length, cancellationToken).ConfigureAwait(false);
                if (existing is null || !existing.AsSpan().SequenceEqual(bytes)) throw new WorkflowDefinitionConflictException(Path.GetFileName(path));
                return WorkflowDefinitionWriteDisposition.AlreadyExists;
            }
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
