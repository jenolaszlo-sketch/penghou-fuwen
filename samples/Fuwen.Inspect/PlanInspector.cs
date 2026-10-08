using System.Text.Json;
using Penghou.Fuwen;

namespace Fuwen.Inspect;

/// <summary>
/// Outcome of one inspection. Status is "Succeeded" or "Failed"; Reason
/// carries the integrity/validation failure. Successful results carry the
/// inspected facts; nothing here admits, authorizes, or executes a plan.
/// </summary>
public record InspectRecord(string Status, string Reason);

/// <summary>Integrity verdict for a persisted plan file and claimed fingerprint.</summary>
public sealed record VerifyRecord(
    string Status, string Reason, string ExecutionFingerprint, long CanonicalBytes)
    : InspectRecord(Status, Reason);

/// <summary>Structural validation verdict for a plan file.</summary>
public sealed record ValidateRecord(string Status, string Reason, int NodeCount)
    : InspectRecord(Status, Reason);

/// <summary>One node in the explained plan tree.</summary>
public sealed record ExplainedNode(string Path, string Kind, string Descriptor, string Intent);

/// <summary>One declared descriptor pin (declared, not resolved or admitted).</summary>
public sealed record DeclaredDescriptor(string Kind, string Name, string Version);

/// <summary>One declared capability requirement (declared, not granted).</summary>
public sealed record DeclaredCapability(string Name, string? ScopeClass);

/// <summary>Structural projection of a plan file: declared content only.</summary>
public sealed record ExplainRecord(
    string Status,
    string Reason,
    string Plan,
    string Revision,
    string IrVersion,
    string ExecutionFingerprint,
    IReadOnlyList<ExplainedNode> Nodes,
    IReadOnlyList<DeclaredDescriptor> Descriptors,
    IReadOnlyList<DeclaredCapability> Capabilities,
    int RegionCount,
    int PhaseCount)
    : InspectRecord(Status, Reason);

/// <summary>One declared structural or semantic difference; a null path denotes plan-level semantics.</summary>
public sealed record InspectedChange(string? Path, string Kind);

/// <summary>
/// Explanatory comparison of two verified plan revisions. Differences describe
/// what changed between the revisions; they never assert that prior artifacts
/// are reusable and never authorize execution.
/// </summary>
public sealed record CompareRecord(
    string Status,
    string Reason,
    bool ExecutionFingerprintEqual,
    IReadOnlyList<InspectedChange> Changes)
    : InspectRecord(Status, Reason);

/// <summary>
/// Read-only inspection over persisted canonical plan files. Verify checks
/// integrity against a claimed fingerprint; Validate checks structural
/// invariants; Explain projects declared structure (nodes, intents,
/// descriptor pins, capability manifest). Explaining never compiles,
/// admits, authorizes, or executes: pins and capabilities are reported as
/// declared, never as resolved or granted.
/// </summary>
public static class PlanInspector
{
    public static VerifyRecord Verify(string planPath, string executionFingerprint)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(planPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new VerifyRecord("Failed", "Cannot read plan file: " + error.GetType().Name, "", 0);
        }

        WorkflowDefinitionDocument document;
        try
        {
            document = WorkflowDefinitionDocument.LoadVerified(executionFingerprint, bytes);
        }
        catch (Exception error) when (error is WorkflowDefinitionIntegrityException or ArgumentException)
        {
            return new VerifyRecord("Failed", error.Message, "", bytes.Length);
        }

