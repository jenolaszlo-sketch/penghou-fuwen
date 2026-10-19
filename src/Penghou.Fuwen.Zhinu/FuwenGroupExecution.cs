using System.Text.Json;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Attaches admitted static scopes to native run-local occurrences.</summary>
internal sealed class FuwenGroupExecution
{
    private readonly WorkflowContext workflowContext;
    private readonly Dictionary<string, WorkflowNode> nodes;
    private readonly Dictionary<string, RepeatNode> repeatsByKey;
    private readonly Dictionary<string, WorkflowGroupDefinition> definitions;
    private readonly Dictionary<string, string> ownership;
    private readonly Dictionary<string, RepeatNode?> enclosingRepeats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivityGroupOccurrence> rootOccurrences = new(StringComparer.Ordinal);
    private readonly Dictionary<WorkflowLoopIteration<JsonElement>, Dictionary<string, ActivityGroupOccurrence>> iterationOccurrences = new();

    internal FuwenGroupExecution(WorkflowGroupManifest manifest, WorkflowPlan plan, WorkflowContext context)
    {
        workflowContext = context;
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plan);
        definitions = manifest.Groups.ToDictionary(group => group.GroupKey, StringComparer.Ordinal);
        ownership = manifest.Members.ToDictionary(member => member.NodePath, member => member.GroupKey, StringComparer.Ordinal);

