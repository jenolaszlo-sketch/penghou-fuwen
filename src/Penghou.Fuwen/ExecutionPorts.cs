using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Penghou.Fuwen;

/// <summary>A bounded, detached named runtime argument supplied to an execution port.</summary>
public sealed class RuntimeArgument
{
    /// <summary>The maximum UTF-8 length of an argument name.</summary>
    public const int MaximumNameUtf8Bytes = 256;

    /// <summary>Creates a deeply detached runtime argument.</summary>
    public RuntimeArgument(string name, RuntimeValue value)
    {
        Name = RuntimeValueSnapshot.Text(name, nameof(name), MaximumNameUtf8Bytes);
        Value = RuntimeValueSnapshot.CloneRuntimeValue(value, nameof(value));
    }

    /// <summary>The stable argument name.</summary>
    public string Name { get; }
    /// <summary>The bounded, deeply snapshotted argument value.</summary>
    public RuntimeValue Value { get; }
}

/// <summary>Base identity shared by provider-neutral execution requests.</summary>
public abstract class ExecutionRequest
{
    /// <summary>The durable invocation and operation identity for this call.</summary>
    public ExecutionInvocation Invocation { get; }
    /// <summary>The deeply snapshotted named input arguments.</summary>
    public IReadOnlyList<RuntimeArgument> Arguments { get; }
    /// <summary>The exact declared output type expected from the provider.</summary>
    public FuwenType OutputType { get; }

    /// <summary>Maximum named arguments accepted by one execution request.</summary>
    public const int MaximumArguments = 256;

    /// <summary>Initializes a request identity and bounded argument list.</summary>
    protected ExecutionRequest(
        ExecutionInvocation invocation,
        IReadOnlyList<RuntimeArgument> arguments,
        FuwenType outputType)
    {
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        Arguments = ExecutionPortValidation.CloneArguments(arguments);
        OutputType = WorkflowPlanSnapshot.CreateType(outputType);
    }
}

/// <summary>Request sent to a registered activity executor.</summary>
public sealed class ActivityExecutionRequest : ExecutionRequest
{
    /// <summary>Creates an activity execution request.</summary>
    public ActivityExecutionRequest(
        ExecutionInvocation invocation,
        DescriptorReference activity,
        IReadOnlyList<RuntimeArgument> arguments,
        FuwenType outputType)
        : base(invocation, arguments, outputType)
    {
        Activity = ExecutionPortValidation.Descriptor(activity, DescriptorKind.Activity, nameof(activity));
    }

    /// <summary>The exact admitted activity descriptor.</summary>
    public DescriptorReference Activity { get; }
}

/// <summary>Request sent to a durable context provider.</summary>
public sealed class ContextExecutionRequest : ExecutionRequest
{
    /// <summary>Creates a context-provider execution request.</summary>
    public ContextExecutionRequest(
        ExecutionInvocation invocation,
        DescriptorReference provider,
        IReadOnlyList<RuntimeArgument> arguments,
        FuwenType outputType)
        : base(invocation, arguments, outputType)
    {
        Provider = ExecutionPortValidation.Descriptor(provider, DescriptorKind.ContextProvider, nameof(provider));
    }

    /// <summary>The exact admitted context-provider descriptor.</summary>
    public DescriptorReference Provider { get; }
}

/// <summary>A typed context value and the required immutable snapshot evidence that produced it.</summary>
public sealed class InferenceContextInput
{
    /// <summary>The maximum UTF-8 length of a context requirement name.</summary>
    public const int MaximumNameUtf8Bytes = 256;

    /// <summary>
    /// Creates an inference context input. The value and snapshot are both
    /// required: a snapshot reference is evidence and is not a substitute for
    /// the typed value consumed by the inference provider.
    /// </summary>
    public InferenceContextInput(
        string name,
        FuwenType expectedType,
        RuntimeValue value,
        ContextSnapshotReference contextSnapshot)
    {
        Name = RuntimeValueSnapshot.Text(name, nameof(name), MaximumNameUtf8Bytes);
        ExpectedType = WorkflowPlanSnapshot.CreateType(expectedType);
        Value = RuntimeValueSnapshot.CloneRuntimeValue(value, nameof(value));
        ContextSnapshot = RuntimeValueSnapshot.CloneContextSnapshot(contextSnapshot);
    }