        return new VerifyRecord("Succeeded", "", document.ExecutionFingerprint, bytes.Length);
    }

    public static ValidateRecord Validate(string planPath)
    {
        WorkflowPlan plan;
        try
        {
            plan = ReadPlan(planPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new ValidateRecord("Failed", error.Message, 0);
        }

        try
        {
            WorkflowPlanValidator.Validate(plan);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new ValidateRecord("Failed", error.Message, 0);
        }

        return new ValidateRecord("Succeeded", "", CountNodes(plan.Nodes));
    }

    public static ExplainRecord Explain(string planPath)
    {
        WorkflowPlan plan;
        try
        {
            plan = ReadPlan(planPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new ExplainRecord("Failed", error.Message, "", "", "", "",
                Array.Empty<ExplainedNode>(), Array.Empty<DeclaredDescriptor>(),
                Array.Empty<DeclaredCapability>(), 0, 0);
        }

        try
        {
            WorkflowPlanValidator.Validate(plan);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new ExplainRecord("Failed", error.Message, "", "", "", "",
                Array.Empty<ExplainedNode>(), Array.Empty<DeclaredDescriptor>(),
                Array.Empty<DeclaredCapability>(), 0, 0);
        }

        var document = WorkflowDefinitionDocument.Create(plan);
        var nodes = new List<ExplainedNode>();
        CollectNodes(plan.Nodes, nodes);
        return new ExplainRecord(
            "Succeeded",
            "",
            plan.Name,
            plan.Revision,
            plan.IrVersion,
            document.ExecutionFingerprint,
            nodes,
            plan.CatalogueBindings
                .Select(descriptor => new DeclaredDescriptor(
                    descriptor.Kind.ToString(), descriptor.Name, descriptor.Version))
                .ToList(),
            plan.CapabilityManifest.Requirements
                .Select(capability => new DeclaredCapability(capability.Name, capability.ScopeClass))
                .ToList(),
            // Validated plans always carry an execution order; validation above enforces it.
            plan.ExecutionOrder!.Regions.Count,
            plan.ExecutionOrder.Regions.Sum(region => region.Phases.Count));
    }

    public static CompareRecord Compare(
        string beforeDefinitionPath,
        string beforeRevisionPath,
        string beforeRevisionFingerprint,
        string afterDefinitionPath,
        string afterRevisionPath,
        string afterRevisionFingerprint)
    {
        try
        {
            var beforeRevision = LoadRevision(beforeRevisionPath, beforeRevisionFingerprint);
            var afterRevision = LoadRevision(afterRevisionPath, afterRevisionFingerprint);
            var beforeDefinition = LoadDefinition(
                beforeDefinitionPath, beforeRevision.ReadEnvelope().ExecutionFingerprint);
            var afterDefinition = LoadDefinition(
                afterDefinitionPath, afterRevision.ReadEnvelope().ExecutionFingerprint);

            var comparison = PlanRevisionComparer.Compare(
                beforeRevision, beforeDefinition, afterRevision, afterDefinition);

            return new CompareRecord(
                "Succeeded",
                "",
                comparison.ExecutionFingerprintEqual,
                comparison.Changes
                    .Select(change => new InspectedChange(change.StructuralPath, change.Kind.ToString()))
                    .ToList());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException or
            PlanRevisionIntegrityException or WorkflowDefinitionIntegrityException)
        {
            return new CompareRecord("Failed", error.Message, false, Array.Empty<InspectedChange>());
        }
    }

    private static PlanRevisionDocument LoadRevision(string revisionPath, string envelopeFingerprint)
    {
        var bytes = File.ReadAllBytes(revisionPath);
        return PlanRevisionDocument.LoadVerified(envelopeFingerprint, bytes);
    }

    private static WorkflowDefinitionDocument LoadDefinition(string definitionPath, string executionFingerprint)
    {
        var bytes = File.ReadAllBytes(definitionPath);
        return WorkflowDefinitionDocument.LoadVerified(executionFingerprint, bytes);
    }

    private static WorkflowPlan ReadPlan(string planPath)
    {
        var bytes = File.ReadAllBytes(planPath);
        return CanonicalJson.Deserialize<WorkflowPlan>(bytes);
    }

    private static int CountNodes(IReadOnlyList<WorkflowNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            foreach (var child in Children(node))
                count += CountNodes(new[] { child });
        }

        return count;
    }

    private static void CollectNodes(IReadOnlyList<WorkflowNode> nodes, List<ExplainedNode> into)
    {
        foreach (var node in nodes)
        {
            into.Add(new ExplainedNode(
                node.StructuralPath,
                KindOf(node),
                node is ActivityNode activity
                    ? activity.Activity.Name + "@" + activity.Activity.Version
                    : "-",
                node is ActivityNode intentNode && intentNode.ExecutionIntent is { } intent
                    ? intent.Profile + "(" + string.Join(";",
                        intent.Required.Select(guarantee => guarantee.Capability + ":" + guarantee.Minimum)) + ")"
                    : "-"));
            foreach (var child in Children(node))
                CollectNodes(new[] { child }, into);
        }
    }

    private static IEnumerable<WorkflowNode> Children(WorkflowNode node) => node switch
    {
        ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
        FanOutNode fanOut => fanOut.Body,
        RepeatNode repeat => repeat.Body,
        _ => Array.Empty<WorkflowNode>()
    };

    private static string KindOf(WorkflowNode node) => node switch
    {
        ActivityNode => "activity",
        ContextNode => "context",
        InferenceNode => "inference",
        ConditionalNode => "conditional",
        ReturnNode => "return",
        FanOutNode => "fanout",
        RepeatNode => "repeat",
        CheckpointNode => "checkpoint",
        WaitNode => "wait",
        _ => node.GetType().Name
    };
}