        nodes = EnumerateNodes(plan.Nodes).ToDictionary(node => node.StructuralPath, StringComparer.Ordinal);
        var repeats = nodes.Values.OfType<RepeatNode>().ToArray();
        repeatsByKey = repeats.ToDictionary(repeat => FuwenZhinuStepKeys.RepeatLoopName(repeat, true), StringComparer.Ordinal);
        foreach (var group in manifest.Groups)
        {
            enclosingRepeats.Add(group.GroupKey, repeats
                .Where(repeat => group.GroupKey.StartsWith(repeat.StructuralPath + "/$body/", StringComparison.Ordinal))
                .MaxBy(repeat => repeat.StructuralPath.Length));
        }
    }

    internal static ActivityGroupManifest Translate(WorkflowGroupManifest manifest) => ActivityGroupManifest.Create(
        manifest.Groups.Select(group => new ActivityGroupDefinition(group.GroupKey,
            group.Kind == WorkflowGroupKind.Macro ? ActivityGroupKind.Macro : ActivityGroupKind.Logical,
            group.DisplayName, group.ParentGroupKey, group.ChildOrdinal,
            group.Provenance is { } provenance ? new ActivityGroupMacroProvenance(provenance.MacroName,
                provenance.DefinitionFingerprint, provenance.InvocationKey) : null)));

    internal StepOptions? Options(WorkflowContext context, string nodePath, StepOptions? options, ActivityGroupMemberRole role)
    {
        if (options?.DependsOn is { } dependencies && repeatsByKey.Count != 0)
            options = options with { DependsOn = RootDependencies(dependencies, nodePath) };
        if (!ownership.TryGetValue(nodePath, out var groupKey)) return options;
        if (enclosingRepeats.TryGetValue(groupKey, out var repeat) && repeat is not null)
            throw new InvalidOperationException($"Group '{groupKey}' requires repeat '{repeat.Name}' occurrence scope.");
        var occurrence = GetRootOccurrence(context, groupKey);
        return (options ?? new StepOptions()) with { Group = new ActivityGroupStepBinding(new ActivityGroupMembership(groupKey, role), occurrence) };
    }

    internal StepOptions? Options(WorkflowContext context, WorkflowLoopIteration<JsonElement> iteration,
        string nodePath, StepOptions? options, ActivityGroupMemberRole role)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        if (options?.DependsOn is { } dependencies)
        {
            var repeat = repeatsByKey[iteration.LoopKey];
            options = options with
            {
                DependsOn = dependencies.Select(path =>
            {
                if (!path.StartsWith(repeat.StructuralPath + "/$body/", StringComparison.Ordinal)) return ResolveRootDependency(path, nodePath);
                var suffix = nodes[path] is ConditionalNode
                    ? FuwenZhinuStepKeys.RepeatMergeStepName(path, repeat.StructuralPath, true)
                    : FuwenZhinuStepKeys.RepeatBodyStepName(path, repeat.StructuralPath, true);
                return FuwenZhinuStepKeys.LoopBody(FuwenZhinuStepKeys.RootLoopPath(iteration.LoopKey), iteration.Iteration, suffix);
            }).ToArray()
            };
        }
        if (!ownership.TryGetValue(nodePath, out var groupKey)) return options;
        var occurrence = GetOccurrence(context, iteration, groupKey);
        return (options ?? new StepOptions()) with { Group = new ActivityGroupStepBinding(new ActivityGroupMembership(groupKey, role), occurrence) };
    }

    internal StepOptions? Options(WorkflowLoopIteration<JsonElement> iteration, string nodePath,
        StepOptions? options, ActivityGroupMemberRole role) => Options(workflowContext, iteration, nodePath, options, role);

    internal bool HasRepeats => repeatsByKey.Count != 0;

    internal IReadOnlyCollection<string>? RootDependencies(IReadOnlyCollection<string>? paths, string consumerPath) =>
        paths?.Select(path => ResolveRootDependency(path, consumerPath)).ToArray();

    private string ResolveRootDependency(string path, string consumerPath) => nodes.TryGetValue(path, out var node) ? node switch
    {
        RepeatNode repeat => FuwenZhinuStepKeys.RepeatLoopName(repeat, true),
        // Descendants inherit the decision as a control edge; the merge is
        // not available until that selected branch has returned.
        ConditionalNode { Merge: not null } when consumerPath != path &&
            !consumerPath.StartsWith(path + "/$then/", StringComparison.Ordinal) &&
            !consumerPath.StartsWith(path + "/$else/", StringComparison.Ordinal) => FuwenZhinuStepKeys.ConditionalMerge(path),
        _ => path
    } : path;

    internal void EndIteration(WorkflowLoopIteration<JsonElement> iteration) => iterationOccurrences.Remove(iteration);

    private ActivityGroupOccurrence GetOccurrence(WorkflowContext context,
        WorkflowLoopIteration<JsonElement> iteration, string key)
    {
        if (!enclosingRepeats.TryGetValue(key, out var repeat) || repeat is null)
            return GetRootOccurrence(context, key);
        if (!string.Equals(iteration.LoopKey, FuwenZhinuStepKeys.RepeatLoopName(repeat, true), StringComparison.Ordinal))
            throw new InvalidOperationException($"Group '{key}' does not belong to repeat '{iteration.LoopKey}'.");

        if (!iterationOccurrences.TryGetValue(iteration, out var occurrences))
        {
            occurrences = new Dictionary<string, ActivityGroupOccurrence>(StringComparer.Ordinal);
            iterationOccurrences.Add(iteration, occurrences);
        }
        if (occurrences.TryGetValue(key, out var occurrence)) return occurrence;
        var definition = definitions[key];
        var parent = definition.ParentGroupKey is { } parentKey ? GetOccurrence(context, iteration, parentKey) : null;
        occurrence = iteration.GetActivityGroup(key, parent);
        occurrences.Add(key, occurrence);
        return occurrence;
    }

    private ActivityGroupOccurrence GetRootOccurrence(WorkflowContext context, string key)
    {
        if (enclosingRepeats.TryGetValue(key, out var repeat) && repeat is not null)
            throw new InvalidOperationException($"Group '{key}' requires repeat '{repeat.Name}' occurrence scope.");
        if (rootOccurrences.TryGetValue(key, out var occurrence)) return occurrence;
        var definition = definitions[key];
        var parent = definition.ParentGroupKey is { } parentKey ? GetRootOccurrence(context, parentKey) : null;
        occurrence = context.GetActivityGroup(key, parent);
        rootOccurrences.Add(key, occurrence);
        return occurrence;
    }

    private static IEnumerable<WorkflowNode> EnumerateNodes(IEnumerable<WorkflowNode> roots)
    {
        var pending = new Stack<WorkflowNode>(roots);
        while (pending.TryPop(out var node))
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => [],
            };
            foreach (var child in children) pending.Push(child);
        }
    }
}