    /// <summary>The declared context-requirement name.</summary>
    public string Name { get; }
    /// <summary>The exact type declared by the compiled context requirement.</summary>
    public FuwenType ExpectedType { get; }
    /// <summary>The typed context value consumed by inference.</summary>
    public RuntimeValue Value { get; }
    /// <summary>The required immutable snapshot evidence for <see cref="Value"/>.</summary>
    public ContextSnapshotReference ContextSnapshot { get; }
    /// <summary>Alias for <see cref="ContextSnapshot"/> for adapter readability.</summary>
    public ContextSnapshotReference SnapshotReference => ContextSnapshot;
}

/// <summary>Request sent to a provider-neutral inference executor.</summary>
public sealed class InferenceExecutionRequest : ExecutionRequest
{
    /// <summary>Maximum context requirements accepted by one inference request.</summary>
    public const int MaximumContextInputs = 256;

    private readonly IReadOnlyList<InferenceContextInput> contextInputs;

    /// <summary>Creates an inference execution request.</summary>
    public InferenceExecutionRequest(
        ExecutionInvocation invocation,
        DescriptorReference profile,
        DescriptorReference promptTemplate,
        IReadOnlyList<RuntimeArgument> arguments,
        IReadOnlyList<InferenceContextInput> contextInputs,
        FuwenType outputType)
        : base(invocation, arguments, outputType)
    {
        Profile = ExecutionPortValidation.Descriptor(profile, DescriptorKind.InferenceProfile, nameof(profile));
        PromptTemplate = ExecutionPortValidation.Descriptor(promptTemplate, DescriptorKind.PromptTemplate, nameof(promptTemplate));
        ArgumentNullException.ThrowIfNull(contextInputs);
        if (contextInputs.Count > MaximumContextInputs)
            throw new ArgumentOutOfRangeException(nameof(contextInputs), $"An inference request supports at most {MaximumContextInputs} context inputs.");

        var copy = new InferenceContextInput[contextInputs.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < contextInputs.Count; i++)
        {
            var input = contextInputs[i] ?? throw new ArgumentException("Context inputs cannot contain null values.", nameof(contextInputs));
            if (!names.Add(input.Name))
                throw new ArgumentException("Inference context input names must be unique.", nameof(contextInputs));
            copy[i] = new InferenceContextInput(input.Name, input.ExpectedType, input.Value, input.ContextSnapshot);
        }
        contextInputs = Array.AsReadOnly(copy);
        this.contextInputs = contextInputs;
    }

    /// <summary>The exact admitted inference requirements profile.</summary>
    public DescriptorReference Profile { get; }
    /// <summary>The exact admitted prompt-template descriptor.</summary>
    public DescriptorReference PromptTemplate { get; }
    /// <summary>The required typed context inputs and their snapshot evidence.</summary>
    public IReadOnlyList<InferenceContextInput> ContextInputs => contextInputs;
    /// <summary>Alias for <see cref="ContextInputs"/>.</summary>
    public IReadOnlyList<InferenceContextInput> Context => contextInputs;
}

/// <summary>Stable broad class of an execution failure.</summary>
public enum ExecutionFailureKind
{
    /// <summary>Admission, policy, or descriptor resolution rejected execution.</summary>
    Admission,
    /// <summary>The request or typed binding violates the admitted contract.</summary>
    Contract,
    /// <summary>The provider response could not satisfy its declared representation.</summary>
    ProviderOutput,
    /// <summary>The provider or host could not complete the operation.</summary>
    Provider,
    /// <summary>Transient execution infrastructure failed.</summary>
    Infrastructure,
    /// <summary>The operation exceeded a host or provider time limit.</summary>
    Timeout,
    /// <summary>Cancellation stopped the operation.</summary>
    Cancelled,
    /// <summary>Non-authoritative observation delivery failed.</summary>
    Observation,
    /// <summary>The provider returned an unclassified failure.</summary>
    Unknown,
}

