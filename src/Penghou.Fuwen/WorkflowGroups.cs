using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>Structural provenance of an expanded workflow group.</summary>
public enum WorkflowGroupKind
{
    /// <summary>An authored logical scope.</summary>
    Logical,
    /// <summary>One expanded macro invocation.</summary>
    Macro
}

/// <summary>Definition and invocation references; invocation arguments are not retained here.</summary>
public sealed record WorkflowMacroProvenance(string MacroName, string DefinitionFingerprint, string InvocationKey,
    SourceSpan? DefinitionSource = null, SourceSpan? InvocationSource = null);

/// <summary>A definition-time group, separate from runtime occurrence identity.</summary>
public sealed record WorkflowGroupDefinition(string GroupKey, string? ParentGroupKey, WorkflowGroupKind Kind,
    string DisplayName, int ChildOrdinal, WorkflowMacroProvenance? Provenance = null);

/// <summary>Direct innermost ownership of one expanded executable node.</summary>
public sealed record WorkflowGroupMember(string NodePath, string GroupKey);

/// <summary>An immutable, versioned companion to ordinary executable IR.</summary>
public sealed class WorkflowGroupManifest
{
    /// <summary>Companion encoding contract.</summary>
    public const string ContractVersion = "fuwen-group-manifest/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] bytes;
    private WorkflowGroupManifest(WorkflowGroupDefinition[] groups, WorkflowGroupMember[] members, byte[] bytes)
    {
        Groups = Array.AsReadOnly(groups); Members = Array.AsReadOnly(members); this.bytes = bytes;
        Fingerprint = "sha256:" + ContractVersion + ":" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    /// <summary>Canonical group definitions.</summary>
    public IReadOnlyList<WorkflowGroupDefinition> Groups { get; }
    /// <summary>Canonical direct node membership.</summary>
    public IReadOnlyList<WorkflowGroupMember> Members { get; }
    /// <summary>Identity of the complete companion, separate from execution identity.</summary>
    public string Fingerprint { get; }
    /// <summary>A defensive copy of the canonical companion bytes.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes => bytes.ToArray();

    /// <summary>Freezes and validates a bounded forest and direct ownership map.</summary>
    public static WorkflowGroupManifest Create(IEnumerable<WorkflowGroupDefinition> groups, IEnumerable<WorkflowGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(groups); ArgumentNullException.ThrowIfNull(members);
        var definitions = groups.Take(4097).ToArray();
        var ownership = members.Take(2049).ToArray();
        if (definitions.Length > 4096 || ownership.Length > 2048) throw new ArgumentException("Group companion exceeds its node budget.");
        var byKey = new Dictionary<string, WorkflowGroupDefinition>(StringComparer.Ordinal);
        var ordinals = new HashSet<(string?, int)>();
        foreach (var group in definitions)
        {
            ArgumentNullException.ThrowIfNull(group);
            Text(group.GroupKey, 512); Text(group.DisplayName, 1024);
            if (!Enum.IsDefined(group.Kind) || group.ChildOrdinal < 0 || !byKey.TryAdd(group.GroupKey, group)
                || !ordinals.Add((group.ParentGroupKey, group.ChildOrdinal))) throw new ArgumentException("Invalid or duplicate group definition.");
            if ((group.Kind == WorkflowGroupKind.Macro) != (group.Provenance is not null)) throw new ArgumentException("Only macro groups require provenance.");
            if (group.Provenance is { } macro)
            {
                Text(macro.MacroName, 1024); Text(macro.DefinitionFingerprint, 1024); Text(macro.InvocationKey, 512);
                ValidateSpan(macro.DefinitionSource); ValidateSpan(macro.InvocationSource);
            }
        }
        foreach (var group in definitions)
        {
            var current = group;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!visited.Add(current.GroupKey) || visited.Count > 128) throw new ArgumentException("Group hierarchy is cyclic or too deep.");
                if (current.ParentGroupKey is null) break;
                if (!byKey.TryGetValue(current.ParentGroupKey, out current!)) throw new ArgumentException("Group parent is missing.");
            }
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in ownership)
        {
            ArgumentNullException.ThrowIfNull(member); Text(member.NodePath, 1024); Text(member.GroupKey, 512);
            if (!paths.Add(member.NodePath) || !byKey.ContainsKey(member.GroupKey)) throw new ArgumentException("Invalid or duplicate group membership.");
        }
        definitions = definitions.OrderBy(group => group.GroupKey, StringComparer.Ordinal).ToArray();
        ownership = ownership.OrderBy(member => member.NodePath, StringComparer.Ordinal).ToArray();
        var bytes = CanonicalJson.Serialize(new Envelope(ContractVersion, definitions, ownership));
        if (bytes.Length > 1_048_576) throw new ArgumentException("Group companion exceeds its byte budget.");
        return new(definitions, ownership, bytes);
    }

    internal static WorkflowGroupManifest Restore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1_048_576) throw new WorkflowDefinitionIntegrityException("Group companion exceeds its byte budget.");
        var envelope = CanonicalJson.Deserialize<Envelope>(bytes);
        if (envelope.Version != ContractVersion) throw new WorkflowDefinitionIntegrityException("Unsupported group companion version.");
        var manifest = Create(envelope.Groups, envelope.Members);
        if (!bytes.SequenceEqual(manifest.bytes)) throw new WorkflowDefinitionIntegrityException("Group companion is not canonical.");
        return manifest;
    }
    private static void Text(string text, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (StrictUtf8.GetByteCount(text) > limit || text.Any(char.IsControl)) throw new ArgumentException("Invalid group metadata text.");
    }
    private static void ValidateSpan(SourceSpan? span)
    {
        if (span is null) return;
        Text(span.DocumentId, 1024);
        if (span.StartUtf8ByteOffset < 0 || span.Utf8ByteLength < 0 || span.StartLine < 0 || span.StartUtf16Column < 0)
            throw new ArgumentException("Invalid group source reference.");
    }
    private sealed record Envelope(string Version, IReadOnlyList<WorkflowGroupDefinition> Groups, IReadOnlyList<WorkflowGroupMember> Members);
}

