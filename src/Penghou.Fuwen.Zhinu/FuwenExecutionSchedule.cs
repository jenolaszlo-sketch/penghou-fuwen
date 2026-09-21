namespace Penghou.Fuwen.Zhinu;

/// <summary>Indexes the admitted execution-order regions and their nodes.</summary>
internal sealed class FuwenExecutionSchedule
{
    private readonly IReadOnlyDictionary<string, WorkflowNode> nodes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> regions;

    internal FuwenExecutionSchedule(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        nodes = Flatten(plan.Nodes).ToDictionary(node => node.StructuralPath, StringComparer.Ordinal);
        regions = plan.ExecutionOrder!.Regions.ToDictionary(
            region => region.RegionPath,
            region => (IReadOnlyList<IReadOnlyList<string>>)region.Phases.Select(phase => phase.NodePaths).ToArray(),
            StringComparer.Ordinal);
    }

    internal IReadOnlyList<IReadOnlyList<string>> GetPhases(string regionPath) =>
        regions.TryGetValue(regionPath, out var phases)
            ? phases
            : throw new FuwenZhinuAdapterException($"The admitted plan has no execution region '{regionPath}'.");

    internal WorkflowNode GetNode(string nodePath) =>
        nodes.TryGetValue(nodePath, out var node)
            ? node
            : throw new FuwenZhinuAdapterException($"The admitted plan has no node '{nodePath}'.");

    private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> source)
    {
        foreach (var node in source)
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => [],
            };
            foreach (var child in Flatten(children))
                yield return child;
        }
    }
}