/// <summary>Stable provider-neutral reason for an execution failure.</summary>
public enum ExecutionFailureCode
{
    /// <summary>The workflow or request was not admitted.</summary>
    NotAdmitted,
    /// <summary>The registered descriptor does not exactly match the admitted descriptor.</summary>
    DescriptorUnavailable,
    /// <summary>Host policy rejected the request.</summary>
    PolicyRejected,
    /// <summary>The effective input is invalid.</summary>
    InvalidInput,
    /// <summary>A compiled binding could not be evaluated.</summary>
    BindingFailure,
    /// <summary>A provider output has the wrong declared type.</summary>
    OutputTypeMismatch,
    /// <summary>Context value and snapshot evidence do not agree.</summary>
    ContextSnapshotMismatch,
    /// <summary>The provider returned malformed structured output.</summary>
    MalformedOutput,
    /// <summary>Repair produced structured data that still fails its schema.</summary>
    RepairedOutputSchemaInvalid,
    /// <summary>The provider output fails its declared schema.</summary>
    SchemaMismatch,
    /// <summary>A model tool result could not be mapped to its declared contract.</summary>
    ToolMappingFailure,
    /// <summary>The provider output was truncated.</summary>
    TruncatedOutput,
    /// <summary>The provider reported a failure not represented more narrowly.</summary>
    ProviderError,
    /// <summary>Transient infrastructure failed before an effect could commit.</summary>
    TransientInfrastructureFailure,
    /// <summary>The operation exceeded its time limit.</summary>
    Timeout,
    /// <summary>The execution lease or fence is no longer authoritative.</summary>
    FencingLost,
    /// <summary>Cancellation stopped the operation.</summary>
    Cancelled,
    /// <summary>Publishing or verifying an artifact was rejected.</summary>
    PublicationRejected,
    /// <summary>Non-authoritative observation delivery failed.</summary>
    ObserverFailure,
    /// <summary>The failure is not classified by this contract version.</summary>
    Unknown,
}

/// <summary>Whether a durable host may retry an execution failure.</summary>
public enum ExecutionRetryDisposition
{
    /// <summary>The failure must not be retried by the durable host.</summary>
    Never,
    /// <summary>Only an infrastructure retry of the same invocation is permitted.</summary>
    InfrastructureOnly,
}

/// <summary>A bounded, provider-neutral execution failure.</summary>
public sealed class ExecutionFailure
{
    /// <summary>Maximum UTF-8 length of a failure message.</summary>
    public const int MaximumMessageUtf8Bytes = 4096;
    /// <summary>Maximum UTF-8 length of a provider-specific failure code.</summary>
    public const int MaximumProviderCodeUtf8Bytes = 256;

    /// <summary>Creates a bounded failure and validates retry/effect semantics.</summary>
    public ExecutionFailure(
        ExecutionFailureKind kind,
        ExecutionFailureCode code,
        string message,
        ExecutionRetryDisposition retryDisposition = ExecutionRetryDisposition.Never,
        bool mayHaveCommittedEffect = false,
        string? providerCode = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        if (!Enum.IsDefined(retryDisposition)) throw new ArgumentOutOfRangeException(nameof(retryDisposition));
        if (!CodeMatchesKind(code, kind))
            throw new ArgumentException($"Failure code '{code}' does not belong to failure kind '{kind}'.", nameof(code));
        if (retryDisposition == ExecutionRetryDisposition.InfrastructureOnly && mayHaveCommittedEffect)
            throw new ArgumentException("A failure that may have committed an effect cannot permit infrastructure retry.", nameof(retryDisposition));
        if (retryDisposition == ExecutionRetryDisposition.InfrastructureOnly && code != ExecutionFailureCode.TransientInfrastructureFailure)
            throw new ArgumentException("Only a transient infrastructure failure can permit infrastructure retry.", nameof(retryDisposition));

        Kind = kind;
        Code = code;
        Message = RuntimeValueSnapshot.Text(message, nameof(message), MaximumMessageUtf8Bytes);
        ProviderCode = RuntimeValueSnapshot.OptionalText(providerCode, nameof(providerCode), MaximumProviderCodeUtf8Bytes);
        RetryDisposition = retryDisposition;
        MayHaveCommittedEffect = mayHaveCommittedEffect;
    }

    /// <summary>The stable broad failure class.</summary>
    public ExecutionFailureKind Kind { get; }
    /// <summary>The stable provider-neutral failure code.</summary>
    public ExecutionFailureCode Code { get; }
    /// <summary>A bounded diagnostic message suitable for logs and operators.</summary>
    public string Message { get; }
    /// <summary>An optional bounded provider-specific code.</summary>
    public string? ProviderCode { get; }
    /// <summary>The only retry disposition exposed by the execution seam.</summary>
    public ExecutionRetryDisposition RetryDisposition { get; }
    /// <summary>Whether the provider may have performed the requested effect.</summary>
    public bool MayHaveCommittedEffect { get; }

