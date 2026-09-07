using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>Minimal host capability-grant policy checked after semantic validation and catalogue resolution.</summary>
public sealed class CapabilityGrantPolicy
{
    private readonly IReadOnlySet<CapabilityRequirement> grants;
    private readonly bool allowAll;
    private readonly string? policyRevision;

    /// <summary>Creates a policy with the exact capability grants supplied by the host.</summary>
    public CapabilityGrantPolicy(IEnumerable<CapabilityRequirement> grantedCapabilities)
        : this(grantedCapabilities, null)
    {
    }

    /// <summary>Creates a finite grant policy with an immutable host policy revision.</summary>
    public CapabilityGrantPolicy(
        string policyRevision,
        IEnumerable<CapabilityRequirement> grantedCapabilities)
        : this(
            grantedCapabilities,
            PolicyRevisionText(policyRevision, nameof(policyRevision)))
    {
    }

    private CapabilityGrantPolicy(
        IEnumerable<CapabilityRequirement> grantedCapabilities,
        string? policyRevision)
    {
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        var snapshot = grantedCapabilities
            .Select(static grant => grant ?? throw new ArgumentException("A capability grant cannot be null.", nameof(grantedCapabilities)))
            .Select(static grant => new CapabilityRequirement(
                grant.Name ?? throw new ArgumentException("A capability grant name cannot be null.", nameof(grantedCapabilities)),
                grant.ScopeClass))
            .ToArray();
        if (snapshot.Any(static grant => string.IsNullOrWhiteSpace(grant.Name)))
            throw new ArgumentException("A capability grant name cannot be empty.", nameof(grantedCapabilities));
        if (snapshot.Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Capability grants must be unique.", nameof(grantedCapabilities));
        var ordered = snapshot
            .OrderBy(static grant => grant.Name, StringComparer.Ordinal)
            .ThenBy(static grant => grant.ScopeClass, StringComparer.Ordinal)
            .ToArray();
        grants = new HashSet<CapabilityRequirement>(ordered);
        this.policyRevision = policyRevision;
        GrantSetFingerprint = ComputeGrantSetFingerprint(ordered);
    }

    private CapabilityGrantPolicy(bool allowAll)
    {
        this.allowAll = allowAll;
        grants = new HashSet<CapabilityRequirement>();
        GrantSetFingerprint = ComputeGrantSetFingerprint([]);
    }

    /// <summary>A policy intended for tests or a trusted host that has already authorized every catalogue capability.</summary>
    public static CapabilityGrantPolicy AllowAll { get; } = new(true);

    /// <summary>The host authorization-policy revision, or null for a legacy unversioned policy.</summary>
    public string? PolicyRevision => policyRevision;

    /// <summary>Deterministic identity of the exact finite grant set.</summary>
    public string GrantSetFingerprint { get; }

    /// <summary>Returns whether the host has granted the exact capability and scope.</summary>
    public bool IsGranted(CapabilityRequirement requirement) =>
        allowAll || grants.Contains(requirement);

    internal bool CanIssueAdmissionReceipt => !allowAll && policyRevision is not null;

    private static string ComputeGrantSetFingerprint(IReadOnlyList<CapabilityRequirement> values)
    {
        var hash = SHA256.HashData(CanonicalJson.Serialize(values));
        return $"sha256:fuwen-capability-grants/v1:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string PolicyRevisionText(string value, string parameterName)
    {
        var result = CompilerContractValidation.Text(value, parameterName, 256, required: true)!;
        return !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new ArgumentException("The policy revision cannot be whitespace.", parameterName);
    }
}

/// <summary>Compiles a detached programmatic plan through semantic validation, catalogue resolution, and capability checks.</summary>
public sealed class WorkflowCompiler
{
    private readonly ITrustedCatalogue catalogue;
    private readonly CompilationBudget hostBudget;
    private readonly CapabilityGrantPolicy capabilityPolicy;

    /// <summary>Creates a compiler with host resource ceilings and capability grants.</summary>
    public WorkflowCompiler(
        ITrustedCatalogue catalogue,
        CompilationBudget? hostBudget = null,
        CapabilityGrantPolicy? capabilityPolicy = null)
    {
        this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        this.hostBudget = hostBudget ?? CompilationBudget.HostDefaults;
        this.capabilityPolicy = capabilityPolicy ?? new CapabilityGrantPolicy(Array.Empty<CapabilityRequirement>());
    }

    /// <summary>Compiles one plan. A failure returns diagnostics and no canonical definition.</summary>
    public ValueTask<CompilationResult> CompileAsync(
        WorkflowPlan plan,
        CompilationBudget? callerBudget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var effectiveBudget = callerBudget is null
            ? hostBudget
            : CompilationBudget.ApplyCaller(hostBudget, callerBudget);
        return CompileCoreAsync(plan, effectiveBudget, cancellationToken);
    }

    /// <summary>Synchronous convenience wrapper for programmatic callers.</summary>
    public CompilationResult Compile(
        WorkflowPlan plan,
        CompilationBudget? callerBudget = null,
        CancellationToken cancellationToken = default) =>
        CompileAsync(plan, callerBudget, cancellationToken).AsTask().GetAwaiter().GetResult();

    private async ValueTask<CompilationResult> CompileCoreAsync(
        WorkflowPlan input,
        CompilationBudget budget,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var diagnostics = new List<CompilerDiagnostic>();
        WorkflowPlan plan;
        try
        {
            // The snapshot is the first trust boundary. No semantic traversal
            // below observes caller-owned arrays, dictionaries, or JSON values.
            plan = WorkflowPlanSnapshot.Create(input);
        }
        catch (Exception exception) when (IsUserPlanFailure(exception))
        {
            diagnostics.Add(Diagnostic(
                CompilerDiagnosticCodes.SemanticValidationFailed,
                DiagnosticPhase.Validation,
                "The programmatic workflow definition could not be snapshotted.",
                exception.Message));
            return Failure(diagnostics, new CompilationUsageSummary(), budget);
        }

        var localUsage = PlanUsage.Count(plan);
        var initialBudget = CompilationBudgetEvaluator.Evaluate(budget, localUsage);
        diagnostics.AddRange(initialBudget.Diagnostics);
        if (!initialBudget.IsWithinBudget)
            return Failure(diagnostics, localUsage, budget);
        if (CompilationDeadlineExceeded(started, budget, diagnostics, localUsage, out var deadlineFailure))
            return deadlineFailure!;

        // v1 remains loadable as historical integrity-checked content, but it
        // is never silently upgraded or accepted by the current compiler.
        if (!string.Equals(plan.IrVersion, FuwenContracts.IrVersionV2, StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic(
                CompilerDiagnosticCodes.SemanticValidationFailed,
                DiagnosticPhase.Validation,
                "Historical IR v1 is integrity-loadable but is not accepted by the v2 compiler.",
                plan.IrVersion));
            return Failure(diagnostics, localUsage, budget);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var remainingMilliseconds = budget.MaxCompilationMilliseconds - PlanUsage.ElapsedMilliseconds(started);
        if (remainingMilliseconds <= 0)
        {
            diagnostics.Add(CompilationDeadlineDiagnostic(budget, started));
            return Failure(diagnostics, PlanUsage.WithCompilationMilliseconds(localUsage, PlanUsage.ElapsedMilliseconds(started)), budget);
        }
        var references = plan.CatalogueBindings
            .Concat(PlanUsage.ReferencedDescriptors(plan))
            .Distinct()
            .ToArray();
        var catalogueBudget = budget.WithCatalogueLookupMilliseconds(
            Math.Min(budget.MaxCatalogueLookupMilliseconds, Math.Max(1, remainingMilliseconds)));
        var resolved = await new TrustedCatalogueResolver(catalogue, catalogueBudget)
            .ResolveManyAsync(references, cancellationToken)
            .ConfigureAwait(false);
        diagnostics.AddRange(resolved.Diagnostics);
        var catalogueUsage = PlanUsage.AddCatalogueUsage(localUsage, resolved.Usage, started);
        if (!resolved.Succeeded)
            return Failure(diagnostics, catalogueUsage, budget);
        if (CompilationDeadlineExceeded(started, budget, diagnostics, catalogueUsage, out deadlineFailure))
            return deadlineFailure!;

        var trustedPlan = CreateResolvedPlan(plan, resolved.Results, diagnostics);
        if (trustedPlan is null)
            return Failure(diagnostics, catalogueUsage, budget);
        if (CompilationDeadlineExceeded(started, budget, diagnostics, catalogueUsage, out deadlineFailure))
            return deadlineFailure!;

        try
        {
            // Semantic validation deliberately runs only after catalogue
            // substitution. Caller-authored schemas are never used as the
            // type-checking authority.
            WorkflowPlanValidator.Validate(trustedPlan);
        }
        catch (Exception exception) when (IsUserPlanFailure(exception))
        {
            diagnostics.Add(Diagnostic(
                ValidationCode(exception),
                DiagnosticPhase.Validation,
                "The trusted workflow definition violates the executable-plan contract.",
                exception.Message));
            return Failure(diagnostics, catalogueUsage, budget);
        }

        var trustedDescriptors = resolved.Results
            .Where(static result => result.Succeeded && result.Descriptor is not null)
            .ToDictionary(static result => result.Requested, static result => result.Descriptor!, EqualityComparer<DescriptorReference>.Default);
        var bindingDiagnostics = WorkflowBindingValidator.Validate(trustedPlan, trustedDescriptors);
        diagnostics.AddRange(bindingDiagnostics);
        if (bindingDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return Failure(diagnostics, catalogueUsage, budget);

        var usage = catalogueUsage;
        if (CompilationDeadlineExceeded(started, budget, diagnostics, usage, out deadlineFailure))
            return deadlineFailure!;
        var budgetEvaluation = CompilationBudgetEvaluator.Evaluate(budget, usage);
        diagnostics.AddRange(budgetEvaluation.Diagnostics);
        if (!budgetEvaluation.IsWithinBudget)
            return Failure(diagnostics, usage, budget);

        WorkflowDefinitionDocument definition;
        try
        {
            // This is the canonical immutable definition boundary for compiled
            // output. It snapshots again and hashes those exact bytes; the
            // document is not an execution authorization receipt.
            definition = WorkflowDefinitionDocument.Create(trustedPlan);
        }
        catch (WorkflowDefinitionIntegrityException exception)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CanonicalDefinitionTooLarge,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Budget,
                "The canonical workflow definition exceeds the immutable size limit.",
                actual: exception.Message));
            return Failure(diagnostics, PlanUsage.WithCompilationMilliseconds(usage, PlanUsage.ElapsedMilliseconds(started)), budget);
        }
        catch (Exception exception) when (IsUserPlanFailure(exception))
        {
            diagnostics.Add(Diagnostic(
                CompilerDiagnosticCodes.SemanticValidationFailed,
                DiagnosticPhase.Admission,
                "The compiled workflow could not be materialized as canonical IR.",
                exception.Message));
            return Failure(diagnostics, usage, budget);
        }

        var finalUsage = PlanUsage.WithCompilationMilliseconds(usage, PlanUsage.ElapsedMilliseconds(started));
        if (CompilationDeadlineExceeded(started, budget, diagnostics, finalUsage, out deadlineFailure))
            return deadlineFailure!;
        var resolvedDescriptors = resolved.Results
            .Where(static result => result.Succeeded && result.Descriptor is not null)
            .Select(static result => result.Descriptor!)
            .ToArray();
        var admissionEvidence = new CompilationAdmissionEvidence(
            (catalogue as ITrustedCatalogueSnapshot)?.SnapshotRevision,
            CatalogueIdentity.ComputeResolvedSetFingerprint(resolvedDescriptors),
            capabilityPolicy.PolicyRevision,
            capabilityPolicy.GrantSetFingerprint,
            capabilityPolicy.CanIssueAdmissionReceipt);
        return new CompilationResult(definition, diagnostics, finalUsage, budget, admissionEvidence);
    }

    private WorkflowPlan? CreateResolvedPlan(
        WorkflowPlan plan,
        IReadOnlyList<DescriptorResolutionResult> results,
        List<CompilerDiagnostic> diagnostics)
    {
        var resolved = results
            .Where(static result => result.Succeeded && result.Descriptor is not null)
            .ToDictionary(static result => result.Requested, static result => result.Descriptor!, EqualityComparer<DescriptorReference>.Default);

        var trustedSchemas = new List<ResolvedSchemaDefinition>(plan.Schemas.Count);
        foreach (var authoredSchema in plan.Schemas)
        {
            if (!resolved.TryGetValue(authoredSchema.Descriptor, out var descriptor) || descriptor.SchemaDefinition is null)
            {
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CatalogueSchemaPayloadMissing,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Binding,
                    $"Trusted catalogue descriptor '{authoredSchema.Descriptor.Name}@{authoredSchema.Descriptor.Version}' does not provide a schema payload.",
                    path: authoredSchema.Descriptor.Name));
                continue;
            }

            if (!SchemasEquivalent(authoredSchema, descriptor.SchemaDefinition))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CatalogueSchemaMismatch,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Binding,
                    $"Authored schema '{authoredSchema.Descriptor.Name}@{authoredSchema.Descriptor.Version}' differs from its trusted catalogue payload.",
                    path: authoredSchema.Descriptor.Name));
                continue;
            }

            trustedSchemas.Add(descriptor.SchemaDefinition);
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Code is CompilerDiagnosticCodes.CatalogueSchemaPayloadMissing or CompilerDiagnosticCodes.CatalogueSchemaMismatch))
            return null;

        var required = resolved.Values
            .SelectMany(static descriptor => descriptor.RequiredCapabilities)
            .Distinct()
            .OrderBy(static capability => capability.Name, StringComparer.Ordinal)
            .ThenBy(static capability => capability.ScopeClass, StringComparer.Ordinal)
            .ToArray();
        var declared = plan.CapabilityManifest.Requirements.ToHashSet();
        var missing = required.Except(declared).ToArray();
        var extra = declared.Except(required).ToArray();
        if (missing.Length != 0 || extra.Length != 0)
        {
            foreach (var capability in missing)
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CapabilityManifestMismatch,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Admission,
                    $"Workflow omits trusted capability '{capability.Name}'.",
                    expected: capability.Name,
                    actual: "missing"));
            foreach (var capability in extra)
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CapabilityManifestMismatch,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Admission,
                    $"Workflow declares capability '{capability.Name}' that is not required by its trusted descriptors.",
                    expected: "inferred capability",
                    actual: capability.Name));
            return null;
        }

        foreach (var capability in required)
        {
            // The catalogue has inferred this requirement; the host policy is
            // the only authority that can grant it.
            if (!capabilityPolicy.IsGranted(capability))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CapabilityNotGranted,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Admission,
                    $"Host capability policy did not grant '{capability.Name}'.",
                    expected: capability.ScopeClass ?? "unscoped",
                    actual: "not granted"));
                return null;
            }
        }

        foreach (var descriptor in resolved.Values)
        {
            if (descriptor.CallableContract is null)
                continue;
            foreach (var reference in CallableContractReferences(descriptor.CallableContract))
            {
                var isResolved = resolved.TryGetValue(reference, out var referencedDescriptor);
                var hasRequiredSchemaPayload = reference.Kind != DescriptorKind.Schema ||
                    (referencedDescriptor?.SchemaDefinition is not null &&
                     trustedSchemas.Any(schema => schema.Descriptor.Equals(reference)));
                if (!isResolved || !hasRequiredSchemaPayload)
                {
                    diagnostics.Add(new CompilerDiagnostic(
                        CompilerDiagnosticCodes.CatalogueCallableContractReferenceMissing,
                        DiagnosticSeverity.Error,
                        DiagnosticPhase.Binding,
                        $"Trusted callable descriptor '{descriptor.Descriptor.Name}@{descriptor.Descriptor.Version}' references '{reference.Name}@{reference.Version}', which is outside the plan's resolved type closure.",
                        path: descriptor.Descriptor.Name,
                        expected: CatalogueContractValidation.Display(reference),
                        actual: "unresolved"));
                }
            }
        }
        if (diagnostics.Any(static diagnostic => diagnostic.Code == CompilerDiagnosticCodes.CatalogueCallableContractReferenceMissing))
            return null;

        return plan with
        {
            Schemas = trustedSchemas.ToArray(),
            CatalogueBindings = resolved.Values.Select(static descriptor => descriptor.Descriptor).ToArray(),
            CapabilityManifest = new CapabilityManifest(required),
        };

    }

    private static bool SchemasEquivalent(ResolvedSchemaDefinition left, ResolvedSchemaDefinition right) =>
        (left, right) switch
        {
            (ObjectSchemaDefinition first, ObjectSchemaDefinition second) =>
                first.Descriptor.Equals(second.Descriptor) &&
                first.Fields.Count == second.Fields.Count &&
                first.Fields.All(field => second.Fields.Any(candidate =>
                    string.Equals(candidate.Name, field.Name, StringComparison.Ordinal) && TypesEquivalent(field.Type, candidate.Type))),
            (EnumSchemaDefinition first, EnumSchemaDefinition second) =>
                first.Descriptor.Equals(second.Descriptor) &&
                first.Members.Count == second.Members.Count &&
                first.Members.All(member => second.Members.Any(candidate =>
                    string.Equals(candidate.Name, member.Name, StringComparison.Ordinal) &&
                    string.Equals(candidate.Value, member.Value, StringComparison.Ordinal))),
            _ => false,
        };

    private static bool TypesEquivalent(FuwenType left, FuwenType right) => (left, right) switch
    {
        (PrimitiveType first, PrimitiveType second) => first.Primitive == second.Primitive,
        (NamedTypeReference first, NamedTypeReference second) => first.Schema.Equals(second.Schema),
        (OptionalType first, OptionalType second) => TypesEquivalent(first.ValueType, second.ValueType),
        (ListType first, ListType second) => first.MaxItems == second.MaxItems && TypesEquivalent(first.ItemType, second.ItemType),
        (ArtifactType first, ArtifactType second) => first.ArtifactDescriptor.Equals(second.ArtifactDescriptor),
        _ => false,
    };

    private static IEnumerable<DescriptorReference> CallableContractReferences(CallableContract contract)
    {
        foreach (var parameter in contract.Signature.Parameters)
            foreach (var reference in TypeReferences(parameter.Type))
                yield return reference;
        foreach (var reference in TypeReferences(contract.Signature.OutputType))
            yield return reference;
    }

    private static IEnumerable<DescriptorReference> TypeReferences(FuwenType type)
    {
        switch (type)
        {
            case NamedTypeReference named:
                yield return named.Schema;
                break;
            case ArtifactType artifact:
                yield return artifact.ArtifactDescriptor;
                break;
            case OptionalType optional:
                foreach (var reference in TypeReferences(optional.ValueType))
                    yield return reference;
                break;
            case ListType list:
                foreach (var reference in TypeReferences(list.ItemType))
                    yield return reference;
                break;
        }
    }

    private CompilationResult Failure(
        IEnumerable<CompilerDiagnostic> diagnostics,
        CompilationUsageSummary usage,
        CompilationBudget budget) =>
        new((WorkflowPlan?)null, diagnostics, usage, budget);

    private static bool CompilationDeadlineExceeded(
        long started,
        CompilationBudget budget,
        List<CompilerDiagnostic> diagnostics,
        CompilationUsageSummary usage,
        out CompilationResult? failure)
    {
        if (PlanUsage.ElapsedMilliseconds(started) < budget.MaxCompilationMilliseconds)
        {
            failure = null;
            return false;
        }

        diagnostics.Add(CompilationDeadlineDiagnostic(budget, started));
        failure = new CompilationResult(
            (WorkflowDefinitionDocument?)null,
            diagnostics,
            PlanUsage.WithCompilationMilliseconds(usage, PlanUsage.ElapsedMilliseconds(started)),
            budget);
        return true;
    }

    private static CompilerDiagnostic CompilationDeadlineDiagnostic(CompilationBudget budget, long started) => new(
        CompilerDiagnosticCodes.BudgetCompilationMillisecondsExceeded,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Budget,
        "Compilation time budget exceeded.",
        expected: budget.MaxCompilationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        actual: PlanUsage.ElapsedMilliseconds(started).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static CompilerDiagnostic Diagnostic(
        string code,
        DiagnosticPhase phase,
        string message,
        string? actual = null) => new(code, DiagnosticSeverity.Error, phase, message, actual: actual);

    private static bool IsUserPlanFailure(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException;

    private static string ValidationCode(Exception exception) =>
        exception.Message.Contains("binding", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("node path", StringComparison.OrdinalIgnoreCase)
            ? CompilerDiagnosticCodes.BindingReferenceInvalid
            : CompilerDiagnosticCodes.SemanticValidationFailed;
}

internal static class WorkflowBindingValidator
{
    internal static IReadOnlyList<CompilerDiagnostic> Validate(
        WorkflowPlan plan,
        IReadOnlyDictionary<DescriptorReference, TrustedCatalogueDescriptor> descriptors)
    {
        var diagnostics = new List<CompilerDiagnostic>();
        var locations = new Dictionary<string, NodeLocation>(StringComparer.Ordinal);
        CollectLocations(plan.Name, plan.Nodes, plan.Name, locations);
        foreach (var location in locations.Values)
        {
            switch (location.Node)
            {
                case ContextNode context:
                    ValidateCallableNode(context.Provider, DescriptorKind.ContextProvider, context.Arguments, context.OutputType, location, plan, locations, descriptors, diagnostics);
                    break;
                case InferenceNode inference:
                    ValidateCallableNode(inference.Profile, DescriptorKind.InferenceProfile, inference.Arguments, inference.OutputType, location, plan, locations, descriptors, diagnostics);
                    var contextSources = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var snapshot in inference.ContextSnapshots)
                    {
                        if (!contextSources.Add(snapshot.NodePath))
                        {
                            diagnostics.Add(new CompilerDiagnostic(
                                CompilerDiagnosticCodes.ContextSnapshotDuplicate,
                                DiagnosticSeverity.Error,
                                DiagnosticPhase.Binding,
                                $"Inference node '{inference.StructuralPath}' repeats context snapshot '{snapshot.NodePath}'.",
                                path: inference.StructuralPath));
                        }
                        else if (!locations.TryGetValue(snapshot.NodePath, out var source) || source.Node is not ContextNode)
                        {
                            diagnostics.Add(new CompilerDiagnostic(
                                CompilerDiagnosticCodes.ContextSnapshotInvalid,
                                DiagnosticSeverity.Error,
                                DiagnosticPhase.Binding,
                                $"Inference context snapshot '{snapshot.NodePath}' must reference a ContextNode.",
                                path: inference.StructuralPath));
                        }
                        ValidateBinding(snapshot, null, location, plan, locations, diagnostics);
                    }
                    break;
                case ActivityNode activity:
                    ValidateCallableNode(activity.Activity, DescriptorKind.Activity, activity.Arguments, activity.OutputType, location, plan, locations, descriptors, diagnostics);
                    break;
                case ConditionalNode conditional:
                    ValidateCondition(conditional.Condition, location, plan, locations, diagnostics);
                    break;
                case ReturnNode @return:
                    ValidateBinding(@return.Value, plan.OutputType, location, plan, locations, diagnostics);
                    break;
            }
        }

        return diagnostics;
    }

    private static void ValidateCallableNode(
        DescriptorReference descriptorReference,
        DescriptorKind expectedKind,
        IReadOnlyList<ArgumentBinding> arguments,
        FuwenType outputType,
        NodeLocation location,
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<DescriptorReference, TrustedCatalogueDescriptor> descriptors,
        List<CompilerDiagnostic> diagnostics)
    {
        if (!descriptors.TryGetValue(descriptorReference, out var descriptor) || descriptor.CallableContract is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CatalogueCallableContractMissing,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Binding,
                $"Trusted {expectedKind} descriptor '{descriptorReference.Name}@{descriptorReference.Version}' does not provide a callable contract.",
                path: location.Node.StructuralPath));
            return;
        }

        var contract = descriptor.CallableContract;
        if (descriptor.Descriptor.Kind != expectedKind)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CatalogueCallableContractInvalid,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Binding,
                $"Descriptor '{descriptorReference.Name}@{descriptorReference.Version}' has kind '{descriptor.Descriptor.Kind}', not '{expectedKind}'.",
                path: location.Node.StructuralPath));
            return;
        }

        if (!Enum.IsDefined(contract.Effect) || contract.Effect is CallableEffect.External or CallableEffect.Destructive)
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CallableEffectRejected,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Admission,
                $"Callable '{descriptorReference.Name}@{descriptorReference.Version}' has an effect outside the conservative compilation matrix.",
                path: location.Node.StructuralPath,
                actual: contract.Effect.ToString()));
        if (!Enum.IsDefined(contract.Idempotency) || contract.Idempotency != CallableIdempotency.Idempotent ||
            !Enum.IsDefined(contract.RetrySafety) || contract.RetrySafety != CallableRetrySafety.Safe)
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CallableRetryRejected,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Admission,
                $"Callable '{descriptorReference.Name}@{descriptorReference.Version}' is not conservatively retry-safe.",
                path: location.Node.StructuralPath,
                actual: $"{contract.Idempotency}/{contract.RetrySafety}"));

        var expected = contract.Signature.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        var supplied = arguments.GroupBy(static argument => argument.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        foreach (var parameter in contract.Signature.Parameters)
        {
            if (!supplied.ContainsKey(parameter.Name))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CallableArgumentMissing,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Binding,
                    $"Callable argument '{parameter.Name}' is required.",
                    path: location.Node.StructuralPath,
                    expected: parameter.Name,
                    actual: "missing"));
            }
        }
        foreach (var argument in arguments)
        {
            if (!expected.TryGetValue(argument.Name, out var parameter))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    CompilerDiagnosticCodes.CallableArgumentUnknown,
                    DiagnosticSeverity.Error,
                    DiagnosticPhase.Binding,
                    $"Callable does not declare argument '{argument.Name}'.",
                    path: location.Node.StructuralPath,
                    actual: argument.Name));
                ValidateBinding(argument.Value, null, location, plan, locations, diagnostics);
                continue;
            }
            ValidateBinding(argument.Value, parameter.Type, location, plan, locations, diagnostics, CompilerDiagnosticCodes.CallableArgumentTypeMismatch, exact: true);
        }

        if (!EquivalentExact(outputType, contract.Signature.OutputType))
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.CallableOutputTypeMismatch,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Typing,
                "Callable node output type does not match the trusted callable signature.",
                path: location.Node.StructuralPath,
                expected: Describe(contract.Signature.OutputType),
                actual: Describe(outputType)));
    }

    private static void CollectLocations(
        string parentPath,
        IEnumerable<WorkflowNode> nodes,
        string region,
        IDictionary<string, NodeLocation> locations)
    {
        foreach (var node in nodes)
        {
            locations[node.StructuralPath] = new NodeLocation(node, region);
            if (node is ConditionalNode conditional)
            {
                CollectLocations($"{conditional.StructuralPath}/$then", conditional.Then, $"{conditional.StructuralPath}/$then", locations);
                CollectLocations($"{conditional.StructuralPath}/$else", conditional.Else, $"{conditional.StructuralPath}/$else", locations);
            }
        }
    }

    private static void ValidateArguments(
        IReadOnlyList<ArgumentBinding> arguments,
        NodeLocation location,
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        List<CompilerDiagnostic> diagnostics)
    {
        foreach (var argument in arguments)
            ValidateBinding(argument.Value, null, location, plan, locations, diagnostics);
    }

    private static void ValidateCondition(
        ConditionExpression condition,
        NodeLocation location,
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        List<CompilerDiagnostic> diagnostics)
    {
        var left = ValidateBinding(condition.Left, null, location, plan, locations, diagnostics);
        var right = condition.Right is null
            ? null
            : ValidateBinding(condition.Right, null, location, plan, locations, diagnostics);
        var valid = condition.Operator switch
        {
            ConditionOperator.Not or ConditionOperator.Exists => right is null &&
                (condition.Operator == ConditionOperator.Exists ? left is OptionalType : IsBoolean(left)),
            ConditionOperator.And or ConditionOperator.Or => right is not null && IsBoolean(left) && IsBoolean(right),
            ConditionOperator.Equal or ConditionOperator.NotEqual => right is not null && Compatible(left, right),
            _ => right is not null && Ordered(left) && Ordered(right),
        };
        if (!valid)
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.BindingTypeMismatch,
                DiagnosticSeverity.Error,
                DiagnosticPhase.Typing,
                $"Condition operator '{condition.Operator}' has incompatible operands.",
                path: location.Node.StructuralPath));
    }

    private static FuwenType? ValidateBinding(
        Binding binding,
        FuwenType? expected,
        NodeLocation consumer,
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        List<CompilerDiagnostic> diagnostics,
        string mismatchCode = CompilerDiagnosticCodes.BindingTypeMismatch,
        bool exact = false)
    {
        switch (binding)
        {
            case InputBinding input:
                return CheckExpected(ResolveProjection(plan.InputType, input.Projection, plan.Schemas, diagnostics, consumer.Node.StructuralPath), expected, consumer, diagnostics, mismatchCode, exact);
            case NodeOutputBinding output:
                if (!locations.TryGetValue(output.NodePath, out var source))
                {
                    diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticCodes.BindingReferenceInvalid, DiagnosticSeverity.Error, DiagnosticPhase.Binding, $"Binding references unknown node '{output.NodePath}'.", path: consumer.Node.StructuralPath));
                    return null;
                }
                if (!string.Equals(source.Region, consumer.Region, StringComparison.Ordinal))
                {
                    diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticCodes.BindingReferenceInvalid, DiagnosticSeverity.Error, DiagnosticPhase.Binding, "Binding crosses a structured-region boundary.", path: consumer.Node.StructuralPath));
                    return null;
                }
                var sourceType = source.Node switch
                {
                    ContextNode context => context.OutputType,
                    InferenceNode inference => inference.OutputType,
                    ActivityNode activity => activity.OutputType,
                    _ => null,
                };
                return CheckExpected(ResolveProjection(sourceType, output.Projection, plan.Schemas, diagnostics, consumer.Node.StructuralPath), expected, consumer, diagnostics, mismatchCode, exact);
            case LiteralBinding literal:
                if (literal.Value.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (expected is not null && !LiteralMatches(literal.Value, expected, exact))
                    diagnostics.Add(TypeMismatch(expected, consumer.Node.StructuralPath, mismatchCode));
                return expected ?? InferLiteral(literal.Value);
            case ListBinding list:
                var listExpected = expected is OptionalType optionalList ? optionalList.ValueType : expected;
                if (listExpected is PrimitiveType { Primitive: FuwenPrimitiveKind.Json })
                {
                    foreach (var item in list.Items)
                        ValidateBinding(item, null, consumer, plan, locations, diagnostics, mismatchCode, exact);
                    return expected;
                }
                if (listExpected is not null and not ListType)
                    diagnostics.Add(TypeMismatch(expected!, consumer.Node.StructuralPath, mismatchCode));
                if (listExpected is ListType expectedList && list.Items.Count > expectedList.MaxItems)
                    diagnostics.Add(new CompilerDiagnostic(mismatchCode, DiagnosticSeverity.Error, DiagnosticPhase.Typing, "List literal exceeds its declared maximum.", path: consumer.Node.StructuralPath));
                foreach (var item in list.Items)
                    ValidateBinding(item, listExpected is ListType listType ? listType.ItemType : null, consumer, plan, locations, diagnostics, mismatchCode, exact);
                return expected;
            case ObjectBinding @object:
                var objectExpected = expected is OptionalType optional ? optional.ValueType : expected;
                if (objectExpected is PrimitiveType { Primitive: FuwenPrimitiveKind.Json })
                {
                    foreach (var property in @object.Properties)
                        ValidateBinding(property.Value, null, consumer, plan, locations, diagnostics, mismatchCode, exact);
                }
                else if (objectExpected is NamedTypeReference named &&
                    plan.Schemas.FirstOrDefault(schema => schema.Descriptor.Equals(named.Schema)) is ObjectSchemaDefinition objectSchema)
                {
                    var fields = objectSchema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
                    foreach (var property in @object.Properties)
                    {
                        if (!fields.TryGetValue(property.Key, out var field))
                        {
                            diagnostics.Add(new CompilerDiagnostic(mismatchCode, DiagnosticSeverity.Error, DiagnosticPhase.Typing, $"Object field '{property.Key}' is not declared by schema '{named.Schema.Name}'.", path: consumer.Node.StructuralPath));
                            continue;
                        }
                        ValidateBinding(property.Value, field.Type, consumer, plan, locations, diagnostics, mismatchCode, exact);
                    }
                    foreach (var field in objectSchema.Fields.Where(field => field.Type is not OptionalType && !@object.Properties.ContainsKey(field.Name)))
                        diagnostics.Add(new CompilerDiagnostic(mismatchCode, DiagnosticSeverity.Error, DiagnosticPhase.Typing, $"Required object field '{field.Name}' is missing.", path: consumer.Node.StructuralPath));
                }
                else
                {
                    if (objectExpected is not null)
                        diagnostics.Add(TypeMismatch(expected!, consumer.Node.StructuralPath, mismatchCode));
                    foreach (var property in @object.Properties)
                        ValidateBinding(property.Value, null, consumer, plan, locations, diagnostics, mismatchCode, exact);
                }
                return expected;
            default:
                diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticCodes.BindingReferenceInvalid, DiagnosticSeverity.Error, DiagnosticPhase.Binding, "Unsupported binding shape.", path: consumer.Node.StructuralPath));
                return null;
        }
    }

    private static FuwenType? ResolveProjection(
        FuwenType? type,
        IReadOnlyList<string> projection,
        IReadOnlyList<ResolvedSchemaDefinition> schemas,
        List<CompilerDiagnostic> diagnostics,
        string path)
    {
        if (type is null)
            return null;
        foreach (var segment in projection)
        {
            if (type is OptionalType optional)
                type = optional.ValueType;
            if (type is not NamedTypeReference named ||
                schemas.FirstOrDefault(schema => schema.Descriptor.Equals(named.Schema)) is not ObjectSchemaDefinition objectSchema)
            {
                diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticCodes.BindingProjectionInvalid, DiagnosticSeverity.Error, DiagnosticPhase.Typing, $"Projection segment '{segment}' is not valid for the bound value.", path: path));
                return null;
            }
            var field = objectSchema.Fields.FirstOrDefault(field => string.Equals(field.Name, segment, StringComparison.Ordinal));
            if (field is null)
            {
                diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticCodes.BindingProjectionInvalid, DiagnosticSeverity.Error, DiagnosticPhase.Typing, $"Projection field '{segment}' is not declared by schema '{named.Schema.Name}'.", path: path));
                return null;
            }
            type = field.Type;
        }
        return type;
    }

    private static FuwenType? CheckExpected(
        FuwenType? actual,
        FuwenType? expected,
        NodeLocation consumer,
        List<CompilerDiagnostic> diagnostics,
        string mismatchCode,
        bool exact)
    {
        if (actual is not null && expected is not null && !(exact ? EquivalentExact(actual, expected) : Equivalent(actual, expected)))
            diagnostics.Add(TypeMismatch(expected, consumer.Node.StructuralPath, mismatchCode));
        return actual;
    }

    private static CompilerDiagnostic TypeMismatch(FuwenType expected, string path, string code) =>
        new(code, DiagnosticSeverity.Error, DiagnosticPhase.Typing, "Binding value is incompatible with its expected type.", path: path, expected: Describe(expected));

    private static bool LiteralMatches(JsonElement value, FuwenType type, bool exact) =>
        type switch
        {
            OptionalType optional => value.ValueKind == JsonValueKind.Null || LiteralMatches(value, optional.ValueType, exact),
            PrimitiveType primitive => primitive.Primitive switch
            {
                FuwenPrimitiveKind.String => value.ValueKind == JsonValueKind.String,
                FuwenPrimitiveKind.Duration => value.ValueKind == JsonValueKind.String && IsDuration(value),
                FuwenPrimitiveKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                FuwenPrimitiveKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                // JSON canonicalization does not preserve a distinct lexical
                // identity for integral-looking numbers such as 1 and 1.0.
                // A Number parameter therefore accepts every JSON number;
                // Integer remains the narrower TryGetInt64 contract.
                FuwenPrimitiveKind.Number => value.ValueKind == JsonValueKind.Number,
                FuwenPrimitiveKind.Json => true,
                _ => false,
            },
            _ => false,
        };

    private static bool IsDuration(JsonElement value)
    {
        try
        {
            _ = XmlConvert.ToTimeSpan(value.GetString()!);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static FuwenType? InferLiteral(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new PrimitiveType(FuwenPrimitiveKind.String),
        JsonValueKind.True or JsonValueKind.False => new PrimitiveType(FuwenPrimitiveKind.Boolean),
        JsonValueKind.Number when value.TryGetInt64(out _) => new PrimitiveType(FuwenPrimitiveKind.Integer),
        JsonValueKind.Number => new PrimitiveType(FuwenPrimitiveKind.Number),
        _ => new PrimitiveType(FuwenPrimitiveKind.Json),
    };

    private static bool IsBoolean(FuwenType? type) => type is PrimitiveType { Primitive: FuwenPrimitiveKind.Boolean };
    private static bool Ordered(FuwenType? type) => type is PrimitiveType { Primitive: FuwenPrimitiveKind.String or FuwenPrimitiveKind.Duration or FuwenPrimitiveKind.Integer or FuwenPrimitiveKind.Number };
    private static bool Compatible(FuwenType? left, FuwenType? right) => left is not null && right is not null && Equivalent(left, right);
    private static bool Equivalent(FuwenType left, FuwenType right) => left switch
    {
        PrimitiveType first when right is PrimitiveType second => first.Primitive == second.Primitive || (first.Primitive is FuwenPrimitiveKind.Integer or FuwenPrimitiveKind.Number && second.Primitive is FuwenPrimitiveKind.Integer or FuwenPrimitiveKind.Number),
        NamedTypeReference first when right is NamedTypeReference second => first.Schema.Equals(second.Schema),
        OptionalType first when right is OptionalType second => Equivalent(first.ValueType, second.ValueType),
        ListType first when right is ListType second => first.MaxItems == second.MaxItems && Equivalent(first.ItemType, second.ItemType),
        ArtifactType first when right is ArtifactType second => first.ArtifactDescriptor.Equals(second.ArtifactDescriptor),
        _ => false,
    };
    private static bool EquivalentExact(FuwenType left, FuwenType right) => (left, right) switch
    {
        (PrimitiveType first, PrimitiveType second) => first.Primitive == second.Primitive,
        (NamedTypeReference first, NamedTypeReference second) => first.Schema.Equals(second.Schema),
        (OptionalType first, OptionalType second) => EquivalentExact(first.ValueType, second.ValueType),
        (ListType first, ListType second) => first.MaxItems == second.MaxItems && EquivalentExact(first.ItemType, second.ItemType),
        (ArtifactType first, ArtifactType second) => first.ArtifactDescriptor.Equals(second.ArtifactDescriptor),
        _ => false,
    };
    private static string Describe(FuwenType type) => type.GetType().Name;
    private sealed record NodeLocation(WorkflowNode Node, string Region);
}

internal static class PlanUsage
{
    internal static CompilationUsageSummary Count(WorkflowPlan plan)
    {
        var nodes = 0;
        var schemas = plan.Schemas.Count;
        var fields = 0;
        var expressions = 0;
        var depth = 0;
        var schemaDepth = 0;
        foreach (var schema in plan.Schemas)
            if (schema is ObjectSchemaDefinition objectSchema)
            {
                fields += objectSchema.Fields.Count;
                foreach (var field in objectSchema.Fields)
                    schemaDepth = Math.Max(schemaDepth, TypeDepth(field.Type));
            }
        CountNodes(plan.Nodes, 1, ref nodes, ref expressions, ref depth);
        return new(
            astNodes: nodes + fields + schemas,
            nestingDepth: depth,
            workflowNodes: nodes,
            schemas: schemas,
            schemaDepth: schemaDepth,
            schemaFields: fields,
            expressions: expressions,
            stringBytes: StringBytes(plan));
    }

    internal static IReadOnlyList<DescriptorReference> ReferencedDescriptors(WorkflowPlan plan)
    {
        var result = new List<DescriptorReference>();
        AddType(plan.InputType, result);
        AddType(plan.OutputType, result);
        foreach (var schema in plan.Schemas)
        {
            result.Add(schema.Descriptor);
            if (schema is ObjectSchemaDefinition objectSchema)
                foreach (var field in objectSchema.Fields)
                    AddType(field.Type, result);
        }
        AddNodes(plan.Nodes, result);
        return result;
    }

    internal static CompilationUsageSummary AddCatalogueUsage(CompilationUsageSummary local, CompilationUsageSummary catalogue, long started) => new(
        local.SourceBytes, local.Tokens, local.AstNodes, local.NestingDepth, local.WorkflowNodes,
        local.Schemas, local.SchemaDepth, local.SchemaFields, local.Expressions, local.StringBytes,
        local.Diagnostics, catalogue.CatalogueLookups, catalogue.CatalogueLookupMilliseconds,
        ElapsedMilliseconds(started));

    internal static long ElapsedMilliseconds(long started) =>
        (long)Math.Ceiling((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);

    internal static CompilationUsageSummary WithCompilationMilliseconds(CompilationUsageSummary usage, long elapsed) => new(
        usage.SourceBytes, usage.Tokens, usage.AstNodes, usage.NestingDepth, usage.WorkflowNodes,
        usage.Schemas, usage.SchemaDepth, usage.SchemaFields, usage.Expressions, usage.StringBytes,
        usage.Diagnostics, usage.CatalogueLookups, usage.CatalogueLookupMilliseconds, elapsed);

    private static void CountNodes(IEnumerable<WorkflowNode> values, int currentDepth, ref int nodes, ref int expressions, ref int depth)
    {
        foreach (var node in values)
        {
            nodes++;
            depth = Math.Max(depth, currentDepth);
            switch (node)
            {
                case ContextNode context:
                    CountArguments(context.Arguments, ref expressions);
                    break;
                case InferenceNode inference:
                    CountArguments(inference.Arguments, ref expressions);
                    expressions += inference.ContextSnapshots.Count;
                    break;
                case ActivityNode activity:
                    CountArguments(activity.Arguments, ref expressions);
                    break;
                case ConditionalNode conditional:
                    expressions++;
                    CountBinding(conditional.Condition.Left, currentDepth, ref expressions, ref depth);
                    if (conditional.Condition.Right is not null)
                        CountBinding(conditional.Condition.Right, currentDepth, ref expressions, ref depth);
                    CountNodes(conditional.Then, currentDepth + 1, ref nodes, ref expressions, ref depth);
                    CountNodes(conditional.Else, currentDepth + 1, ref nodes, ref expressions, ref depth);
                    break;
                case ReturnNode @return:
                    CountBinding(@return.Value, currentDepth, ref expressions, ref depth);
                    break;
            }
        }
    }

    private static void CountArguments(IEnumerable<ArgumentBinding> values, ref int expressions)
    {
        foreach (var argument in values)
        {
            expressions++;
            var depth = 0;
            CountBinding(argument.Value, 1, ref expressions, ref depth);
        }
    }

    private static void CountBinding(Binding binding, int currentDepth, ref int expressions, ref int depth)
    {
        expressions++;
        depth = Math.Max(depth, currentDepth);
        switch (binding)
        {
            case ListBinding list:
                foreach (var item in list.Items)
                    CountBinding(item, currentDepth + 1, ref expressions, ref depth);
                break;
            case ObjectBinding @object:
                foreach (var item in @object.Properties.Values)
                    CountBinding(item, currentDepth + 1, ref expressions, ref depth);
                break;
        }
    }

    private static long StringBytes(WorkflowPlan plan)
    {
        long bytes = 0;
        AddText(plan.IrVersion, ref bytes);
        AddText(plan.LanguageVersion, ref bytes);
        AddText(plan.CompilerSemanticVersion, ref bytes);
        AddText(plan.CanonicalJsonVersion, ref bytes);
        AddText(plan.FingerprintVersion, ref bytes);
        AddText(plan.Name, ref bytes);
        AddText(plan.Revision, ref bytes);
        AddText(plan.RoutingPolicyRevision, ref bytes);
        foreach (var descriptor in plan.CatalogueBindings)
            AddDescriptorText(descriptor, ref bytes);
        AddTypeText(plan.InputType, ref bytes);
        AddTypeText(plan.OutputType, ref bytes);
        foreach (var schema in plan.Schemas)
        {
            AddDescriptorText(schema.Descriptor, ref bytes);
            switch (schema)
            {
                case ObjectSchemaDefinition @object:
                    foreach (var field in @object.Fields)
                    {
                        AddText(field.Name, ref bytes);
                        AddTypeText(field.Type, ref bytes);
                    }
                    break;
                case EnumSchemaDefinition @enum:
                    foreach (var member in @enum.Members)
                    {
                        AddText(member.Name, ref bytes);
                        AddText(member.Value, ref bytes);
                    }
                    break;
            }
        }
        foreach (var capability in plan.CapabilityManifest.Requirements)
        {
            AddText(capability.Name, ref bytes);
            AddText(capability.ScopeClass, ref bytes);
        }
        AddNodeText(plan.Nodes, ref bytes);
        if (plan.ExecutionOrder is not null)
        {
            foreach (var region in plan.ExecutionOrder.Regions)
            {
                AddText(region.RegionPath, ref bytes);
                foreach (var phase in region.Phases)
                    foreach (var path in phase.NodePaths)
                        AddText(path, ref bytes);
            }
        }

        return bytes;
    }

    private static void AddNodeText(IEnumerable<WorkflowNode> nodes, ref long bytes)
    {
        foreach (var node in nodes)
        {
            AddText(node.Name, ref bytes);
            AddText(node.StructuralPath, ref bytes);
            switch (node)
            {
                case ContextNode context:
                    AddDescriptorText(context.Provider, ref bytes);
                    AddTypeText(context.OutputType, ref bytes);
                    AddArgumentsText(context.Arguments, ref bytes);
                    break;
                case InferenceNode inference:
                    AddDescriptorText(inference.Profile, ref bytes);
                    AddDescriptorText(inference.PromptTemplate, ref bytes);
                    AddTypeText(inference.OutputType, ref bytes);
                    AddArgumentsText(inference.Arguments, ref bytes);
                    foreach (var snapshot in inference.ContextSnapshots)
                        AddBindingText(snapshot, ref bytes);
                    break;
                case ActivityNode activity:
                    AddDescriptorText(activity.Activity, ref bytes);
                    AddTypeText(activity.OutputType, ref bytes);
                    AddArgumentsText(activity.Arguments, ref bytes);
                    break;
                case ConditionalNode conditional:
                    AddBindingText(conditional.Condition.Left, ref bytes);
                    if (conditional.Condition.Right is not null)
                        AddBindingText(conditional.Condition.Right, ref bytes);
                    AddNodeText(conditional.Then, ref bytes);
                    AddNodeText(conditional.Else, ref bytes);
                    break;
                case ReturnNode @return:
                    AddBindingText(@return.Value, ref bytes);
                    break;
            }
        }
    }

    private static void AddArgumentsText(IEnumerable<ArgumentBinding> arguments, ref long bytes)
    {
        foreach (var argument in arguments)
        {
            AddText(argument.Name, ref bytes);
            AddBindingText(argument.Value, ref bytes);
        }
    }

    private static void AddBindingText(Binding binding, ref long bytes)
    {
        switch (binding)
        {
            case InputBinding input:
                AddProjectionText(input.Projection, ref bytes);
                break;
            case NodeOutputBinding output:
                AddText(output.NodePath, ref bytes);
                AddProjectionText(output.Projection, ref bytes);
                break;
            case LiteralBinding literal:
                AddText(literal.Value.GetRawText(), ref bytes);
                break;
            case ListBinding list:
                foreach (var item in list.Items)
                    AddBindingText(item, ref bytes);
                break;
            case ObjectBinding @object:
                foreach (var pair in @object.Properties)
                {
                    AddText(pair.Key, ref bytes);
                    AddBindingText(pair.Value, ref bytes);
                }
                break;
        }
    }

    private static void AddProjectionText(IEnumerable<string> projection, ref long bytes)
    {
        foreach (var segment in projection)
            AddText(segment, ref bytes);
    }

    private static void AddDescriptorText(DescriptorReference descriptor, ref long bytes)
    {
        AddText(descriptor.Name, ref bytes);
        AddText(descriptor.Version, ref bytes);
        AddText(descriptor.ContentDigest.Algorithm, ref bytes);
        AddText(descriptor.ContentDigest.Contract, ref bytes);
        AddText(descriptor.ContentDigest.Value, ref bytes);
    }

    private static void AddTypeText(FuwenType type, ref long bytes)
    {
        switch (type)
        {
            case NamedTypeReference named:
                AddDescriptorText(named.Schema, ref bytes);
                break;
            case OptionalType optional:
                AddTypeText(optional.ValueType, ref bytes);
                break;
            case ListType list:
                AddTypeText(list.ItemType, ref bytes);
                break;
            case ArtifactType artifact:
                AddDescriptorText(artifact.ArtifactDescriptor, ref bytes);
                break;
        }
    }

    private static void AddText(string? value, ref long bytes)
    {
        if (value is not null)
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(value));
    }

    private static int TypeDepth(FuwenType type) => type switch
    {
        OptionalType optional => 1 + TypeDepth(optional.ValueType),
        ListType list => 1 + TypeDepth(list.ItemType),
        _ => 1,
    };

    private static void AddNodes(IEnumerable<WorkflowNode> values, List<DescriptorReference> result)
    {
        foreach (var node in values)
        {
            switch (node)
            {
                case ContextNode context:
                    result.Add(context.Provider);
                    AddType(context.OutputType, result);
                    break;
                case InferenceNode inference:
                    result.Add(inference.Profile);
                    result.Add(inference.PromptTemplate);
                    AddType(inference.OutputType, result);
                    break;
                case ActivityNode activity:
                    result.Add(activity.Activity);
                    AddType(activity.OutputType, result);
                    break;
                case ConditionalNode conditional:
                    AddNodes(conditional.Then, result);
                    AddNodes(conditional.Else, result);
                    break;
            }
        }
    }

    private static void AddType(FuwenType type, List<DescriptorReference> result)
    {
        switch (type)
        {
            case NamedTypeReference named: result.Add(named.Schema); break;
            case ArtifactType artifact: result.Add(artifact.ArtifactDescriptor); break;
            case OptionalType optional: AddType(optional.ValueType, result); break;
            case ListType list: AddType(list.ItemType, result); break;
        }
    }
}
