using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Canonicalization and fingerprint contracts supported by the Fuwen IR.</summary>
public static class FuwenContracts
{
    /// <summary>The current executable-plan contract.</summary>
    public const string IrVersion = "fuwen-ir/v1";
    /// <summary>The current compiler-semantics contract.</summary>
    public const string CompilerSemanticVersion = "compiler-semantics/1";
    /// <summary>The canonical JSON contract used by all currently supported IR versions.</summary>
    public const string CanonicalJsonVersion = "penghou-canonical-json/v1";
    /// <summary>The current execution fingerprint envelope.</summary>
    public const string ExecutionFingerprintVersion = "fuwen-execution/v1";
    /// <summary>The first canonical authored-source fingerprint envelope.</summary>
    public const string SourceFingerprintVersion = "fuwen-source/v1";
    /// <summary>The maximum persisted canonical IR size accepted by the core verifier.</summary>
    public const int MaximumCanonicalPlanBytes = 4 * 1024 * 1024;
}

/// <summary>Base contract for explicitly named executable IR nodes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ContextNode), "context")]
[JsonDerivedType(typeof(InferenceNode), "inference")]
[JsonDerivedType(typeof(ActivityNode), "activity")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(ReturnNode), "return")]
[JsonDerivedType(typeof(FanOutNode), "fan-out")]
[JsonDerivedType(typeof(RepeatNode), "repeat")]
[JsonDerivedType(typeof(CheckpointNode), "checkpoint")]
[JsonDerivedType(typeof(WaitNode), "wait")]
public abstract record WorkflowNode(string Name, string StructuralPath);

