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
public sealed class BaizeRoutedInferenceExecutor : IInferenceExecutor
{
    private readonly IReadOnlyDictionary<(DescriptorReference Profile, DescriptorReference Prompt), IInferenceExecutor> routes;

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

    /// <inheritdoc />
    public ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return routes.TryGetValue((request.Profile, request.PromptTemplate), out var executor)
            ? executor.ExecuteAsync(request, cancellationToken)
            : ValueTask.FromResult(InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "No exact Baize inference route matched the admitted profile and prompt template.")));
    }
}
