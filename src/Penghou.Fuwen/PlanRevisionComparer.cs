namespace Penghou.Fuwen;

/// <summary>A deterministic semantic difference between two validated planning records.</summary>
public enum PlanChangeKind
{
    /// <summary>The structural element and its dependencies are identical.</summary>
    Unchanged,

    /// <summary>A structural path exists only in the later plan.</summary>
    Added,

    /// <summary>A structural path exists only in the earlier plan.</summary>
    Removed,

    /// <summary>The element's own executable semantics changed.</summary>
    Changed,

    /// <summary>The element's inputs, control membership, or dependencies changed.</summary>
    DependencyChanged,

    /// <summary>The revision objective digest changed.</summary>
    ObjectiveChanged,

    /// <summary>The revision acceptance-criteria digest changed.</summary>
    AcceptanceCriteriaChanged,

    /// <summary>The revision validation-requirements digest changed.</summary>
    ValidationRequirementChanged,
}

/// <summary>One bounded comparison result; a null path denotes workflow-level semantics.</summary>
public sealed record PlanChange(string? StructuralPath, PlanChangeKind Kind);

/// <summary>Explainable differences without any assertion that prior artifacts are reusable.</summary>
public sealed record PlanRevisionComparison
{
    /// <summary>Creates an immutable comparison result.</summary>
    /// <param name="executionFingerprintEqual">Whether both definitions have the same executable identity.</param>
    /// <param name="changes">The bounded, deterministic change sequence.</param>
    public PlanRevisionComparison(bool executionFingerprintEqual, IReadOnlyList<PlanChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Any(static change => change is null))
            throw new ArgumentException("Changes cannot contain null entries.", nameof(changes));
        ExecutionFingerprintEqual = executionFingerprintEqual;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Whether both definitions have the same executable identity.</summary>
    public bool ExecutionFingerprintEqual { get; }

    /// <summary>The immutable, deterministic change sequence.</summary>
    public IReadOnlyList<PlanChange> Changes { get; }
}

/// <summary>Compares verified immutable definitions and their lineage semantics.</summary>
public static class PlanRevisionComparer
{
    /// <summary>Compares two verified plan revisions using exact structural-path correspondence.</summary>
    /// <param name="beforeRevision">The earlier immutable lineage record.</param>
    /// <param name="beforeDefinition">The verified definition referenced by <paramref name="beforeRevision"/>.</param>
    /// <param name="afterRevision">The later immutable lineage record.</param>
    /// <param name="afterDefinition">The verified definition referenced by <paramref name="afterRevision"/>.</param>
    /// <returns>A deterministic, bounded description of semantic differences.</returns>
    /// <remarks>The result describes differences only. It never authorizes reuse of runtime artifacts.</remarks>
    public static PlanRevisionComparison Compare(
        PlanRevisionDocument beforeRevision,
        WorkflowDefinitionDocument beforeDefinition,
        PlanRevisionDocument afterRevision,
        WorkflowDefinitionDocument afterDefinition)
    {
        ArgumentNullException.ThrowIfNull(beforeRevision);
        ArgumentNullException.ThrowIfNull(beforeDefinition);
        ArgumentNullException.ThrowIfNull(afterRevision);
        ArgumentNullException.ThrowIfNull(afterDefinition);
        var beforeEnvelope = beforeRevision.ReadEnvelope();
        var afterEnvelope = afterRevision.ReadEnvelope();
        RequireBinding(beforeEnvelope, beforeDefinition, nameof(beforeDefinition));
        RequireBinding(afterEnvelope, afterDefinition, nameof(afterDefinition));
        var before = ReadValidatedPlan(beforeDefinition, nameof(beforeDefinition));
        var after = ReadValidatedPlan(afterDefinition, nameof(afterDefinition));
        var changes = new List<PlanChange>();

        AddSemanticChange(changes, beforeEnvelope.Semantics.Objective, afterEnvelope.Semantics.Objective, PlanChangeKind.ObjectiveChanged);
        AddSemanticChange(changes, beforeEnvelope.Semantics.AcceptanceCriteria, afterEnvelope.Semantics.AcceptanceCriteria, PlanChangeKind.AcceptanceCriteriaChanged);
        AddSemanticChange(changes, beforeEnvelope.Semantics.ValidationRequirements, afterEnvelope.Semantics.ValidationRequirements, PlanChangeKind.ValidationRequirementChanged);
        if (!BytesEqual(RootSemantics(before), RootSemantics(after)))
            changes.Add(new PlanChange(null, PlanChangeKind.Changed));
        if (!BytesEqual(before.ExecutionOrder, after.ExecutionOrder))
            changes.Add(new PlanChange(null, PlanChangeKind.DependencyChanged));

        var beforeNodes = Flatten(before.Nodes).ToDictionary(static node => node.StructuralPath, StringComparer.Ordinal);
        var afterNodes = Flatten(after.Nodes).ToDictionary(static node => node.StructuralPath, StringComparer.Ordinal);
        foreach (var path in beforeNodes.Keys.Concat(afterNodes.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!beforeNodes.TryGetValue(path, out var oldNode))
                changes.Add(new PlanChange(path, PlanChangeKind.Added));
            else if (!afterNodes.TryGetValue(path, out var newNode))
                changes.Add(new PlanChange(path, PlanChangeKind.Removed));
            else if (!BytesEqual(NodeSemantics(oldNode), NodeSemantics(newNode)))
                changes.Add(new PlanChange(path, PlanChangeKind.Changed));
            else if (!BytesEqual(NodeDependencies(oldNode), NodeDependencies(newNode)))
                changes.Add(new PlanChange(path, PlanChangeKind.DependencyChanged));
            else
                changes.Add(new PlanChange(path, PlanChangeKind.Unchanged));
        }
        return new PlanRevisionComparison(
            string.Equals(beforeDefinition.ExecutionFingerprint, afterDefinition.ExecutionFingerprint, StringComparison.Ordinal),
            Array.AsReadOnly(changes.ToArray()));
    }