/// <summary>Resolves an immutable context snapshot.</summary>
public sealed record ContextNode(
    string Name,
    string StructuralPath,
    DescriptorReference Provider,
    IReadOnlyList<ArgumentBinding> Arguments,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>A named, typed dependency on the output of a direct context node.</summary>
public sealed record ContextRequirement(
    string Name,
    NodeOutputBinding Source,
    FuwenType ExpectedType);

/// <summary>
/// Executes structured or media inference through a resolved profile, either
/// with a registered prompt template and descriptor arguments or
/// with a workflow-owned prompt reference and typed prompt bindings.
/// Exactly one prompt source is set: <see cref="PromptTemplate"/> is null
/// if and only if <see cref="PromptName"/> names a plan prompt definition.
/// <see cref="Tools"/> lists the admitted model-callable tool descriptors;
/// null or empty means no model-callable tools.
/// </summary>
public sealed record InferenceNode(
    string Name,
    string StructuralPath,
    DescriptorReference Profile,
    DescriptorReference? PromptTemplate,
    IReadOnlyList<ArgumentBinding> Arguments,
    IReadOnlyList<NodeOutputBinding> ContextSnapshots,
    FuwenType OutputType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ContextRequirement>? ContextRequirements = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PromptName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PromptBinding>? PromptBindings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DescriptorReference>? Tools = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InferenceLimits? Limits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InferenceProtocol? Protocol = null) : WorkflowNode(Name, StructuralPath);

/// <summary>Executes one trusted catalogue activity.</summary>
public sealed record ActivityNode(
    string Name,
    string StructuralPath,
    DescriptorReference Activity,
    IReadOnlyList<ArgumentBinding> Arguments,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>Optional per-inference execution bounds. Null means unbounded.</summary>
/// <param name="MaxTokens">Maximum model output tokens.</param>
/// <param name="TimeoutSeconds">Wall-clock ceiling per attempt.</param>
public sealed record InferenceLimits(int? MaxTokens, int? TimeoutSeconds);

/// <summary>
/// Aggregate bounds for one inference node. These bounds cap the complete
/// activity, rather than a single model attempt. A null member means the
/// workflow does not declare that particular aggregate bound; adapters must
/// supply a finite host policy before executing a plan that omits a bound.
/// </summary>
public sealed record InferenceProtocolLimits
{
    /// <summary>Maximum model turns in the complete inference activity.</summary>
    public long? MaxTurns { get; }
    /// <summary>Maximum model calls in the complete inference activity.</summary>
    public long? MaxModelCalls { get; }
    /// <summary>Maximum tool calls in the complete inference activity.</summary>
    public long? MaxToolCalls { get; }
    /// <summary>Maximum prompt tokens consumed by the complete activity.</summary>
    public long? MaxPromptTokens { get; }
    /// <summary>Maximum completion tokens produced by the complete activity.</summary>
    public long? MaxCompletionTokens { get; }
    /// <summary>Maximum total tokens consumed by the complete activity.</summary>
    public long? MaxTotalTokens { get; }
    /// <summary>Maximum wall-clock duration in seconds for the complete activity.</summary>
    public long? MaxDurationMilliseconds { get; }
    /// <summary>Maximum aggregate tool-argument bytes for the complete activity.</summary>
    public long? MaxToolArgumentBytes { get; }
    /// <summary>Maximum aggregate tool-result bytes for the complete activity.</summary>
    public long? MaxToolResultBytes { get; }
    /// <summary>Maximum retained conversation bytes for the complete activity.</summary>
    public long? MaxRetainedConversationBytes { get; }
    /// <summary>Maximum retained evidence bytes for the complete activity.</summary>
    public long? MaxRetainedEvidenceBytes { get; }
    /// <summary>Optional aggregate monetary ceiling.</summary>
    public InferenceCostLimit? Cost { get; }

    /// <summary>Creates immutable aggregate bounds after validating each supplied bound.</summary>
    public InferenceProtocolLimits(
        long? maxTurns = null,
        long? maxModelCalls = null,
        long? maxToolCalls = null,
        long? maxPromptTokens = null,
        long? maxCompletionTokens = null,
        long? maxTotalTokens = null,
        long? maxDurationMilliseconds = null,
        long? maxToolArgumentBytes = null,
        long? maxToolResultBytes = null,
        long? maxRetainedConversationBytes = null,
        long? maxRetainedEvidenceBytes = null,
        InferenceCostLimit? cost = null)
    {
        MaxTurns = PositiveOrNull(maxTurns, nameof(maxTurns));
        MaxModelCalls = PositiveOrNull(maxModelCalls, nameof(maxModelCalls));
        MaxToolCalls = PositiveOrNull(maxToolCalls, nameof(maxToolCalls));
        MaxPromptTokens = PositiveOrNull(maxPromptTokens, nameof(maxPromptTokens));
        MaxCompletionTokens = PositiveOrNull(maxCompletionTokens, nameof(maxCompletionTokens));
        MaxTotalTokens = PositiveOrNull(maxTotalTokens, nameof(maxTotalTokens));
        MaxDurationMilliseconds = PositiveOrNull(maxDurationMilliseconds, nameof(maxDurationMilliseconds));
        MaxToolArgumentBytes = PositiveOrNull(maxToolArgumentBytes, nameof(maxToolArgumentBytes));
        MaxToolResultBytes = PositiveOrNull(maxToolResultBytes, nameof(maxToolResultBytes));
        MaxRetainedConversationBytes = PositiveOrNull(maxRetainedConversationBytes, nameof(maxRetainedConversationBytes));
        MaxRetainedEvidenceBytes = PositiveOrNull(maxRetainedEvidenceBytes, nameof(maxRetainedEvidenceBytes));
        Cost = cost;

        if (MaxPromptTokens is not null && MaxTotalTokens is not null && MaxPromptTokens > MaxTotalTokens)
            throw new ArgumentOutOfRangeException(nameof(maxPromptTokens), "prompt-token bound cannot exceed total-token bound");
        if (MaxCompletionTokens is not null && MaxTotalTokens is not null && MaxCompletionTokens > MaxTotalTokens)
            throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens), "completion-token bound cannot exceed total-token bound");
    }

    private const long MaximumAggregateLimit = 1_000_000_000_000_000;

    private static long? PositiveOrNull(long? value, string parameterName)
    {
        if (value is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "aggregate limits must be positive when specified");
        if (value > MaximumAggregateLimit)
            throw new ArgumentOutOfRangeException(parameterName, value, "aggregate limits exceed the supported bound");
        return value;
    }
}

/// <summary>Deterministic monetary ceiling for aggregate inference usage.</summary>
public sealed record InferenceCostLimit
{
    private const int MaximumTextUtf8Bytes = 256;

    /// <summary>ISO-like host currency identifier.</summary>
    public string Currency { get; }
    /// <summary>Maximum spend in the currency's fixed microunit.</summary>
    public long MaximumMicrounits { get; }

    /// <summary>Creates an immutable, bounded monetary ceiling.</summary>
    public InferenceCostLimit(string currency, long maximumMicrounits)
    {
        Currency = BoundedText(currency, nameof(currency));
        if (maximumMicrounits <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMicrounits), maximumMicrounits, "cost limits must be positive");
        MaximumMicrounits = maximumMicrounits;
    }

    private static string BoundedText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaximumTextUtf8Bytes)
            throw new ArgumentException($"{parameterName} exceeds the bounded UTF-8 length.", parameterName);
        return value;
    }
}

/// <summary>
/// Current implementation contract for bounded inference.
/// </summary>
public sealed record InferenceProtocol
{
    /// <summary>Immutable aggregate bounds for the logical inference activity.</summary>
    public InferenceProtocolLimits Limits { get; }

