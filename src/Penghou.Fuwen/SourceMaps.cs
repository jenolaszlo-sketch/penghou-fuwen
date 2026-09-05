namespace Penghou.Fuwen;

/// <summary>An immutable source document identity without a storage path.</summary>
public sealed record SourceDocumentReference(
    string DocumentId,
    string LogicalName,
    ContentDigest ContentDigest,
    long Utf8ByteLength);

/// <summary>A zero-based UTF-8 byte span with a display-oriented UTF-16 location.</summary>
public sealed record SourceSpan(
    string DocumentId,
    long StartUtf8ByteOffset,
    long Utf8ByteLength,
    int StartLine,
    int StartUtf16Column);

/// <summary>Maps a structural executable identity to authored source.</summary>
public sealed record SourceMapEntry(string StructuralPath, SourceSpan Span);

/// <summary>
/// Debug metadata bound to source and execution identities but excluded from
/// canonical executable-plan bytes.
/// </summary>
public sealed record WorkflowSourceMap(
    string ExecutionFingerprint,
    ContentDigest SourceFingerprint,
    IReadOnlyList<SourceDocumentReference> Documents,
    IReadOnlyList<SourceMapEntry> Entries);

/// <summary>Validates source-map integrity independently from a workflow plan.</summary>
public static class WorkflowSourceMapValidator
{
    /// <summary>Rejects malformed spans, missing documents, and duplicate node mappings.</summary>
    public static void Validate(WorkflowSourceMap sourceMap)
    {
        ArgumentNullException.ThrowIfNull(sourceMap);
        RequireText(sourceMap.ExecutionFingerprint, nameof(sourceMap.ExecutionFingerprint));
        ValidateDigest(sourceMap.SourceFingerprint);

        var documents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in sourceMap.Documents)
        {
            RequireText(document.DocumentId, nameof(document.DocumentId));
            RequireText(document.LogicalName, nameof(document.LogicalName));
            ValidateDigest(document.ContentDigest);
            if (document.Utf8ByteLength < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceMap), "Source document length cannot be negative.");
            if (!documents.Add(document.DocumentId))
                throw new ArgumentException($"Duplicate source document '{document.DocumentId}'.", nameof(sourceMap));
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sourceMap.Entries)
        {
            RequireText(entry.StructuralPath, nameof(entry.StructuralPath));
            if (!paths.Add(entry.StructuralPath))
                throw new ArgumentException($"Duplicate source mapping for '{entry.StructuralPath}'.", nameof(sourceMap));
            if (!documents.Contains(entry.Span.DocumentId))
                throw new ArgumentException($"Source document '{entry.Span.DocumentId}' is not declared.", nameof(sourceMap));
            if (entry.Span.StartUtf8ByteOffset < 0 || entry.Span.Utf8ByteLength < 0 || entry.Span.StartLine < 0 || entry.Span.StartUtf16Column < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceMap), "Source span coordinates cannot be negative.");
            var documentLength = sourceMap.Documents.Single(document => document.DocumentId == entry.Span.DocumentId).Utf8ByteLength;
            if (entry.Span.StartUtf8ByteOffset > documentLength ||
                entry.Span.Utf8ByteLength > documentLength - entry.Span.StartUtf8ByteOffset)
                throw new ArgumentOutOfRangeException(nameof(sourceMap), "Source span exceeds its document revision.");
        }
    }

    /// <summary>Validates a source map and its binding to one exact executable plan.</summary>
    public static void ValidateForPlan(WorkflowSourceMap sourceMap, WorkflowPlan plan)
    {
        Validate(sourceMap);
        ArgumentNullException.ThrowIfNull(plan);
        var expectedFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(plan);
        if (!string.Equals(sourceMap.ExecutionFingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Source map execution fingerprint does not match the workflow plan.", nameof(sourceMap));

        var nodePaths = EnumerateNodes(plan.Nodes).Select(node => node.StructuralPath).ToHashSet(StringComparer.Ordinal);
        var unknown = sourceMap.Entries.FirstOrDefault(entry => !nodePaths.Contains(entry.StructuralPath));
        if (unknown is not null)
            throw new ArgumentException($"Source mapping references unknown node '{unknown.StructuralPath}'.", nameof(sourceMap));
    }

    private static IEnumerable<WorkflowNode> EnumerateNodes(IEnumerable<WorkflowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is not ConditionalNode conditional)
                continue;
            foreach (var child in EnumerateNodes(conditional.Then.Concat(conditional.Else)))
                yield return child;
        }
    }

    private static void ValidateDigest(ContentDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        RequireText(digest.Algorithm, nameof(digest.Algorithm));
        RequireText(digest.Contract, nameof(digest.Contract));
        RequireText(digest.Value, nameof(digest.Value));
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
    }
}