    private static bool CodeMatchesKind(ExecutionFailureCode code, ExecutionFailureKind kind) => code switch
    {
        ExecutionFailureCode.NotAdmitted or ExecutionFailureCode.DescriptorUnavailable or ExecutionFailureCode.PolicyRejected
            => kind == ExecutionFailureKind.Admission,
        ExecutionFailureCode.InvalidInput or ExecutionFailureCode.BindingFailure or ExecutionFailureCode.OutputTypeMismatch or
        ExecutionFailureCode.ContextSnapshotMismatch or ExecutionFailureCode.PublicationRejected
            => kind == ExecutionFailureKind.Contract,
        ExecutionFailureCode.MalformedOutput or ExecutionFailureCode.RepairedOutputSchemaInvalid or ExecutionFailureCode.SchemaMismatch or
        ExecutionFailureCode.ToolMappingFailure or ExecutionFailureCode.TruncatedOutput
            => kind == ExecutionFailureKind.ProviderOutput,
        ExecutionFailureCode.ProviderError => kind == ExecutionFailureKind.Provider,
        ExecutionFailureCode.TransientInfrastructureFailure or ExecutionFailureCode.FencingLost
            => kind == ExecutionFailureKind.Infrastructure,
        ExecutionFailureCode.Timeout => kind == ExecutionFailureKind.Timeout,
        ExecutionFailureCode.Cancelled => kind == ExecutionFailureKind.Cancelled,
        ExecutionFailureCode.ObserverFailure => kind == ExecutionFailureKind.Observation,
        ExecutionFailureCode.Unknown => kind == ExecutionFailureKind.Unknown,
        _ => false,
    };
}

/// <summary>Non-authoritative execution observation kind.</summary>
public enum ExecutionObservationKind
{
    /// <summary>The host submitted an execution request.</summary>
    Requested,
    /// <summary>The provider returned a successful result.</summary>
    Succeeded,
    /// <summary>The provider returned a failure result.</summary>
    Failed,
}

/// <summary>A deliberately minimal, non-authoritative observation payload.</summary>
public sealed class ExecutionObservation
{
    /// <summary>Creates an observation for one invocation.</summary>
    public ExecutionObservation(ExecutionInvocation invocation, ExecutionObservationKind kind)
    {
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    /// <summary>The observed invocation identity.</summary>
    public ExecutionInvocation Invocation { get; }
    /// <summary>The observed lifecycle event.</summary>
    public ExecutionObservationKind Kind { get; }
}

/// <summary>Base contract for typed execution results.</summary>
public abstract class ExecutionResult
{
    private protected ExecutionResult(RuntimeValue? output, ExecutionFailure? failure, IReadOnlyList<ArtifactPublicationReceipt> publications)
    {
        if ((output is null) == (failure is null))
            throw new ArgumentException("An execution result must contain exactly one of output or failure.");
        Output = output is null ? null : RuntimeValueSnapshot.CloneRuntimeValue(output, nameof(output));
        Failure = failure;
        Publications = ExecutionPortValidation.CloneReceipts(publications);
    }

    /// <summary>Whether this result contains a successful output.</summary>
    public bool IsSuccess => Failure is null;
    /// <summary>The successful output, or null for a failure.</summary>
    public RuntimeValue? Output { get; }
    /// <summary>The failure, or null for a success.</summary>
    public ExecutionFailure? Failure { get; }
    /// <summary>Verified artifact publication evidence returned with the output.</summary>
    public IReadOnlyList<ArtifactPublicationReceipt> Publications { get; }
}

/// <summary>Result returned by an activity executor.</summary>
public sealed class ActivityExecutionResult : ExecutionResult
{
    /// <summary>Creates a successful activity result.</summary>
    public ActivityExecutionResult(RuntimeValue output, IReadOnlyList<ArtifactPublicationReceipt>? publications = null)
        : base(output ?? throw new ArgumentNullException(nameof(output)), null, publications ?? Array.Empty<ArtifactPublicationReceipt>()) { }

    /// <summary>Creates a failed activity result.</summary>
    public ActivityExecutionResult(ExecutionFailure failure)
        : base(null, failure ?? throw new ArgumentNullException(nameof(failure)), Array.Empty<ArtifactPublicationReceipt>()) { }

