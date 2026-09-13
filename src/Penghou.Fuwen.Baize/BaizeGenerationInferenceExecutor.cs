using System.Diagnostics;
using Penghou.Baize;
using Penghou.Baize.Generation;

namespace Penghou.Fuwen.Baize;

/// <summary>The media modality produced by one exact Baize generation binding.</summary>
public enum BaizeGenerationModality
{
    /// <summary>Image generation or transformation.</summary>
    Image,
    /// <summary>Video generation or transformation.</summary>
    Video,
    /// <summary>Audio generation or transformation.</summary>
    Audio,
}

/// <summary>
/// Trusted host boundary that materializes generated assets and returns
/// verified, durable artifact receipts.
/// </summary>
/// <remarks>
/// Implementations must treat <see cref="ExecutionInvocation.OperationKey"/>
/// as the idempotency identity for the complete ordered batch. Repeating a
/// call with the same request and equivalent assets must return the same
/// ordered artifact identities without creating duplicate publications.
/// Implementations must either publish atomically or resume a partially
/// published batch by that identity. They must read or retrieve each asset,
/// verify its exact immutable bytes, compute the returned content digest and
/// byte length from those bytes, persist them durably, and return receipts in
/// the same order as the supplied assets. Remote retrieval, credentials,
/// storage selection, and byte verification remain host responsibilities;
/// Fuwen validates receipt identity and nominal type but never dereferences
/// provider content.
/// </remarks>
public interface IBaizeGeneratedAssetPublisher
{
    /// <summary>
    /// Publishes the complete batch under the Fuwen operation identity. The
    /// implementation must satisfy the ordering, verification, durability,
    /// and idempotency contract described by this interface.
    /// </summary>
    ValueTask<IReadOnlyList<ArtifactPublicationReceipt>> PublishAsync(
        InferenceExecutionRequest request,
        IReadOnlyList<GeneratedAsset> assets,
        DescriptorReference artifactDescriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>Trusted policy for one descriptor-bound generation route.</summary>
public sealed class BaizeGenerationPolicy
{
    /// <summary>The largest generated batch accepted by this adapter version.</summary>
    public const int MaximumGeneratedAssets = 256;

    /// <summary>Creates a bounded generation policy.</summary>
    public BaizeGenerationPolicy(
        int maximumAssets = 1,
        string? policyRevision = null,
        string? routingPolicyRevision = null,
        Func<InferenceExecutionRequest, string?>? rejection = null,
        TimeSpan? timeout = null,
        TimeSpan? pollingInterval = null)
    {
        if (maximumAssets < 1 || maximumAssets > MaximumGeneratedAssets)
            throw new ArgumentOutOfRangeException(nameof(maximumAssets));
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var effectivePollingInterval = pollingInterval ?? TimeSpan.FromSeconds(1);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (effectivePollingInterval <= TimeSpan.Zero || effectivePollingInterval > effectiveTimeout)
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));
        MaximumAssets = maximumAssets;
        PolicyRevision = BaizeBindingValidation.OptionalText(
            policyRevision,
            nameof(policyRevision),
            InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        RoutingPolicyRevision = BaizeBindingValidation.OptionalText(
            routingPolicyRevision,
            nameof(routingPolicyRevision),
            InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Rejection = rejection;
        Timeout = effectiveTimeout;
        PollingInterval = effectivePollingInterval;
    }

    /// <summary>The largest generated batch accepted from the configured route.</summary>
    public int MaximumAssets { get; }
    /// <summary>The host policy identity recorded in evidence.</summary>
    public string? PolicyRevision { get; }
    /// <summary>The host routing identity recorded in evidence.</summary>
    public string? RoutingPolicyRevision { get; }
    /// <summary>Optional trusted admission predicate.</summary>
    public Func<InferenceExecutionRequest, string?>? Rejection { get; }
    /// <summary>The trusted wall-clock limit for submission and polling.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>The fixed interval between durable operation polls.</summary>
    public TimeSpan PollingInterval { get; }
}

/// <summary>An exact host-owned binding from Fuwen inference to Baize generation.</summary>
public sealed class BaizeGenerationBinding
{
    /// <summary>Creates one descriptor-bound media-generation route.</summary>
    public BaizeGenerationBinding(
        DescriptorReference profile,
        DescriptorReference promptTemplate,
        DescriptorReference artifactDescriptor,
        BaizeGenerationModality modality,
        string endpointId,
        string provider,
        string model,
        IGenerationClient client,
        Func<InferenceExecutionRequest, GenerationRequest> requestFactory,
        IBaizeGeneratedAssetPublisher publisher,
        BaizeGenerationPolicy? policy = null,
        Func<GenerationResult, InferenceCostEvidence?>? costResolver = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(promptTemplate);
        ArgumentNullException.ThrowIfNull(artifactDescriptor);
        if (profile.Kind != DescriptorKind.InferenceProfile)
            throw new ArgumentException("Generation bindings require an InferenceProfile descriptor.", nameof(profile));
        if (promptTemplate.Kind != DescriptorKind.PromptTemplate)
            throw new ArgumentException("Generation bindings require a PromptTemplate descriptor.", nameof(promptTemplate));
        if (artifactDescriptor.Kind != DescriptorKind.Artifact)
            throw new ArgumentException("Generation bindings require an Artifact descriptor.", nameof(artifactDescriptor));
        if (!Enum.IsDefined(modality))
            throw new ArgumentOutOfRangeException(nameof(modality));

        Profile = profile;
        PromptTemplate = promptTemplate;
        ArtifactDescriptor = artifactDescriptor;
        Modality = modality;
        EndpointId = BaizeBindingValidation.Text(endpointId, nameof(endpointId), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Provider = BaizeBindingValidation.Text(provider, nameof(provider), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Model = BaizeBindingValidation.Text(model, nameof(model), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Client = client ?? throw new ArgumentNullException(nameof(client));
        if (!Client.Capabilities.Supports(GenerationFeature.IdempotentSubmission))
            throw new ArgumentException(
                "A Fuwen generation binding requires Baize idempotent submission for crash-safe replay.",
                nameof(client));
        if (!Client.Capabilities.Supports(GenerationFeature.OperationRetrieval))
            throw new ArgumentException(
                "A Fuwen generation binding requires Baize operation retrieval for durable polling.",
                nameof(client));
        RequestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        Publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        Policy = policy ?? new BaizeGenerationPolicy();
        CostResolver = costResolver;
    }

    /// <summary>The exact admitted logical profile descriptor.</summary>
    public DescriptorReference Profile { get; }
    /// <summary>The exact admitted prompt-template descriptor.</summary>
    public DescriptorReference PromptTemplate { get; }
    /// <summary>The nominal artifact descriptor assigned after verified publication.</summary>
    public DescriptorReference ArtifactDescriptor { get; }
    /// <summary>The resolved generation modality.</summary>
    public BaizeGenerationModality Modality { get; }
    /// <summary>The configured endpoint identity.</summary>
    public string EndpointId { get; }
    /// <summary>The configured provider identity.</summary>
    public string Provider { get; }
    /// <summary>The configured model identity.</summary>
    public string Model { get; }
    /// <summary>The exact Baize generation endpoint used for durable submission and polling.</summary>
    public IGenerationClient Client { get; }
    /// <summary>The trusted mapping from typed Fuwen inputs to a modality-specific request.</summary>
    public Func<InferenceExecutionRequest, GenerationRequest> RequestFactory { get; }
    /// <summary>The host-owned durable artifact publisher.</summary>
    public IBaizeGeneratedAssetPublisher Publisher { get; }
    /// <summary>Trusted generation policy.</summary>
    public BaizeGenerationPolicy Policy { get; }
    /// <summary>Optional host-owned mapping from provider result metadata to cost.</summary>
    public Func<GenerationResult, InferenceCostEvidence?>? CostResolver { get; }
}

/// <summary>Executes artifact-producing inference through Baize generation clients.</summary>
public sealed class BaizeGenerationInferenceExecutor : IInferenceExecutor
{
    private readonly IReadOnlyList<BaizeGenerationBinding> bindings;
    private readonly IBaizeInferenceProvenanceSink? provenanceSink;

    /// <summary>Creates an executor over exact descriptor-bound generation routes.</summary>
    public BaizeGenerationInferenceExecutor(
        IReadOnlyList<BaizeGenerationBinding> bindings,
        IBaizeInferenceProvenanceSink? provenanceSink = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0)
            throw new ArgumentException("At least one generation binding is required.", nameof(bindings));
        var copy = bindings.Select(binding => binding ??
            throw new ArgumentException("Generation bindings cannot contain null values.", nameof(bindings))).ToArray();
        if (copy.Select(binding => (binding.Profile, binding.PromptTemplate)).Distinct().Count() != copy.Length)
            throw new ArgumentException("Profile and prompt-template descriptor pairs must be unique.", nameof(bindings));
        this.bindings = Array.AsReadOnly(copy);
        this.provenanceSink = provenanceSink;
    }

    /// <inheritdoc />
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = bindings.FirstOrDefault(candidate =>
            candidate.Profile == request.Profile && candidate.PromptTemplate == request.PromptTemplate);
        if (binding is null)
            return InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.DescriptorUnavailable,
                "No exact Baize generation binding matched the admitted profile and prompt template."));

        var started = Stopwatch.GetTimestamp();
        var modality = ToInferenceModality(binding.Modality);
        string? rejection;
        try
        {
            rejection = binding.Policy.Rejection?.Invoke(request);
        }
        catch (Exception exception)
        {
            rejection = $"The trusted generation policy failed: {exception.GetType().Name}.";
        }
        if (rejection is not null)
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Admission,
                ExecutionFailureCode.PolicyRejected, rejection, "PolicyRejected", cancellationToken).ConfigureAwait(false);

        if (!TryGetArtifactOutput(request.OutputType, binding.ArtifactDescriptor, out var expectsList, out var maximumItems))
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Contract,
                ExecutionFailureCode.InvalidInput,
                "The generation output must be the binding's exact artifact type or a bounded list of that artifact type.",
                "InvalidOutputContract", cancellationToken).ConfigureAwait(false);

