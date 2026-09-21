using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

/// <summary>
/// Identifies the immutable provider and descriptor runtime snapshot available
/// to the Fuwen execution adapter.
/// </summary>
public interface IFuwenZhinuProviderRuntimeIdentity
{
    /// <summary>The exact trusted catalogue snapshot revision active at registration.</summary>
    string CatalogueSnapshotRevision { get; }

    /// <summary>The exact resolved descriptor set fingerprint active at registration.</summary>
    string ResolvedDescriptorSetFingerprint { get; }
}

/// <summary>A fixed, validated provider-runtime identity suitable for configuration and tests.</summary>
public sealed record FuwenZhinuProviderRuntimeIdentity : IFuwenZhinuProviderRuntimeIdentity
{
    /// <summary>Creates an exact immutable provider-runtime identity.</summary>
    public FuwenZhinuProviderRuntimeIdentity(
        string catalogueSnapshotRevision,
        string resolvedDescriptorSetFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogueSnapshotRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedDescriptorSetFingerprint);
        CatalogueSnapshotRevision = catalogueSnapshotRevision;
        ResolvedDescriptorSetFingerprint = resolvedDescriptorSetFingerprint;
    }

    /// <inheritdoc />
    public string CatalogueSnapshotRevision { get; }

    /// <inheritdoc />
    public string ResolvedDescriptorSetFingerprint { get; }
}

/// <summary>Raised when an admission result cannot safely become a Zhinu registration.</summary>
public sealed class FuwenZhinuAdmissionException : InvalidOperationException
{
    /// <summary>Creates an admission-boundary failure with a safe diagnostic message.</summary>
    public FuwenZhinuAdmissionException(string message) : base(message) { }
}

/// <summary>Raised when execution reaches an adapter capability that has not shipped.</summary>
public sealed class FuwenZhinuAdapterException : NotSupportedException
{
    /// <summary>Creates an adapter capability failure with a safe diagnostic message.</summary>
    public FuwenZhinuAdapterException(string message) : base(message) { }
}

/// <summary>Raised when a provider result cannot satisfy the admitted execution contract.</summary>
public sealed class FuwenZhinuExecutionException : InvalidOperationException
{
    /// <summary>Creates an adapter execution failure.</summary>
    public FuwenZhinuExecutionException(string message) : base(message) { }

    /// <summary>Creates an adapter execution failure from a provider-neutral failure.</summary>
    public FuwenZhinuExecutionException(ExecutionFailure failure)
        : base(failure?.Message ?? throw new ArgumentNullException(nameof(failure))) =>
        Failure = failure;

    /// <summary>The provider-neutral failure, when the failure came from an execution port.</summary>
    public ExecutionFailure? Failure { get; }
}

/// <summary>The provider-neutral execution ports used by the sequential Zhinu adapter.</summary>
public sealed class FuwenZhinuExecutionPorts
{
    /// <summary>Creates bounded execution options for provider calls.</summary>
    public sealed class Options
    {
        /// <summary>The maximum total attempts for one infrastructure retry loop.</summary>
        public const int MaximumAllowedInfrastructureAttempts = 8;
        /// <summary>The largest host fan-out concurrency bound accepted by this adapter.</summary>
        public const int MaximumAllowedFanOutConcurrency = 256;

