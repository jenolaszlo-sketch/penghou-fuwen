using System.Security.Cryptography;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>
/// Opaque in-process proof that one exact canonical definition passed host
/// admission under identified catalogue, policy, grant, and budget inputs.
/// This is not a signed cross-process credential.
/// </summary>
public sealed class WorkflowAdmissionReceipt
{
    internal WorkflowAdmissionReceipt(
        string executionFingerprint,
        string catalogueSnapshotRevision,
        string resolvedDescriptorSetFingerprint,
        string capabilityPolicyRevision,
        string capabilityGrantFingerprint,
        CompilationBudget effectiveBudget)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        ExecutionFingerprint = executionFingerprint;
        CatalogueSnapshotRevision = Text(catalogueSnapshotRevision, nameof(catalogueSnapshotRevision));
        ResolvedDescriptorSetFingerprint = Text(resolvedDescriptorSetFingerprint, nameof(resolvedDescriptorSetFingerprint));
        CapabilityPolicyRevision = Text(capabilityPolicyRevision, nameof(capabilityPolicyRevision));
        CapabilityGrantFingerprint = Text(capabilityGrantFingerprint, nameof(capabilityGrantFingerprint));
        EffectiveBudget = effectiveBudget ?? throw new ArgumentNullException(nameof(effectiveBudget));
        ReceiptFingerprint = ComputeFingerprint(new AdmissionClaims(
            ExecutionFingerprint,
            CatalogueSnapshotRevision,
            ResolvedDescriptorSetFingerprint,
            CapabilityPolicyRevision,
            CapabilityGrantFingerprint,
            BudgetClaims.From(EffectiveBudget)));
    }

    /// <summary>Identity of the immutable admission claims.</summary>
    public string ReceiptFingerprint { get; }

    /// <summary>Exact canonical workflow definition admitted by the host.</summary>
    public string ExecutionFingerprint { get; }

    /// <summary>Revision of the immutable trusted-catalogue snapshot used.</summary>
    public string CatalogueSnapshotRevision { get; }

    /// <summary>Fingerprint of the exact resolved descriptors and trusted metadata used.</summary>
    public string ResolvedDescriptorSetFingerprint { get; }

    /// <summary>Revision of the host authorization policy used.</summary>
    public string CapabilityPolicyRevision { get; }

    /// <summary>Fingerprint of the exact finite capability grants used.</summary>
    public string CapabilityGrantFingerprint { get; }

    /// <summary>Effective compilation/admission limits after caller narrowing.</summary>
    public CompilationBudget EffectiveBudget { get; }

    private static string Text(string value, string name)
    {
        var result = CompilerContractValidation.Text(value, name, 512, required: true)!;
        return !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new ArgumentException("The admission identity cannot be whitespace.", name);
    }

    private static string ComputeFingerprint(AdmissionClaims claims)
    {
        var hash = SHA256.HashData(CanonicalJson.Serialize(claims));
        return $"sha256:fuwen-admission/v1:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed record AdmissionClaims(
        string ExecutionFingerprint,
        string CatalogueSnapshotRevision,
        string ResolvedDescriptorSetFingerprint,
        string CapabilityPolicyRevision,
        string CapabilityGrantFingerprint,
        BudgetClaims EffectiveBudget);

    private sealed record BudgetClaims(
        long MaxSourceBytes,
        long MaxTokens,
        long MaxAstNodes,
        int MaxNestingDepth,
        int MaxWorkflowNodes,
        int MaxSchemas,
        int MaxSchemaDepth,
        int MaxSchemaFields,
        int MaxExpressions,
        long MaxStringBytes,
        int MaxDiagnostics,
        int MaxCatalogueLookups,
        long MaxCatalogueLookupMilliseconds,
        long MaxCompilationMilliseconds)
    {
        internal static BudgetClaims From(CompilationBudget value) => new(
            value.MaxSourceBytes,
            value.MaxTokens,
            value.MaxAstNodes,
            value.MaxNestingDepth,
            value.MaxWorkflowNodes,
            value.MaxSchemas,
            value.MaxSchemaDepth,
            value.MaxSchemaFields,
            value.MaxExpressions,
            value.MaxStringBytes,
            value.MaxDiagnostics,
            value.MaxCatalogueLookups,
            value.MaxCatalogueLookupMilliseconds,
            value.MaxCompilationMilliseconds);
    }
}