        GenerationRequest generationRequest;
        try
        {
            generationRequest = binding.RequestFactory(request) ??
                throw new InvalidOperationException("The generation request factory returned null.");
        }
        catch (Exception exception)
        {
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Contract,
                ExecutionFailureCode.InvalidInput,
                $"The trusted generation request mapping failed: {exception.GetType().Name}.",
                exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(generationRequest.IdempotencyKey, request.Invocation.OperationKey, StringComparison.Ordinal))
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Contract,
                ExecutionFailureCode.InvalidInput,
                "The generation request must use the Fuwen operation key as its idempotency key.",
                "IdempotencyKeyMismatch", cancellationToken).ConfigureAwait(false);

        GenerationResult result;
        try
        {
            result = await ExecuteDurablyAsync(binding, generationRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GenerationTerminalException exception)
        {
            var mapped = MapFailure(exception.Error.Kind);
            return await FailAsync(request, binding, modality, started, mapped.Kind, mapped.Code,
                exception.Error.Message, exception.Error.Kind.ToString(), cancellationToken,
                mapped.MayHaveCommittedEffect, attemptSucceeded: false).ConfigureAwait(false);
        }
        catch (GenerationPollingTimeoutException)
        {
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Timeout,
                ExecutionFailureCode.Timeout, "Baize generation exceeded the trusted polling timeout.",
                GenerationErrorKind.TimeoutExceeded.ToString(), cancellationToken,
                mayHaveCommittedEffect: true, attemptSucceeded: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError, "Baize generation failed.", exception.GetType().Name,
                cancellationToken, mayHaveCommittedEffect: true, attemptSucceeded: false).ConfigureAwait(false);
        }

        var assets = result.Assets?.ToArray() ?? [];
        var allowedAssets = Math.Min(binding.Policy.MaximumAssets, maximumItems);
        if (assets.Length == 0 || assets.Length > allowedAssets || (!expectsList && assets.Length != 1) || assets.Any(asset => asset is null))
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.SchemaMismatch,
                $"Baize generation returned {assets.Length} assets; the admitted output permits {allowedAssets}.",
                "AssetCountMismatch", cancellationToken,
                mayHaveCommittedEffect: true, attemptSucceeded: false).ConfigureAwait(false);

        InferenceCostEvidence? cost;
        try
        {
            cost = binding.CostResolver?.Invoke(result);
        }
        catch (Exception exception)
        {
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError, "Generation cost accounting failed.", exception.GetType().Name,
                cancellationToken, mayHaveCommittedEffect: true, attemptSucceeded: true).ConfigureAwait(false);
        }

        IReadOnlyList<ArtifactPublicationReceipt> publications;
        try
        {
            publications = await binding.Publisher.PublishAsync(
                request, assets, binding.ArtifactDescriptor, cancellationToken).ConfigureAwait(false);
            ValidatePublications(publications, assets.Length, request.Invocation.OperationKey, binding.ArtifactDescriptor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await FailAsync(request, binding, modality, started, ExecutionFailureKind.Contract,
                ExecutionFailureCode.PublicationRejected,
                $"Generated artifact publication was rejected: {exception.GetType().Name}.",
                exception.GetType().Name, cancellationToken,
                mayHaveCommittedEffect: true, cost: cost, attemptSucceeded: true).ConfigureAwait(false);
        }

        var output = expectsList
            ? RuntimeValue.FromList(publications.Select(receipt => RuntimeValue.FromArtifact(receipt.Artifact)).ToArray())
            : RuntimeValue.FromArtifact(publications[0].Artifact);
        var evidence = Evidence(request, binding, modality, started, attemptSucceeded: true, null, null, cost);
        await RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
        return InferenceExecutionResult.Succeeded(output, publications, evidence);
    }

    private async ValueTask<InferenceExecutionResult> FailAsync(
        InferenceExecutionRequest request,
        BaizeGenerationBinding binding,
        InferenceModality modality,
        long started,
        ExecutionFailureKind kind,
        ExecutionFailureCode code,
        string message,
        string providerCode,
        CancellationToken cancellationToken,
        bool mayHaveCommittedEffect = false,
        InferenceCostEvidence? cost = null,
        bool? attemptSucceeded = null)
    {
        var boundedMessage = Bound(message, ExecutionFailure.MaximumMessageUtf8Bytes);
        var evidence = Evidence(request, binding, modality, started, attemptSucceeded,
            providerCode, boundedMessage, cost);
        await RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
        return InferenceExecutionResult.Failed(new ExecutionFailure(
            kind, code, boundedMessage, mayHaveCommittedEffect: mayHaveCommittedEffect,
            providerCode: Bound(providerCode, ExecutionFailure.MaximumProviderCodeUtf8Bytes)), evidence);
    }

    private static InferenceExecutionEvidence Evidence(
        InferenceExecutionRequest request,
        BaizeGenerationBinding binding,
        InferenceModality modality,
        long started,
        bool? attemptSucceeded,
        string? failureCode,
        string? failureMessage,
        InferenceCostEvidence? cost)
    {
        IReadOnlyList<InferenceAttemptEvidence> attempts = attemptSucceeded switch
        {
            true => [new InferenceAttemptEvidence(
                1, binding.Provider, binding.Model, binding.EndpointId, succeeded: true,
                durationMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                cost: cost)],
            false => [new InferenceAttemptEvidence(
                1, binding.Provider, binding.Model, binding.EndpointId, succeeded: false,
                failureCode, failureMessage,
                durationMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                cost: cost)],
            null => [],
        };
        return new InferenceExecutionEvidence(
            request.Profile,
            request.PromptTemplate,
            attempts,
            policyRevision: binding.Policy.PolicyRevision,
            routingPolicyRevision: binding.Policy.RoutingPolicyRevision,
            durationMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            modality: modality,
            cost: cost);
    }

    private static bool TryGetArtifactOutput(
        FuwenType outputType,
        DescriptorReference artifactDescriptor,
        out bool expectsList,
        out int maximumItems)
    {
        expectsList = false;
        maximumItems = 1;
        if (outputType is ArtifactType artifact)
            return artifact.ArtifactDescriptor == artifactDescriptor;
        if (outputType is ListType { ItemType: ArtifactType item } list &&
            list.MaxItems > 0 &&
            item.ArtifactDescriptor == artifactDescriptor)
        {
            expectsList = true;
            maximumItems = list.MaxItems;
            return true;
        }
        return false;
    }

    private static void ValidatePublications(
        IReadOnlyList<ArtifactPublicationReceipt>? publications,
        int expectedCount,
        string operationKey,
        DescriptorReference artifactDescriptor)
    {
        if (publications is null || publications.Count != expectedCount)
            throw new InvalidOperationException("The publisher must return one receipt for every generated asset.");
        foreach (var receipt in publications)
        {
            if (receipt is null || receipt.Artifact is null)
                throw new InvalidOperationException("Publication receipts cannot contain null values.");
            if (!string.Equals(receipt.IdempotencyKey, operationKey, StringComparison.Ordinal))
                throw new InvalidOperationException("A publication receipt is not bound to the Fuwen operation key.");
            if (receipt.Artifact.ArtifactDescriptor != artifactDescriptor)
                throw new InvalidOperationException("A publication receipt has the wrong nominal artifact descriptor.");
        }
    }

    private static (ExecutionFailureKind Kind, ExecutionFailureCode Code, bool MayHaveCommittedEffect) MapFailure(
        GenerationErrorKind errorKind) => errorKind switch
        {
            GenerationErrorKind.InvalidRequest => (ExecutionFailureKind.Contract, ExecutionFailureCode.InvalidInput, false),
            GenerationErrorKind.UnsupportedCapability or GenerationErrorKind.SafetyRejected =>
                (ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected, false),
            GenerationErrorKind.Canceled => (ExecutionFailureKind.Cancelled, ExecutionFailureCode.Cancelled, false),
            GenerationErrorKind.TimeoutExceeded => (ExecutionFailureKind.Timeout, ExecutionFailureCode.Timeout, true),
            GenerationErrorKind.UnknownSubmissionOutcome => (ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, true),
            _ => (ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, false),
        };

    private static async Task<GenerationResult> ExecuteDurablyAsync(
        BaizeGenerationBinding binding,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(binding.Policy.Timeout);
        try
        {
            var operation = await binding.Client.SubmitAsync(request, timeout.Token).ConfigureAwait(false);
            var submittedHandle = operation?.Handle ?? throw new InvalidOperationException(
                "Baize generation submission did not include an operation handle.");
            EnsureSubmittedOperationIdentity(binding, submittedHandle);
            while (true)
            {
                var currentHandle = operation.Handle ?? throw new InvalidOperationException(
                    "Baize generation polling returned an operation without a handle.");
                EnsureStableOperationIdentity(submittedHandle, currentHandle);
                switch (operation.State)
                {
                    case GenerationOperationState.Succeeded:
                        return operation.Result ?? throw new InvalidOperationException(
                            "A successful Baize generation operation did not include a result.");
                    case GenerationOperationState.Failed:
                    case GenerationOperationState.Canceled:
                        throw new GenerationTerminalException(operation.Error ?? new GenerationError(
                            operation.State == GenerationOperationState.Canceled
                                ? GenerationErrorKind.Canceled
                                : GenerationErrorKind.GenerationFailed,
                            $"Baize generation ended in state '{operation.State}'."));
                }

                await Task.Delay(binding.Policy.PollingInterval, timeout.Token).ConfigureAwait(false);
                operation = await binding.Client.GetAsync(currentHandle, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new GenerationPollingTimeoutException();
        }
    }

    private static void EnsureStableOperationIdentity(
        GenerationOperationHandle submittedHandle,
        GenerationOperationHandle currentHandle)
    {
        if (!string.Equals(submittedHandle.Provider, currentHandle.Provider, StringComparison.Ordinal) ||
            !string.Equals(submittedHandle.EndpointId, currentHandle.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(submittedHandle.Id, currentHandle.Id, StringComparison.Ordinal) ||
            !string.Equals(submittedHandle.Model, currentHandle.Model, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Baize generation polling returned a handle with a different provider, endpoint, operation, or model identity.");
    }

    private static void EnsureSubmittedOperationIdentity(
        BaizeGenerationBinding binding,
        GenerationOperationHandle submittedHandle)
    {
        if (!string.Equals(binding.Provider, submittedHandle.Provider, StringComparison.Ordinal) ||
            !string.Equals(binding.EndpointId, submittedHandle.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(binding.Model, submittedHandle.Model, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Baize generation submission returned a handle outside the configured provider, endpoint, or model route.");
    }

    private static InferenceModality ToInferenceModality(BaizeGenerationModality modality) => modality switch
    {
        BaizeGenerationModality.Image => InferenceModality.Image,
        BaizeGenerationModality.Video => InferenceModality.Video,
        BaizeGenerationModality.Audio => InferenceModality.Audio,
        _ => throw new ArgumentOutOfRangeException(nameof(modality)),
    };

    private static string Bound(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unclassified generation failure.";
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes) return value;
        var builder = new System.Text.StringBuilder(value.Length);
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumUtf8Bytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.Length == 0 ? "Unclassified generation failure." : builder.ToString();
    }

    private async ValueTask RecordAsync(InferenceExecutionEvidence evidence, CancellationToken cancellationToken)
    {
        if (provenanceSink is null) return;
        try
        {
            await provenanceSink.RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Provenance sinks are observational. Durable execution evidence is
            // still returned to Zhinu in the typed result envelope.
        }
    }

    private sealed class GenerationTerminalException(GenerationError error) : Exception(error.Message)
    {
        public GenerationError Error { get; } = error;
    }

    private sealed class GenerationPollingTimeoutException : Exception;
}