        /// <summary>Creates options. The count includes the first provider attempt.</summary>
        public Options(int maximumInfrastructureAttempts = 1, int maximumFanOutConcurrency = 32)
        {
            if (maximumInfrastructureAttempts < 1 ||
                maximumInfrastructureAttempts > MaximumAllowedInfrastructureAttempts)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumInfrastructureAttempts),
                    $"Infrastructure attempts must be between 1 and {MaximumAllowedInfrastructureAttempts}.");
            }

            MaximumInfrastructureAttempts = maximumInfrastructureAttempts;
            if (maximumFanOutConcurrency < 1 || maximumFanOutConcurrency > MaximumAllowedFanOutConcurrency)
                throw new ArgumentOutOfRangeException(nameof(maximumFanOutConcurrency));
            MaximumFanOutConcurrency = maximumFanOutConcurrency;
        }

        /// <summary>The maximum total attempts for one infrastructure retry loop.</summary>
        public int MaximumInfrastructureAttempts { get; }
        /// <summary>The host ceiling applied to all keyed fan-out regions.</summary>
        public int MaximumFanOutConcurrency { get; }
    }

    /// <summary>Creates a complete provider-port set for sequential execution.</summary>
    public FuwenZhinuExecutionPorts(
        IActivityExecutor activityExecutor,
        IContextProvider contextProvider,
        IInferenceExecutor inferenceExecutor)
        : this(activityExecutor, contextProvider, inferenceExecutor, observer: null)
    {
    }

    /// <summary>Creates a complete provider-port set with an optional non-authoritative observer.</summary>
    public FuwenZhinuExecutionPorts(
        IActivityExecutor activityExecutor,
        IContextProvider contextProvider,
        IInferenceExecutor inferenceExecutor,
        IExecutionObserver? observer)
        : this(activityExecutor, contextProvider, inferenceExecutor, observer, new Options())
    {
    }

    /// <summary>Creates a complete provider-port set with bounded retry options.</summary>
    public FuwenZhinuExecutionPorts(
        IActivityExecutor activityExecutor,
        IContextProvider contextProvider,
        IInferenceExecutor inferenceExecutor,
        IExecutionObserver? observer,
        Options options)
    {
        ActivityExecutor = activityExecutor ?? throw new ArgumentNullException(nameof(activityExecutor));
        ContextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        InferenceExecutor = inferenceExecutor ?? throw new ArgumentNullException(nameof(inferenceExecutor));
        Observer = observer;
        ExecutionOptions = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Executes trusted activity descriptors.</summary>
    public IActivityExecutor ActivityExecutor { get; }
    /// <summary>Resolves trusted context-provider descriptors.</summary>
    public IContextProvider ContextProvider { get; }
    /// <summary>Executes trusted inference descriptors.</summary>
    public IInferenceExecutor InferenceExecutor { get; }
    /// <summary>Receives best-effort lifecycle observations, or null when observations are disabled.</summary>
    public IExecutionObserver? Observer { get; }
    /// <summary>The validated bounded execution options.</summary>
    public Options ExecutionOptions { get; }
    /// <summary>
    /// Previously admitted execution fingerprints whose durable step evidence
    /// this registration accepts alongside its own fingerprint. The host sets
    /// this when migrating a run across admitted plan versions (workflow
    /// mutation): reused steps keep their original evidence, which the
    /// interpreter admits only when the fingerprint is listed here, the
    /// structural path matches, and the request fingerprint matches. Empty
    /// by default, preserving strict single-plan evidence checks.
    /// </summary>
    public IReadOnlySet<string> PriorExecutionFingerprints { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// A verified, admission-bound Fuwen definition and its Zhinu registration.
/// </summary>
public sealed class FuwenZhinuWorkflowRegistration
{
    internal FuwenZhinuWorkflowRegistration(
        string name,
        string version,
        WorkflowDefinitionDocument definition,
        FuwenZhinuExecutionPorts? executionPorts)
    {
        Name = RequireText(name, nameof(name));
        Version = RequireText(version, nameof(version));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ExecutionPorts = executionPorts;
        ZhinuRegistration = new WorkflowRegistration<JsonElement, JsonElement>(
            new WorkflowDefinition { Name = Name, Version = Version },
            () => new FuwenZhinuSequentialWorkflow(
                Definition.ReadPlan(),
                Definition.ExecutionFingerprint,
                ExecutionPorts));
    }

    /// <summary>The host-defined durable Zhinu workflow name.</summary>
    public string Name { get; }

    /// <summary>The host-defined durable Zhinu workflow version.</summary>
    public string Version { get; }

    /// <summary>The verified immutable definition stored for this registration.</summary>
    public WorkflowDefinitionDocument Definition { get; }

    /// <summary>The provider ports bound to this registration, or null for admission-only registration.</summary>
    public FuwenZhinuExecutionPorts? ExecutionPorts { get; }

    /// <summary>The durable Zhinu registration carrying the Fuwen execution fingerprint.</summary>
    public WorkflowRegistration<JsonElement, JsonElement> ZhinuRegistration { get; }

    /// <summary>Registers this exact admission-bound definition with a Zhinu registry.</summary>
    public WorkflowRegistry Register(WorkflowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register(ZhinuRegistration);
    }

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

/// <summary>
/// Creates Zhinu registrations only from a successful, in-process Fuwen
/// admission receipt and matching immutable provider runtime identity.
/// </summary>
/// <remarks>
/// A <see cref="WorkflowAdmissionReceipt"/> is deliberately not serialized or
/// reconstructed. On process startup, hosts must re-admit and re-register a
/// definition before resuming a durable Zhinu run.
/// </remarks>
public sealed class FuwenZhinuWorkflowFactory
{
    private readonly IWorkflowDefinitionStore definitionStore;
    private readonly IFuwenZhinuProviderRuntimeIdentity providerRuntimeIdentity;
    private readonly FuwenZhinuExecutionPorts? executionPorts;

    /// <summary>Creates the registration boundary over one immutable definition store and runtime identity.</summary>
    public FuwenZhinuWorkflowFactory(
        IWorkflowDefinitionStore definitionStore,
        IFuwenZhinuProviderRuntimeIdentity providerRuntimeIdentity)
    {
        this.definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        this.providerRuntimeIdentity = providerRuntimeIdentity ?? throw new ArgumentNullException(nameof(providerRuntimeIdentity));
    }

    /// <summary>Creates an admission and sequential-execution boundary.</summary>
    public FuwenZhinuWorkflowFactory(
        IWorkflowDefinitionStore definitionStore,
        IFuwenZhinuProviderRuntimeIdentity providerRuntimeIdentity,
        FuwenZhinuExecutionPorts executionPorts)
    {
        this.definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        this.providerRuntimeIdentity = providerRuntimeIdentity ?? throw new ArgumentNullException(nameof(providerRuntimeIdentity));
        this.executionPorts = executionPorts ?? throw new ArgumentNullException(nameof(executionPorts));
    }

    /// <summary>
    /// Verifies, stores, reloads, and adapts one successfully admitted definition.
    /// </summary>
    public async ValueTask<FuwenZhinuWorkflowRegistration> CreateAsync(
        string name,
        string version,
        WorkflowAdmissionResult admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();

        if (!admission.Succeeded || admission.Receipt is null || admission.Compilation.Definition is null)
        {
            throw new FuwenZhinuAdmissionException(
                "Zhinu registration requires a successful Fuwen admission result with an in-process receipt and immutable definition.");
        }

        var receipt = admission.Receipt;
        var definition = admission.Compilation.Definition;
        VerifyDefinitionFingerprint(receipt, definition);
        VerifyProviderRuntimeIdentity(receipt, providerRuntimeIdentity);

        var admittedPlan = definition.ReadPlan();
        if (executionPorts is not null &&
            !IrVersions.SupportsTypedContextRequirements(admittedPlan.IrVersion))
        {
            throw new FuwenZhinuAdmissionException(
                $"The sequential Zhinu adapter supports '{FuwenContracts.IrVersionV3}'–'{FuwenContracts.IrVersionV8}', not '{admittedPlan.IrVersion}'.");
        }
        if (executionPorts is not null)
            ValidateExecutableSubset(admittedPlan.Nodes, insideFanOut: false, insideRepeat: false);
        if (executionPorts is not null)
            ValidateInferenceBindings(
                admittedPlan,
                executionPorts.InferenceExecutor as IInferenceExecutorPreflight);

        await definitionStore.StoreAsync(definition, cancellationToken).ConfigureAwait(false);
        var stored = await definitionStore.ReadAsync(receipt.ExecutionFingerprint, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            throw new FuwenZhinuAdmissionException(
                "The immutable Fuwen definition was not available after storage and cannot be registered.");
        }

        VerifyStoredDefinition(definition, stored);
        return new FuwenZhinuWorkflowRegistration(name, version, stored, executionPorts);
    }

    private static void ValidateExecutableSubset(IEnumerable<WorkflowNode> nodes, bool insideFanOut, bool insideRepeat)
    {
        foreach (var node in nodes)
        {
            if (insideFanOut && node is not ActivityNode and not ContextNode and not InferenceNode and not ConditionalNode)
            {
                throw new FuwenZhinuAdmissionException(
                    $"The sequential Zhinu adapter does not support '{node.GetType().Name}' inside fan-out body '{node.StructuralPath}'.");
            }
            if (insideRepeat && node is not ActivityNode and not ContextNode and not InferenceNode and not ConditionalNode and not CheckpointNode and not WaitNode)
            {
                throw new FuwenZhinuAdmissionException(
                    $"The sequential Zhinu adapter does not support '{node.GetType().Name}' inside repeat body '{node.StructuralPath}'.");
            }

            switch (node)
            {
                case ConditionalNode conditional:
                    if (conditional.Merge is not null && insideFanOut)
                        throw new FuwenZhinuAdmissionException(
                            $"The sequential Zhinu adapter does not support value-producing conditional '{conditional.StructuralPath}' inside fan-out.");
                    ValidateExecutableSubset(conditional.Then, insideFanOut, insideRepeat);
                    ValidateExecutableSubset(conditional.Else, insideFanOut, insideRepeat);
                    break;
                case FanOutNode fanOut:
                    if (insideFanOut)
                    {
                        throw new FuwenZhinuAdmissionException(
                            $"The sequential Zhinu adapter does not support nested fan-out body '{fanOut.StructuralPath}'.");
                    }
                    if (insideRepeat)
                    {
                        throw new FuwenZhinuAdmissionException(
                            $"The sequential Zhinu adapter does not support fan-out '{fanOut.StructuralPath}' inside repeat; nested regions need iteration-scoped step keys.");
                    }
                    ValidateExecutableSubset(fanOut.Body, insideFanOut: true, insideRepeat: false);
                    break;
                case RepeatNode repeat:
                    if (insideFanOut)
                    {
                        throw new FuwenZhinuAdmissionException(
                            $"The sequential Zhinu adapter does not support repeat '{repeat.StructuralPath}' inside fan-out; nested regions need iteration-scoped step keys.");
                    }
                    if (insideRepeat)
                    {
                        throw new FuwenZhinuAdmissionException(
                            $"The sequential Zhinu adapter does not support nested repeat '{repeat.StructuralPath}'; nested regions need iteration-scoped step keys.");
                    }
                    ValidateExecutableSubset(repeat.Body, insideFanOut: false, insideRepeat: true);
                    break;
            }
        }
    }

    private static void ValidateInferenceBindings(
        WorkflowPlan plan,
        IInferenceExecutorPreflight? preflight)
    {
        foreach (var node in EnumerateNodes(plan.Nodes).OfType<InferenceNode>())
        {
            DescriptorReference? promptTemplate = node.PromptTemplate;
            string? promptDigest = null;
            if (node.PromptName is not null)
            {
                var prompt = plan.Prompts!.Single(candidate =>
                    string.Equals(candidate.Name, node.PromptName, StringComparison.Ordinal));
                promptTemplate = prompt.RegisteredSource;
                if (promptTemplate is null)
                    promptDigest = prompt.GetSemanticDigest();
                else if (preflight is null)
                {
                    throw new FuwenZhinuAdmissionException(
                        $"Inference node '{node.StructuralPath}' uses registered prompt alias '{prompt.Name}', " +
                        "but the configured inference executor cannot preflight exact prompt-template bindings.");
                }
            }

            if (preflight is null)
                continue;

            var failure = preflight.Preflight(new InferenceExecutionRequirement(
                node.Profile,
                promptTemplate,
                promptDigest,
                node.Tools));
            if (failure is not null)
            {
                throw new FuwenZhinuAdmissionException(
                    $"Inference node '{node.StructuralPath}' failed executor preflight " +
                    $"[{failure.Code}]: {failure.Message}");
            }
        }
    }

    private static IEnumerable<WorkflowNode> EnumerateNodes(IEnumerable<WorkflowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => [],
            };
            foreach (var child in EnumerateNodes(children))
                yield return child;
        }
    }

    private static void VerifyDefinitionFingerprint(
        WorkflowAdmissionReceipt receipt,
        WorkflowDefinitionDocument definition)
    {
        if (!string.Equals(receipt.ExecutionFingerprint, definition.ExecutionFingerprint, StringComparison.Ordinal))
        {
            throw new FuwenZhinuAdmissionException(
                "The admission receipt execution fingerprint does not match the immutable definition.");
        }
    }

    private static void VerifyProviderRuntimeIdentity(
        WorkflowAdmissionReceipt receipt,
        IFuwenZhinuProviderRuntimeIdentity runtimeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentity.CatalogueSnapshotRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentity.ResolvedDescriptorSetFingerprint);

        if (!string.Equals(
                receipt.CatalogueSnapshotRevision,
                runtimeIdentity.CatalogueSnapshotRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ResolvedDescriptorSetFingerprint,
                runtimeIdentity.ResolvedDescriptorSetFingerprint,
                StringComparison.Ordinal))
        {
            throw new FuwenZhinuAdmissionException(
                "The active provider runtime identity does not match the Fuwen admission receipt.");
        }
    }

    private static void VerifyStoredDefinition(
        WorkflowDefinitionDocument expected,
        WorkflowDefinitionDocument stored)
    {
        if (!string.Equals(expected.ExecutionFingerprint, stored.ExecutionFingerprint, StringComparison.Ordinal) ||
            !expected.CanonicalBytes.Span.SequenceEqual(stored.CanonicalBytes.Span))
        {
            throw new FuwenZhinuAdmissionException(
                "The immutable Fuwen definition loaded from storage differs from the admitted definition.");
        }
    }

}

internal sealed class FuwenZhinuSequentialWorkflow(
    WorkflowPlan plan,
    string fingerprint,
    FuwenZhinuExecutionPorts? executionPorts)
    : IWorkflow<JsonElement, JsonElement>, IWorkflowFingerprint
{
    public string Fingerprint { get; } = fingerprint;

    public Task<JsonElement> RunAsync(
        WorkflowContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (executionPorts is null)
        {
            throw new FuwenZhinuAdapterException(
                "This registration is admission-only. Supply FuwenZhinuExecutionPorts to execute the admitted plan.");
        }

        return FuwenZhinuSequentialInterpreter.ExecuteAsync(
            plan,
            Fingerprint,
            executionPorts,
            context,
            input,
            cancellationToken);
    }
}