/// <summary>Compilation plus the optional opaque host-admission receipt.</summary>
public sealed class WorkflowAdmissionResult
{
    internal WorkflowAdmissionResult(
        CompilationResult compilation,
        WorkflowAdmissionReceipt? receipt,
        IEnumerable<CompilerDiagnostic> admissionDiagnostics)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        Receipt = receipt;
        var combined = compilation.Diagnostics.Concat(admissionDiagnostics ?? throw new ArgumentNullException(nameof(admissionDiagnostics)));
        Diagnostics = new DiagnosticCollection(combined, compilation.Budget.MaxDiagnostics);
    }

    /// <summary>The ordinary semantic compilation result.</summary>
    public CompilationResult Compilation { get; }

    /// <summary>The receipt when every admission authority carried stable identity.</summary>
    public WorkflowAdmissionReceipt? Receipt { get; }

    /// <summary>Bounded compilation and admission diagnostics.</summary>
    public DiagnosticCollection Diagnostics { get; }

    /// <summary>True only when compilation succeeded and a receipt was issued.</summary>
    public bool Succeeded => Compilation.Succeeded && Receipt is not null && !Diagnostics.HasErrors;
}

/// <summary>Issues an opaque receipt from the exact resolution trace produced by one compiler invocation.</summary>
public sealed class WorkflowAdmissionService
{
    private readonly WorkflowCompiler compiler;

    /// <summary>Creates an admission boundary over one configured compiler.</summary>
    public WorkflowAdmissionService(WorkflowCompiler compiler)
    {
        this.compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    /// <summary>Compiles and admits a programmatic plan without repeating catalogue resolution.</summary>
    public async ValueTask<WorkflowAdmissionResult> AdmitAsync(
        WorkflowPlan plan,
        CompilationBudget? callerBudget = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await compiler.CompileAsync(plan, callerBudget, cancellationToken).ConfigureAwait(false);
        if (!compilation.Succeeded || compilation.AdmissionEvidence is null)
            return new WorkflowAdmissionResult(compilation, null, []);

        var evidence = compilation.AdmissionEvidence;
        var diagnostics = new List<CompilerDiagnostic>();
        if (evidence.CatalogueSnapshotRevision is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.AdmissionCatalogueSnapshotUnavailable,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Admission,
                "The trusted catalogue does not expose an immutable snapshot revision."));
        }
        if (!evidence.PolicyCanIssueReceipt || evidence.CapabilityPolicyRevision is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.AdmissionPolicyRevisionUnavailable,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Admission,
                "The capability policy is unversioned or uses the test-only allow-all mode."));
        }
        if (diagnostics.Count != 0)
            return new WorkflowAdmissionResult(compilation, null, diagnostics);

        var receipt = new WorkflowAdmissionReceipt(
            compilation.Definition!.ExecutionFingerprint,
            evidence.CatalogueSnapshotRevision!,
            evidence.ResolvedDescriptorSetFingerprint,
            evidence.CapabilityPolicyRevision!,
            evidence.CapabilityGrantFingerprint,
            compilation.Budget);
        return new WorkflowAdmissionResult(compilation, receipt, diagnostics);
    }
}

internal sealed record CompilationAdmissionEvidence(
    string? CatalogueSnapshotRevision,
    string ResolvedDescriptorSetFingerprint,
    string? CapabilityPolicyRevision,
    string CapabilityGrantFingerprint,
    bool PolicyCanIssueReceipt);