    /// <summary>Creates a successful result.</summary>
    public static ActivityExecutionResult Succeeded(RuntimeValue output, IReadOnlyList<ArtifactPublicationReceipt>? publications = null) => new(output, publications);
    /// <summary>Creates a failed result.</summary>
    public static ActivityExecutionResult Failed(ExecutionFailure failure) => new(failure);
}

/// <summary>Result returned by a context provider.</summary>
public sealed class ContextExecutionResult : ExecutionResult
{
    /// <summary>Creates a successful context result with immutable snapshot evidence.</summary>
    public ContextExecutionResult(RuntimeValue output, ContextSnapshotReference contextSnapshot)
        : base(output ?? throw new ArgumentNullException(nameof(output)), null, Array.Empty<ArtifactPublicationReceipt>())
    {
        ContextSnapshot = RuntimeValueSnapshot.CloneContextSnapshot(contextSnapshot);
    }

    /// <summary>Creates a failed context result.</summary>
    public ContextExecutionResult(ExecutionFailure failure)
        : base(null, failure ?? throw new ArgumentNullException(nameof(failure)), Array.Empty<ArtifactPublicationReceipt>()) { }

    /// <summary>The immutable evidence for the context output.</summary>
    public ContextSnapshotReference? ContextSnapshot { get; }
    /// <summary>Creates a successful result.</summary>
    public static ContextExecutionResult Succeeded(RuntimeValue output, ContextSnapshotReference contextSnapshot) => new(output, contextSnapshot);
    /// <summary>Creates a failed result.</summary>
    public static ContextExecutionResult Failed(ExecutionFailure failure) => new(failure);
}

/// <summary>Result returned by an inference executor.</summary>
public sealed class InferenceExecutionResult : ExecutionResult
{
    /// <summary>Creates a successful inference result.</summary>
    public InferenceExecutionResult(
        RuntimeValue output,
        IReadOnlyList<ArtifactPublicationReceipt>? publications = null,
        InferenceExecutionEvidence? evidence = null)
        : base(output ?? throw new ArgumentNullException(nameof(output)), null, publications ?? Array.Empty<ArtifactPublicationReceipt>())
    {
        Evidence = evidence;
    }

    /// <summary>Creates a failed inference result.</summary>
    public InferenceExecutionResult(ExecutionFailure failure, InferenceExecutionEvidence? evidence = null)
        : base(null, failure ?? throw new ArgumentNullException(nameof(failure)), Array.Empty<ArtifactPublicationReceipt>())
    {
        Evidence = evidence;
    }

    /// <summary>Bounded provider-neutral evidence for the inference attempt.</summary>
    public InferenceExecutionEvidence? Evidence { get; }

    /// <summary>Creates a successful result.</summary>
    public static InferenceExecutionResult Succeeded(
        RuntimeValue output,
        IReadOnlyList<ArtifactPublicationReceipt>? publications = null,
        InferenceExecutionEvidence? evidence = null) => new(output, publications, evidence);
    /// <summary>Creates a failed result.</summary>
    public static InferenceExecutionResult Failed(ExecutionFailure failure, InferenceExecutionEvidence? evidence = null) => new(failure, evidence);
}

/// <summary>A bounded, detached provider-neutral inference provenance record.</summary>
public sealed class InferenceExecutionEvidence
{
    /// <summary>The maximum number of provider attempts retained.</summary>
    public const int MaximumAttempts = 32;
    /// <summary>The maximum UTF-8 length of a provider or model identity.</summary>
    public const int MaximumIdentityUtf8Bytes = 256;
    /// <summary>The maximum UTF-8 length of a diagnostic value.</summary>
    public const int MaximumDiagnosticUtf8Bytes = 1024;

