namespace Penghou.Fuwen.Baize;

/// <summary>An exact descriptor route to one structured-text or generation executor.</summary>
public sealed class BaizeInferenceRoute
{
    /// <summary>Creates one exact inference route.</summary>
    public BaizeInferenceRoute(
        DescriptorReference profile,
        DescriptorReference promptTemplate,
        IInferenceExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(promptTemplate);
        if (profile.Kind != DescriptorKind.InferenceProfile)
            throw new ArgumentException("An inference route requires an InferenceProfile descriptor.", nameof(profile));
        if (promptTemplate.Kind != DescriptorKind.PromptTemplate)
            throw new ArgumentException("An inference route requires a PromptTemplate descriptor.", nameof(promptTemplate));
        Profile = profile;
        PromptTemplate = promptTemplate;
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>The exact admitted profile.</summary>
    public DescriptorReference Profile { get; }
    /// <summary>The exact admitted prompt template.</summary>
    public DescriptorReference PromptTemplate { get; }
    /// <summary>The executor selected for this exact descriptor pair.</summary>
    public IInferenceExecutor Executor { get; }
}

/// <summary>Routes mixed structured-text and media inference by exact descriptors.</summary>
public sealed class BaizeRoutedInferenceExecutor : IInferenceExecutor, IInferenceExecutorPreflight, IInferenceExecutorManifest
{
    private readonly IReadOnlyDictionary<(DescriptorReference Profile, DescriptorReference Prompt), IInferenceExecutor> routes;

    // A router cannot truthfully publish the cartesian union of its route
    // manifests: a profile from one route and a prompt from another would
    // appear executable even though that pair is not registered. Exact
    // detailed preflight therefore delegates to the selected route. This
    // intentionally conservative snapshot is only used for unmatched routes
    // and for legacy executors that do not expose a manifest.
    private static readonly InferenceFeatureManifest featureManifest = CreateConservativeManifest();

    /// <summary>Creates a deterministic exact-descriptor router.</summary>
    public BaizeRoutedInferenceExecutor(IReadOnlyList<BaizeInferenceRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
            throw new ArgumentException("At least one inference route is required.", nameof(routes));
        var copy = new Dictionary<(DescriptorReference, DescriptorReference), IInferenceExecutor>();
        foreach (var route in routes)
        {
            if (route is null)
                throw new ArgumentException("Inference routes cannot contain null values.", nameof(routes));
            if (!copy.TryAdd((route.Profile, route.PromptTemplate), route.Executor))
                throw new ArgumentException("Inference routes must have unique profile and prompt-template pairs.", nameof(routes));
        }
        this.routes = copy;
    }

    /// <summary>
    /// Gets the router's intentionally conservative manifest. It does not
    /// aggregate route capabilities or exact bindings; callers must use
    /// <see cref="PreflightDetailed(InferenceExecutionRequirement)"/> for
    /// exact route-aware admission.
    /// </summary>
    public InferenceFeatureManifest FeatureManifest => featureManifest;

    /// <inheritdoc />
    public ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Prompt is not null)
        {
            return ValueTask.FromResult(InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "Routed inference does not support workflow-owned prompts; bind the profile to BaizeInferenceExecutor.")));
        }
        return request.PromptTemplate is not null &&
            routes.TryGetValue((request.Profile, request.PromptTemplate), out var executor)
            ? executor.ExecuteAsync(request, cancellationToken)
            : ValueTask.FromResult(InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "No exact Baize inference route matched the admitted profile and prompt template.")));
    }

    /// <inheritdoc />
    public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.PromptTemplate is null)
        {
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "Routed inference does not support workflow-owned prompts; bind the profile to BaizeInferenceExecutor.");
        }
        if (!routes.TryGetValue((requirement.Profile, requirement.PromptTemplate), out var executor))
        {
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "No exact Baize inference route matched the admitted profile and prompt template.");
        }
        return executor is IInferenceExecutorPreflight preflight
            ? preflight.Preflight(requirement)
            : null;
    }

    /// <inheritdoc />
    public InferencePreflightReport PreflightDetailed(InferenceExecutionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // Route selection is deliberately performed before looking at a
        // manifest. Evaluating this router's descriptor sets would recreate
        // the invalid cartesian-union admission that the exact route table is
        // intended to prevent.
        if (requirement.PromptTemplate is not null &&
            routes.TryGetValue((requirement.Profile, requirement.PromptTemplate), out var executor))
        {
            if (executor is IInferenceExecutorManifest manifest)
                return manifest.PreflightDetailed(requirement);

            // A legacy executor may still execute through this router, but it
            // has no provider-neutral capability contract. Detailed preflight
            // must fail closed without invoking provider work.
        }

        return featureManifest.Preflight(requirement);
    }

    private static InferenceFeatureManifest CreateConservativeManifest() =>
        new(
            supportedPromptForms: [],
            supportedModalities: [],
            supportsContextDelivery: false,
            maximumContextPayloadUtf8Bytes: null,
            supportedToolEffects: [],
            supportedLimits: [],
            recoveryQuality: InferenceRecoveryQuality.Unsupported,
            usageQuality: InferenceUsageQuality.Unknown,
            pricingQuality: InferencePricingQuality.Unknown,
            supportsStructuredOutput: false,
            supportsSyntheticStructuredOutput: false,
            profiles: [],
            promptTemplates: [],
            tools: [],
            workflowPromptDigests: []);
}
