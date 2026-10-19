using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Attaches admitted static scopes to native run-local occurrences.</summary>
internal sealed class FuwenGroupExecution(WorkflowGroupManifest manifest)
{
    private readonly Dictionary<string, WorkflowGroupDefinition> definitions = manifest.Groups.ToDictionary(group => group.GroupKey, StringComparer.Ordinal);
    private readonly Dictionary<string, string> ownership = manifest.Members.ToDictionary(member => member.NodePath, member => member.GroupKey, StringComparer.Ordinal);
    private readonly Dictionary<string, ActivityGroupOccurrence> occurrences = new(StringComparer.Ordinal);

    internal static ActivityGroupManifest Translate(WorkflowGroupManifest manifest) => ActivityGroupManifest.Create(
        manifest.Groups.Select(group => new ActivityGroupDefinition(group.GroupKey,
            group.Kind == WorkflowGroupKind.Macro ? ActivityGroupKind.Macro : ActivityGroupKind.Logical,
            group.DisplayName, group.ParentGroupKey, group.ChildOrdinal,
            group.Provenance is { } provenance ? new ActivityGroupMacroProvenance(provenance.MacroName,
                provenance.DefinitionFingerprint, provenance.InvocationKey) : null)));

    internal StepOptions? Options(WorkflowContext context, string nodePath, StepOptions? options, ActivityGroupMemberRole role)
    {
        if (!ownership.TryGetValue(nodePath, out var groupKey)) return options;
        var occurrence = GetOccurrence(context, groupKey);
        return (options ?? new StepOptions()) with { Group = new ActivityGroupStepBinding(new ActivityGroupMembership(groupKey, role), occurrence) };
    }

    private ActivityGroupOccurrence GetOccurrence(WorkflowContext context, string key)
    {
        if (occurrences.TryGetValue(key, out var occurrence)) return occurrence;
        var definition = definitions[key];
        var parent = definition.ParentGroupKey is { } parentKey ? GetOccurrence(context, parentKey) : null;
        occurrence = context.GetActivityGroup(key, parent);
        occurrences.Add(key, occurrence);
        return occurrence;
    }
}