    /// <summary>Creates detached evidence for one inference execution.</summary>
    public InferenceExecutionEvidence(
        DescriptorReference profile,
        DescriptorReference promptTemplate,
        IReadOnlyList<InferenceAttemptEvidence> attempts,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        bool wasRepaired = false,
        int repairAttempts = 0,
        string? repairStrategy = null,
        string? policyRevision = null,
        string? routingPolicyRevision = null,
        long? durationMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(promptTemplate);
        if (profile.Kind != DescriptorKind.InferenceProfile)
            throw new ArgumentException("Inference evidence profile must be an InferenceProfile descriptor.", nameof(profile));
        if (promptTemplate.Kind != DescriptorKind.PromptTemplate)
            throw new ArgumentException("Inference evidence prompt template must be a PromptTemplate descriptor.", nameof(promptTemplate));
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (repairAttempts < 0 || repairAttempts > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(repairAttempts));
        if (promptTokens is < 0 || completionTokens is < 0 || totalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(totalTokens));
        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));

        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index] ??
                throw new ArgumentException("Inference attempts cannot contain null values.", nameof(attempts));
            if (attempt.Attempt != index + 1)
                throw new ArgumentException("Inference attempt numbers must be contiguous and one-based.", nameof(attempts));
            if (attempt.Succeeded && (attempt.FailureCode is not null || attempt.FailureMessage is not null))
                throw new ArgumentException("A successful inference attempt cannot carry failure diagnostics.", nameof(attempts));
            if (!attempt.Succeeded && attempt.FailureCode is null)
                throw new ArgumentException("A failed inference attempt must carry a stable failure code.", nameof(attempts));
            if (attempt.Succeeded && index != attempts.Count - 1)
                throw new ArgumentException("Only the final inference attempt may be successful.", nameof(attempts));
        }

        Profile = RuntimeValueSnapshot.CloneDescriptor(profile);
        PromptTemplate = RuntimeValueSnapshot.CloneDescriptor(promptTemplate);
        Attempts = Array.AsReadOnly(attempts.Select(static attempt => new InferenceAttemptEvidence(
            attempt.Attempt,
            attempt.Provider,
            attempt.Model,
            attempt.EndpointId,
            attempt.Succeeded,
            attempt.FailureCode,
            attempt.FailureMessage)).ToArray());
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        WasRepaired = wasRepaired;
        RepairAttempts = repairAttempts;
        RepairStrategy = RuntimeValueSnapshot.OptionalText(repairStrategy, nameof(repairStrategy), MaximumDiagnosticUtf8Bytes);
        PolicyRevision = RuntimeValueSnapshot.OptionalText(policyRevision, nameof(policyRevision), MaximumIdentityUtf8Bytes);
        RoutingPolicyRevision = RuntimeValueSnapshot.OptionalText(routingPolicyRevision, nameof(routingPolicyRevision), MaximumIdentityUtf8Bytes);
        DurationMilliseconds = durationMilliseconds;
    }

    /// <summary>The exact admitted logical profile descriptor.</summary>
    public DescriptorReference Profile { get; }
    /// <summary>The exact admitted prompt-template descriptor.</summary>
    public DescriptorReference PromptTemplate { get; }
    /// <summary>The ordered provider/model attempts made by the adapter.</summary>
    public IReadOnlyList<InferenceAttemptEvidence> Attempts { get; }
    /// <summary>Input tokens reported by the provider, when available.</summary>
    public int? PromptTokens { get; }
    /// <summary>Output tokens reported by the provider, when available.</summary>
    public int? CompletionTokens { get; }
    /// <summary>Total tokens reported by the provider, when available.</summary>
    public int? TotalTokens { get; }
    /// <summary>Whether Nuwa or another adapter repair changed the output.</summary>
    public bool WasRepaired { get; }
    /// <summary>The number of deterministic repair passes recorded.</summary>
    public int RepairAttempts { get; }
    /// <summary>The final repair strategy, when reported.</summary>
    public string? RepairStrategy { get; }
    /// <summary>The host policy revision used to authorize this request.</summary>
    public string? PolicyRevision { get; }
    /// <summary>The host routing policy revision used for endpoint selection.</summary>
    public string? RoutingPolicyRevision { get; }
    /// <summary>Elapsed provider/adapter time in milliseconds, when available.</summary>
    public long? DurationMilliseconds { get; }
}

