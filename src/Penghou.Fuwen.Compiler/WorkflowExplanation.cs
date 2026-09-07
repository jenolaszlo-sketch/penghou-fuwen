using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>
/// A deterministic, side-effect-free projection of an existing compilation or
/// admission result for humans, tools, and model-assisted correction.
/// </summary>
/// <remarks>
/// Creating an explanation never compiles a plan, resolves a catalogue, issues
/// a receipt, or executes workflow work. A compilation may have consulted the
/// configured trusted catalogue before the result was created; this type only
/// projects that already-produced result.
/// </remarks>
public sealed class WorkflowExplanation
{
    private readonly WorkflowDefinitionDocument? definition;
    private readonly IReadOnlyList<DescriptorReference> resolvedDescriptorPins;
    private readonly IReadOnlyList<CapabilityRequirement> requiredCapabilities;

    private WorkflowExplanation(
        CompilationResult compilation,
        DiagnosticCollection diagnostics,
        bool admissionEvaluated,
        bool admissionSucceeded)
    {
        Compilation = compilation;
        Diagnostics = diagnostics;
        AdmissionEvaluated = admissionEvaluated;
        AdmissionSucceeded = admissionSucceeded;
        CompilationSucceeded = compilation.Succeeded;
        definition = compilation.Definition;
        EffectiveBudget = compilation.Budget;
        Usage = compilation.Usage;

        if (definition is null)
        {
            resolvedDescriptorPins = Array.AsReadOnly(Array.Empty<DescriptorReference>());
            requiredCapabilities = Array.AsReadOnly(Array.Empty<CapabilityRequirement>());
            return;
        }

        var plan = definition.ReadPlan();
        resolvedDescriptorPins = Array.AsReadOnly(
            plan.CatalogueBindings
                .Select(static descriptor => new DescriptorReference(
                    descriptor.Kind,
                    descriptor.Name,
                    descriptor.Version,
                    new ContentDigest(
                        descriptor.ContentDigest.Algorithm,
                        descriptor.ContentDigest.Contract,
                        descriptor.ContentDigest.Value)))
                .OrderBy(static descriptor => descriptor.Kind)
                .ThenBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.Version, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Algorithm, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Contract, StringComparer.Ordinal)
                .ThenBy(static descriptor => descriptor.ContentDigest.Value, StringComparer.Ordinal)
                .ToArray());
        requiredCapabilities = Array.AsReadOnly(
            plan.CapabilityManifest.Requirements
                .Select(static capability => new CapabilityRequirement(capability.Name, capability.ScopeClass))
                .OrderBy(static capability => capability.Name, StringComparer.Ordinal)
                .ThenBy(static capability => capability.ScopeClass, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>Creates an explanation from a completed compilation only.</summary>
    public static WorkflowExplanation Create(CompilationResult compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return new WorkflowExplanation(
            compilation,
            compilation.Diagnostics,
            admissionEvaluated: false,
            admissionSucceeded: false);
    }

    /// <summary>Creates an explanation from a completed host-admission result.</summary>
    /// <remarks>
    /// The admission receipt is intentionally not exposed by this projection.
    /// Callers that need the receipt must retain the original admission result.
    /// </remarks>
    public static WorkflowExplanation Create(WorkflowAdmissionResult admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return new WorkflowExplanation(
            admission.Compilation,
            admission.Diagnostics,
            admissionEvaluated: true,
            admissionSucceeded: admission.Succeeded);
    }

    /// <summary>The original immutable compilation result being explained.</summary>
    public CompilationResult Compilation { get; }

    /// <summary>Whether semantic compilation completed without error diagnostics.</summary>
    public bool CompilationSucceeded { get; }

    /// <summary>Whether host admission was evaluated for this explanation.</summary>
    public bool AdmissionEvaluated { get; }

    /// <summary>Whether the evaluated host admission succeeded.</summary>
    public bool AdmissionSucceeded { get; }

    /// <summary>The canonical execution fingerprint, or null when compilation failed.</summary>
    public string? ExecutionFingerprint => definition?.ExecutionFingerprint;

    /// <summary>
    /// Returns a detached plan snapshot, or null when compilation failed. Each
    /// access returns a fresh snapshot so caller mutation cannot affect this
    /// explanation.
    /// </summary>
    public WorkflowPlan? Plan => definition?.ReadPlan();

    /// <summary>Resolved descriptor pins in deterministic ordinal order.</summary>
    public IReadOnlyList<DescriptorReference> ResolvedDescriptorPins => resolvedDescriptorPins;

    /// <summary>Compiler-inferred capabilities in deterministic ordinal order.</summary>
    public IReadOnlyList<CapabilityRequirement> RequiredCapabilities => requiredCapabilities;

    /// <summary>The effective compilation budget used by the compiler.</summary>
    public CompilationBudget EffectiveBudget { get; }

    /// <summary>The bounded resource usage observed by the compiler.</summary>
    public CompilationUsageSummary Usage { get; }

    /// <summary>The bounded diagnostics from compilation, or compilation plus admission.</summary>
    public DiagnosticCollection Diagnostics { get; }
}
