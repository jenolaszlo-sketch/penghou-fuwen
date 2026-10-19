namespace Penghou.Fuwen.Compiler;

/// <summary>Opaque in-process admission proof binding executable IR and its exact hierarchy companion.</summary>
public sealed class GroupedWorkflowAdmissionResult
{
    internal GroupedWorkflowAdmissionResult(WorkflowAdmissionResult admission, GroupedWorkflowDefinitionDocument definition)
    { Admission = admission; Definition = definition; }
    /// <summary>The normal catalogue/policy/capability admission.</summary>
    public WorkflowAdmissionResult Admission { get; }
    /// <summary>The exact verified companion bundle covered by this admission.</summary>
    public GroupedWorkflowDefinitionDocument Definition { get; }
    /// <summary>Whether the normal admission succeeded for this bundle's executable definition.</summary>
    public bool Succeeded => Admission.Succeeded && Admission.Receipt?.ExecutionFingerprint == Definition.Definition.ExecutionFingerprint;
}

public sealed partial class WorkflowAdmissionService
{
    /// <summary>Re-admits the pinned expansion without consulting macro source or a macro library.</summary>
    public async ValueTask<GroupedWorkflowAdmissionResult> AdmitGroupedAsync(GroupedWorkflowDefinitionDocument definition,
        CompilationBudget? callerBudget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var verified = GroupedWorkflowDefinitionDocument.LoadVerified(definition.Fingerprint, definition.CanonicalBytes.Span);
        var admission = await AdmitAsync(verified.Definition.ReadPlan(), callerBudget, cancellationToken).ConfigureAwait(false);
        return new(admission, verified);
    }
}