/// <summary>One bounded provider-neutral attempt in inference evidence.</summary>
public sealed class InferenceAttemptEvidence
{
    /// <summary>Creates one detached provider attempt record.</summary>
    public InferenceAttemptEvidence(
        int attempt,
        string provider,
        string model,
        string? endpointId,
        bool succeeded,
        string? failureCode = null,
        string? failureMessage = null)
    {
        if (attempt < 1 || attempt > InferenceExecutionEvidence.MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        Attempt = attempt;
        Provider = RuntimeValueSnapshot.Text(provider, nameof(provider), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Model = RuntimeValueSnapshot.Text(model, nameof(model), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        EndpointId = RuntimeValueSnapshot.OptionalText(endpointId, nameof(endpointId), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Succeeded = succeeded;
        FailureCode = RuntimeValueSnapshot.OptionalText(failureCode, nameof(failureCode), InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes);
        FailureMessage = RuntimeValueSnapshot.OptionalText(failureMessage, nameof(failureMessage), InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes);
    }

    /// <summary>One-based attempt number.</summary>
    public int Attempt { get; }
    /// <summary>Provider identity actually used.</summary>
    public string Provider { get; }
    /// <summary>Model identity actually used.</summary>
    public string Model { get; }
    /// <summary>Provider endpoint identity, when available.</summary>
    public string? EndpointId { get; }
    /// <summary>Whether this attempt produced the accepted output.</summary>
    public bool Succeeded { get; }
    /// <summary>Provider-neutral failure code, when the attempt failed.</summary>
    public string? FailureCode { get; }
    /// <summary>Bounded failure detail, when the attempt failed.</summary>
    public string? FailureMessage { get; }
}

/// <summary>Executes one admitted activity request.</summary>
public interface IActivityExecutor
{
    /// <summary>Executes a request without embedding retry, authorization, or scheduling policy.</summary>
    ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Resolves one admitted context request.</summary>
public interface IContextProvider
{
    /// <summary>Resolves a request without embedding retry, authorization, or scheduling policy.</summary>
    ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Executes one admitted inference request.</summary>
public interface IInferenceExecutor
{
    /// <summary>Executes a request without embedding retry, authorization, or routing policy.</summary>
    ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Receives non-authoritative lifecycle observations from the host.</summary>
public interface IExecutionObserver
{
    /// <summary>Publishes one bounded observation; this method does not authorize or alter execution.</summary>
    ValueTask ObserveAsync(ExecutionObservation observation, CancellationToken cancellationToken = default);
}

internal static class ExecutionPortValidation
{
    internal static DescriptorReference Descriptor(DescriptorReference descriptor, DescriptorKind expected, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(descriptor, parameterName);
        if (descriptor.Kind != expected)
            throw new ArgumentException($"Descriptor must be of kind '{expected}', not '{descriptor.Kind}'.", parameterName);
        return RuntimeValueSnapshot.CloneDescriptor(descriptor);
    }

    internal static IReadOnlyList<RuntimeArgument> CloneArguments(IReadOnlyList<RuntimeArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > ExecutionRequest.MaximumArguments)
            throw new ArgumentOutOfRangeException(nameof(arguments), $"A request supports at most {ExecutionRequest.MaximumArguments} arguments.");
        var copy = new RuntimeArgument[arguments.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i] ?? throw new ArgumentException("Arguments cannot contain null values.", nameof(arguments));
            if (!names.Add(argument.Name))
                throw new ArgumentException("Argument names must be unique.", nameof(arguments));
            copy[i] = new RuntimeArgument(argument.Name, argument.Value);
        }
        return Array.AsReadOnly(copy);
    }

    internal static IReadOnlyList<ArtifactPublicationReceipt> CloneReceipts(IReadOnlyList<ArtifactPublicationReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        const int maximumReceipts = 256;
        if (receipts.Count > maximumReceipts)
            throw new ArgumentOutOfRangeException(nameof(receipts), $"A result supports at most {maximumReceipts} publication receipts.");
        var copy = new ArtifactPublicationReceipt[receipts.Count];
        for (var i = 0; i < receipts.Count; i++)
        {
            var receipt = receipts[i] ?? throw new ArgumentException("Publication receipts cannot contain null values.", nameof(receipts));
            if (!Enum.IsDefined(receipt.Disposition))
                throw new ArgumentOutOfRangeException(nameof(receipts), "Publication receipt disposition is invalid.");
            copy[i] = new ArtifactPublicationReceipt(
                RuntimeValueSnapshot.Text(receipt.IdempotencyKey, nameof(receipt.IdempotencyKey), 1024),
                RuntimeValueSnapshot.CloneArtifact(receipt.Artifact),
                RuntimeValueSnapshot.Text(receipt.ProviderReceiptId, nameof(receipt.ProviderReceiptId), 1024),
                receipt.Disposition);
        }
        return Array.AsReadOnly(copy);
    }
}