    private static void RequireBinding(PlanRevisionEnvelope envelope, WorkflowDefinitionDocument definition, string name)
    {
        if (!string.Equals(envelope.ExecutionFingerprint, definition.ExecutionFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Plan revision does not reference the supplied verified definition.", name);
    }

    private static WorkflowPlan ReadValidatedPlan(WorkflowDefinitionDocument definition, string name)
    {
        try
        {
            var plan = definition.ReadPlan();
            WorkflowPlanValidator.Validate(plan);
            return plan;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new ArgumentException("Workflow definition is not a structurally valid executable plan.", name, exception);
        }
    }

    private static void AddSemanticChange(List<PlanChange> changes, ContentDigest before, ContentDigest after, PlanChangeKind kind)
    {
        if (!Equals(before, after))
            changes.Add(new PlanChange(null, kind));
    }

    private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is ConditionalNode conditional)
                foreach (var child in Flatten(conditional.Then.Concat(conditional.Else)))
                    yield return child;
        }
    }

    private static object RootSemantics(WorkflowPlan plan) => new
    {
        plan.IrVersion,
        plan.LanguageVersion,
        plan.CompilerSemanticVersion,
        plan.CanonicalJsonVersion,
        plan.FingerprintVersion,
        plan.Name,
        plan.Revision,
        plan.InputType,
        plan.OutputType,
        plan.RoutingPolicyRevision,
        plan.Schemas,
        plan.CatalogueBindings,
        plan.CapabilityManifest,
    };

    private static object NodeSemantics(WorkflowNode node) => node switch
    {
        ContextNode value => new { Kind = "context", value.Provider, value.OutputType },
        InferenceNode value => new { Kind = "inference", value.Profile, value.PromptTemplate, value.OutputType },
        ActivityNode value => new { Kind = "activity", value.Activity, value.OutputType },
        ConditionalNode value => new { Kind = "conditional", value.Condition.Operator },
        ReturnNode => new { Kind = "return" },
        _ => throw new NotSupportedException($"Unsupported workflow node '{node.GetType().Name}'."),
    };

    private static object NodeDependencies(WorkflowNode node) => node switch
    {
        ContextNode value => new { value.Arguments },
        InferenceNode value => new { value.Arguments, value.ContextSnapshots },
        ActivityNode value => new { value.Arguments },
        ConditionalNode value => new
        {
            value.Condition.Left,
            value.Condition.Right,
            Then = value.Then.Select(static child => child.StructuralPath).ToArray(),
            Else = value.Else.Select(static child => child.StructuralPath).ToArray(),
        },
        ReturnNode value => new { value.Value },
        _ => throw new NotSupportedException($"Unsupported workflow node '{node.GetType().Name}'."),
    };

    private static bool BytesEqual<T>(T left, T right) =>
        CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));
}
