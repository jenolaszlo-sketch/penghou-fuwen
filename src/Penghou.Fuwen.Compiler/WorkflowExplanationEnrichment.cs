using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>
/// A bounded summary of a trusted callable used by a compiled workflow.
/// </summary>
/// <remarks>
/// This is descriptive evidence only. It does not grant capability, authorize
/// an invocation, or identify an execution adapter. The descriptor identity and
/// effect claims come from the trusted catalogue consulted by the compiler.
/// </remarks>
public sealed class CallableEffectSummary
{
    private const int MaximumNodePaths = 100_000;
    private readonly IReadOnlyList<string> nodePaths;

    /// <summary>Creates a detached summary for one trusted callable descriptor.</summary>
    public CallableEffectSummary(
        DescriptorReference descriptor,
        CallableEffect effect,
        CallableIdempotency idempotency,
        CallableRetrySafety retrySafety,
        IEnumerable<string> nodePaths)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(nodePaths);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (!Enum.IsDefined(idempotency))
            throw new ArgumentOutOfRangeException(nameof(idempotency));
        if (!Enum.IsDefined(retrySafety))
            throw new ArgumentOutOfRangeException(nameof(retrySafety));

        var descriptorSnapshot = CatalogueContractValidation.SnapshotDescriptor(descriptor);
        Descriptor = new DescriptorReference(
            descriptorSnapshot.Kind,
            descriptorSnapshot.Name,
            descriptorSnapshot.Version,
            new ContentDigest(
                descriptorSnapshot.ContentDigest.Algorithm,
                descriptorSnapshot.ContentDigest.Contract,
                descriptorSnapshot.ContentDigest.Value));
        Effect = effect;
        Idempotency = idempotency;
        RetrySafety = retrySafety;