    /// <summary>Creates an immutable protocol contract.</summary>
    public InferenceProtocol(InferenceProtocolLimits limits)
    {
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }
}

/// <summary>The explicit merge declaration for a value-producing conditional.</summary>
public sealed record ConditionalMerge(
    Binding ThenValue,
    Binding ElseValue,
    FuwenType ResultType);

/// <summary>Selects one named lexical branch using a deterministic condition.</summary>
public sealed record ConditionalNode(
    string Name,
    string StructuralPath,
    ConditionExpression Condition,
    IReadOnlyList<WorkflowNode> Then,
    IReadOnlyList<WorkflowNode> Else,
    ConditionalMerge? Merge = null) : WorkflowNode(Name, StructuralPath);

/// <summary>Produces the typed workflow result.</summary>
public sealed record ReturnNode(
    string Name,
    string StructuralPath,
    Binding Value) : WorkflowNode(Name, StructuralPath);

/// <summary>The explicitly declared item binding for one keyed fan-out region.</summary>
public sealed record FanOutItemBinding(string Name, FuwenType Type);

/// <summary>References the current item while compiling a fan-out body.</summary>
public sealed record FanOutItemValueBinding(IReadOnlyList<string> Projection) : Binding;

/// <summary>References the current repeat state while compiling a repeat body.</summary>
public sealed record LoopStateBinding(IReadOnlyList<string> Projection) : Binding;

/// <summary>References the current repeat iteration number while compiling a repeat body.</summary>
public sealed record LoopIterationBinding(IReadOnlyList<string> Projection) : Binding;

/// <summary>
/// A bounded keyed fan-out region. Its source is evaluated once, keys are
/// validated before any body work starts, and yielded values are aggregated in
/// source order. Body references are closed over the item and earlier body
/// outputs only.
/// </summary>
public sealed record FanOutNode(
    string Name,
    string StructuralPath,
    Binding Source,
    FanOutItemBinding Item,
    Binding Key,
    IReadOnlyList<WorkflowNode> Body,
    Binding Yield,
    FuwenType ResultType,
    int MaximumItems,
    int MaximumConcurrency) : WorkflowNode(Name, StructuralPath);

/// <summary>A bounded repeat region with explicit carried state.</summary>
public sealed record RepeatNode(
    string Name,
    string StructuralPath,
    int MaxIterations,
    FuwenType StateType,
    Binding InitialState,
    IReadOnlyList<WorkflowNode> Body,
    Binding ContinueWith,
    ConditionExpression BreakWhen,
    FuwenType ResultType) : WorkflowNode(Name, StructuralPath);

/// <summary>Durable state checkpoint without suspension.</summary>
public sealed record CheckpointNode(
    string Name,
    string StructuralPath,
    Binding Value,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>Suspends until an external signal delivers a typed payload.</summary>
public sealed record WaitNode(
    string Name,
    string StructuralPath,
    string SignalName,
    FuwenType OutputType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TimeoutSeconds = null) : WorkflowNode(Name, StructuralPath);

/// <summary>The typed outcome of an approval gate signal.</summary>
public enum ApprovalOutcome
{
    /// <summary>The supervisor approved the request.</summary>
    Approved,
    /// <summary>The supervisor denied the request.</summary>
    Denied,
}

/// <summary>The explicit completion schedule for a workflow.</summary>
public sealed record WorkflowExecutionOrder(
    IReadOnlyList<WorkflowExecutionRegion> Regions);

/// <summary>A lexical workflow or structured-branch execution region.</summary>
public sealed record WorkflowExecutionRegion(
    string RegionPath,
    IReadOnlyList<WorkflowExecutionPhase> Phases);

/// <summary>A completion barrier containing unordered directly-owned node paths.</summary>
public sealed record WorkflowExecutionPhase(
    IReadOnlyList<string> NodePaths);

/// <summary>An immutable, completely resolved executable plan.</summary>
public sealed record WorkflowPlan(
    string IrVersion,
    string LanguageVersion,
    string CompilerSemanticVersion,
    string CanonicalJsonVersion,
    string FingerprintVersion,
    string Name,
    string Revision,
    FuwenType InputType,
    FuwenType OutputType,
    string RoutingPolicyRevision,
    IReadOnlyList<ResolvedSchemaDefinition> Schemas,
    IReadOnlyList<DescriptorReference> CatalogueBindings,
    CapabilityManifest CapabilityManifest,
    IReadOnlyList<WorkflowNode> Nodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkflowExecutionOrder? ExecutionOrder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PromptDefinition>? Prompts = null);