/// <summary>Verified executable IR and a separately pinned immutable hierarchy companion.</summary>
public sealed class GroupedWorkflowDefinitionDocument
{
    private readonly byte[] bytes;
    private GroupedWorkflowDefinitionDocument(WorkflowDefinitionDocument definition, WorkflowGroupManifest groups, byte[] bytes)
    {
        Definition = definition; Groups = groups; this.bytes = bytes;
        Fingerprint = "sha256:fuwen-grouped-definition/v1:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    /// <summary>The ordinary expanded executable definition.</summary>
    public WorkflowDefinitionDocument Definition { get; }
    /// <summary>The exact hierarchy companion.</summary>
    public WorkflowGroupManifest Groups { get; }
    /// <summary>Identity of both admitted components.</summary>
    public string Fingerprint { get; }
    /// <summary>Defensive canonical bytes for persistence/recovery.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes => bytes.ToArray();

    /// <summary>Creates a bundle without changing executable-plan canonical bytes.</summary>
    public static GroupedWorkflowDefinitionDocument Create(WorkflowDefinitionDocument definition, WorkflowGroupManifest groups)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(groups);
        var verified = WorkflowDefinitionDocument.LoadVerified(definition.ExecutionFingerprint, definition.CanonicalBytes.Span);
        var plan = verified.ReadPlan();
        WorkflowPlanValidator.Validate(plan);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<WorkflowNode>(plan.Nodes);
        while (pending.TryPop(out var node))
        {
            paths.Add(node.StructuralPath);
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => []
            };
            foreach (var child in children) pending.Push(child);
        }
        if (groups.Members.Any(member => !paths.Contains(member.NodePath))) throw new ArgumentException("Group companion references an unknown expanded node.");
        if (plan.UsesExpandedGroupScopes)
        {
            var ownership = groups.Members.ToDictionary(member => member.NodePath, StringComparer.Ordinal);
            foreach (var group in groups.Groups)
            {
                var parent = groups.Groups.Where(candidate => group.GroupKey.StartsWith(candidate.GroupKey + "/", StringComparison.Ordinal))
                    .MaxBy(candidate => candidate.GroupKey.Length);
                if (!group.GroupKey.StartsWith(plan.Name + "/", StringComparison.Ordinal) ||
                    !group.GroupKey.Contains("/$group/", StringComparison.Ordinal) || parent?.GroupKey != group.ParentGroupKey)
                    throw new ArgumentException("Group parent does not match the expanded structural scope.");
            }
            foreach (var member in groups.Members)
                if (!member.NodePath.StartsWith(member.GroupKey + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Group ownership does not match the expanded structural scope.");
            foreach (var path in paths.Where(path => path.Contains("/$group/", StringComparison.Ordinal)))
            {
                var innermost = groups.Groups.Where(group => path.StartsWith(group.GroupKey + "/", StringComparison.Ordinal))
                    .MaxBy(group => group.GroupKey.Length);
                if (innermost is null || !ownership.TryGetValue(path, out var member) || member.GroupKey != innermost.GroupKey)
                    throw new ArgumentException("Expanded group scope has no exact innermost companion membership.");
            }
        }
        using var planJson = JsonDocument.Parse(verified.CanonicalBytes);
        using var groupsJson = JsonDocument.Parse(groups.CanonicalBytes);
        var bytes = CanonicalJson.Serialize(new Envelope("fuwen-grouped-definition/v1", verified.ExecutionFingerprint,
            planJson.RootElement.Clone(), groupsJson.RootElement.Clone()));
        if (bytes.Length > FuwenContracts.MaximumCanonicalPlanBytes + 1_048_576) throw new ArgumentException("Grouped definition exceeds its byte budget.");
        return new(verified, groups, bytes);
    }

    /// <summary>Loads pinned expansion bytes without a source compiler or macro library.</summary>
    public static GroupedWorkflowDefinitionDocument LoadVerified(string fingerprint, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > FuwenContracts.MaximumCanonicalPlanBytes + 1_048_576) throw new WorkflowDefinitionIntegrityException("Grouped definition exceeds its byte budget.");
        try
        {
            var envelope = CanonicalJson.Deserialize<Envelope>(bytes);
            if (envelope.Version != "fuwen-grouped-definition/v1") throw new WorkflowDefinitionIntegrityException("Unsupported grouped definition version.");
            var definition = WorkflowDefinitionDocument.LoadVerified(envelope.ExecutionFingerprint, CanonicalJson.Canonicalize(envelope.Plan));
            var groups = WorkflowGroupManifest.Restore(CanonicalJson.Canonicalize(envelope.Groups));
            var verified = Create(definition, groups);
            if (verified.Fingerprint != fingerprint || !bytes.SequenceEqual(verified.bytes)) throw new WorkflowDefinitionIntegrityException("Grouped definition identity mismatch.");
            return verified;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        { throw new WorkflowDefinitionIntegrityException("Grouped definition is malformed or unsupported."); }
    }
    private sealed record Envelope(string Version, string ExecutionFingerprint, JsonElement Plan, JsonElement Groups);
}