        var paths = new List<string>();
        using var enumerator = nodePaths.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (paths.Count == MaximumNodePaths)
                throw new InvalidOperationException($"Callable node paths exceed the hard ceiling of {MaximumNodePaths} items.");
            var path = enumerator.Current ?? throw new ArgumentException("A callable node path cannot be null.", nameof(nodePaths));
            if (path.Length == 0 || path.Length > 1024 || path.Any(static c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                throw new ArgumentException("A callable node path is invalid.", nameof(nodePaths));
            paths.Add(new string(path.ToCharArray()));
        }

        if (paths.Count == 0)
            throw new ArgumentException("A callable effect summary must identify at least one workflow node.", nameof(nodePaths));
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new ArgumentException("Callable node paths must be unique.", nameof(nodePaths));
        this.nodePaths = Array.AsReadOnly(paths
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>The exact trusted descriptor identity.</summary>
    public DescriptorReference Descriptor { get; }

    /// <summary>The trusted callable's declared side-effect class.</summary>
    public CallableEffect Effect { get; }

    /// <summary>The trusted callable's declared duplicate-invocation behavior.</summary>
    public CallableIdempotency Idempotency { get; }

    /// <summary>The trusted callable's declared retry behavior.</summary>
    public CallableRetrySafety RetrySafety { get; }

    /// <summary>The deterministic structural paths at which this callable is used.</summary>
    public IReadOnlyList<string> NodePaths => nodePaths;

    /// <summary>The number of callable nodes represented by this summary.</summary>
    public int InvocationCount => nodePaths.Count;
}

/// <summary>Actionable, deterministic guidance associated with one explanation diagnostic.</summary>
public sealed class DiagnosticRepairGuidance
{
    internal DiagnosticRepairGuidance(int diagnosticIndex, CompilerDiagnostic diagnostic, string guidance)
    {
        if (diagnosticIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(diagnosticIndex));
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        Guidance = CompilerContractValidation.Text(guidance, nameof(guidance), 4096, required: true)!;
        DiagnosticIndex = diagnosticIndex;
    }

    /// <summary>The zero-based index of the diagnostic in the explanation.</summary>
    public int DiagnosticIndex { get; }

    /// <summary>The immutable diagnostic for which guidance is provided.</summary>
    public CompilerDiagnostic Diagnostic { get; }

    /// <summary>A provider-neutral correction suggestion.</summary>
    public string Guidance { get; }

    /// <summary>The stable diagnostic code, provided as a convenient index key.</summary>
    public string Code => Diagnostic.Code;

    /// <summary>The diagnostic structural path, when one was supplied.</summary>
    public string? Path => Diagnostic.Path;
}

internal static class WorkflowExplanationEnrichment
{
    internal static IReadOnlyList<CallableEffectSummary> CreateCallableEffectSummaries(
        WorkflowPlan plan,
        IReadOnlyDictionary<DescriptorReference, TrustedCatalogueDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(descriptors);

        var uses = new Dictionary<DescriptorReference, List<string>>();
        Collect(plan.Nodes, uses);
        return Array.AsReadOnly(uses
            .Where(pair => descriptors.TryGetValue(pair.Key, out var descriptor) && descriptor.CallableContract is not null)
            .Select(pair =>
            {
                var descriptor = descriptors[pair.Key];
                var contract = descriptor.CallableContract!;
                return new CallableEffectSummary(
                    descriptor.Descriptor,
                    contract.Effect,
                    contract.Idempotency,
                    contract.RetrySafety,
                    pair.Value);
            })
            .OrderBy(static summary => summary.Descriptor.Kind)
            .ThenBy(static summary => summary.Descriptor.Name, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Descriptor.Version, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Descriptor.ContentDigest.Algorithm, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Descriptor.ContentDigest.Contract, StringComparer.Ordinal)
            .ThenBy(static summary => summary.Descriptor.ContentDigest.Value, StringComparer.Ordinal)
            .ToArray());

        static void Collect(IEnumerable<WorkflowNode> nodes, Dictionary<DescriptorReference, List<string>> uses)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ContextNode context:
                        Add(context.Provider, context.StructuralPath);
                        break;
                    case InferenceNode inference:
                        Add(inference.Profile, inference.StructuralPath);
                        break;
                    case ActivityNode activity:
                        Add(activity.Activity, activity.StructuralPath);
                        break;
                    case ConditionalNode conditional:
                        Collect(conditional.Then, uses);
                        Collect(conditional.Else, uses);
                        break;
                }
            }

            void Add(DescriptorReference descriptor, string path)
            {
                if (!uses.TryGetValue(descriptor, out var paths))
                {
                    paths = [];
                    uses.Add(descriptor, paths);
                }

                paths.Add(path);
            }
        }
    }

    internal static IReadOnlyList<DiagnosticRepairGuidance> CreateRepairGuidance(DiagnosticCollection diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var result = new List<DiagnosticRepairGuidance>();
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var guidance = GuidanceFor(diagnostic);
            if (guidance is not null)
                result.Add(new DiagnosticRepairGuidance(index, diagnostic, guidance));
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static string? GuidanceFor(CompilerDiagnostic diagnostic) => diagnostic.Code switch
    {
        CompilerDiagnosticCodes.SemanticValidationFailed => "Correct the reported workflow contract or structural invariant, then compile the detached plan again.",
        CompilerDiagnosticCodes.BindingReferenceInvalid => "Use an existing structural node path in the valid lexical region, or remove the invalid binding.",
        CompilerDiagnosticCodes.BindingTypeMismatch => "Change the binding or declared type so the source value exactly matches the expected type.",
        CompilerDiagnosticCodes.BindingProjectionInvalid => "Use only valid fields for the source type and keep the projection within its declared shape.",
        CompilerDiagnosticCodes.CapabilityManifestMismatch => "Align the workflow capability manifest with trusted descriptor requirements; changing the manifest does not grant host authority.",
        CompilerDiagnosticCodes.CapabilityNotGranted => "Request the exact capability from host policy or remove the dependent descriptor; an explanation is not an authorization grant.",
        CompilerDiagnosticCodes.CatalogueDescriptorNotFound => "Resolve the exact trusted descriptor identity, or update the plan to reference an available descriptor.",
        CompilerDiagnosticCodes.CatalogueDescriptorDigestMismatch => "Use the digest returned by the trusted catalogue only after independently verifying that it is the intended descriptor.",
        CompilerDiagnosticCodes.CatalogueResolutionInvalidResult => "Fix the trusted catalogue response so its status, requested identity, and descriptor payload agree.",
        CompilerDiagnosticCodes.CatalogueSchemaPayloadMissing => "Publish the exact schema payload for the referenced trusted descriptor, or reference a descriptor that supplies one.",
        CompilerDiagnosticCodes.CatalogueSchemaMismatch => "Make the authored schema match the trusted catalogue payload exactly; the trusted payload remains authoritative.",
        CompilerDiagnosticCodes.CatalogueCallableContractMissing => "Publish a complete trusted callable contract for the referenced descriptor before compiling the node.",
        CompilerDiagnosticCodes.CatalogueCallableContractInvalid => "Correct the trusted callable descriptor kind or contract metadata before compiling the node.",
        CompilerDiagnosticCodes.CatalogueCallableContractReferenceMissing => "Resolve every schema or artifact referenced by the trusted callable contract in the plan's descriptor closure.",
        CompilerDiagnosticCodes.CallableArgumentMissing => "Add the required named argument using the exact name declared by the trusted callable signature.",
        CompilerDiagnosticCodes.CallableArgumentUnknown => "Remove the unknown argument or rename it to an exact parameter from the trusted callable signature.",
        CompilerDiagnosticCodes.CallableArgumentTypeMismatch => "Change the argument binding so its type exactly matches the trusted callable parameter.",
        CompilerDiagnosticCodes.CallableOutputTypeMismatch => "Change the node output type to the trusted callable output type, or select a descriptor with the intended output.",
        CompilerDiagnosticCodes.CallableEffectRejected => "Select a trusted callable whose declared effect is permitted by the compiler's conservative matrix; do not weaken this diagnostic.",
        CompilerDiagnosticCodes.CallableRetryRejected => "Select a trusted callable with an explicitly safe idempotency and retry contract, or defer execution policy to a host adapter.",
        CompilerDiagnosticCodes.ContextSnapshotInvalid or CompilerDiagnosticCodes.ContextRequirementSourceInvalid => "Reference a direct ContextNode output with no unsupported projection.",
        CompilerDiagnosticCodes.ContextSnapshotDuplicate or CompilerDiagnosticCodes.ContextRequirementInvalid => "Declare each context source and requirement name at most once.",
        CompilerDiagnosticCodes.ContextRequirementTypeMismatch => "Make the context requirement type exactly match the referenced ContextNode output type.",
        CompilerDiagnosticCodes.AdmissionCatalogueSnapshotUnavailable => "Configure an immutable trusted-catalogue snapshot revision before requesting host admission.",
        CompilerDiagnosticCodes.AdmissionPolicyRevisionUnavailable => "Configure a versioned finite host capability policy before requesting admission.",
        CompilerDiagnosticCodes.BudgetSourceBytesExceeded or CompilerDiagnosticCodes.BudgetTokensExceeded or CompilerDiagnosticCodes.BudgetAstNodesExceeded or
        CompilerDiagnosticCodes.BudgetNestingDepthExceeded or CompilerDiagnosticCodes.BudgetWorkflowNodesExceeded or CompilerDiagnosticCodes.BudgetSchemasExceeded or
        CompilerDiagnosticCodes.BudgetSchemaDepthExceeded or CompilerDiagnosticCodes.BudgetSchemaFieldsExceeded or CompilerDiagnosticCodes.BudgetExpressionsExceeded or
        CompilerDiagnosticCodes.BudgetStringBytesExceeded or CompilerDiagnosticCodes.BudgetCatalogueLookupsExceeded or
        CompilerDiagnosticCodes.BudgetCatalogueLookupMillisecondsExceeded or CompilerDiagnosticCodes.BudgetCompilationMillisecondsExceeded or
        CompilerDiagnosticCodes.BudgetDiagnosticsExceeded or CompilerDiagnosticCodes.CanonicalDefinitionTooLarge =>
            "Reduce the detached plan or narrow its payload within the reported resource limit, then compile it again.",
        _ => "Review the diagnostic details and correct the reported input before compiling again.",
    };
}
