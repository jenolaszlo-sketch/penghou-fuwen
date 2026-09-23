namespace Penghou.Fuwen;

/// <summary>Retry safety attested by the host for one read-tool execution.</summary>
public enum InferenceReadToolRetrySafety
{
    /// <summary>Repeating the same operation key does not add another effect.</summary>
    Safe,
    /// <summary>The tool must not be retried automatically.</summary>
    Unsafe,
}

/// <summary>Request sent to a host-owned exact read-tool executor.</summary>
public sealed class InferenceReadToolRequest
{
    /// <summary>Maximum UTF-8 bytes in a capability scope.</summary>
    public const int MaximumScopeUtf8Bytes = 256;
    /// <summary>Maximum UTF-8 bytes in a stable operation key.</summary>
    public const int MaximumOperationKeyUtf8Bytes = 256;

    /// <summary>Creates a detached read-tool execution request.</summary>
    public InferenceReadToolRequest(
        DescriptorReference tool,
        RuntimeValue arguments,
        string scope,
        string operationKey,
        InferenceReadToolRetrySafety retrySafety = InferenceReadToolRetrySafety.Safe,
        int? maximumResultUtf8Bytes = null)
    {
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        Arguments = RuntimeValueSnapshot.CloneRuntimeValue(arguments, nameof(arguments));
        Scope = RuntimeValueSnapshot.Text(scope, nameof(scope), MaximumScopeUtf8Bytes);
        OperationKey = RuntimeValueSnapshot.Text(operationKey, nameof(operationKey), MaximumOperationKeyUtf8Bytes);
        if (!Enum.IsDefined(retrySafety))
            throw new ArgumentOutOfRangeException(nameof(retrySafety));
        RetrySafety = retrySafety;
        if (maximumResultUtf8Bytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumResultUtf8Bytes));
        MaximumResultUtf8Bytes = maximumResultUtf8Bytes;
    }

    /// <summary>The exact admitted tool descriptor.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The deeply snapshotted typed bounded arguments.</summary>
    public RuntimeValue Arguments { get; }
    /// <summary>The capability/resource scope the host must authorize.</summary>
    public string Scope { get; }
    /// <summary>The stable idempotency identity for this tool operation.</summary>
    public string OperationKey { get; }
    /// <summary>The host-attested retry safety for this operation.</summary>
    public InferenceReadToolRetrySafety RetrySafety { get; }
    /// <summary>
    /// Maximum canonical result JSON bytes the host may return, when the plan
    /// declares a result ceiling. A host may enforce it before returning; the
    /// coordinator always enforces it before persisting.
    /// </summary>
    public int? MaximumResultUtf8Bytes { get; }
}

/// <summary>Bounded evidence for one read-tool execution.</summary>
public sealed class InferenceReadToolEvidence
{
    /// <summary>Creates detached read-tool evidence without retaining sensitive payloads.</summary>
    public InferenceReadToolEvidence(
        DescriptorReference tool,
        string operationKey,
        int? resultUtf8Bytes = null,
        long? durationMilliseconds = null)
    {
        Tool = ExecutionPortValidation.Descriptor(tool, DescriptorKind.Tool, nameof(tool));
        OperationKey = RuntimeValueSnapshot.Text(operationKey, nameof(operationKey), InferenceReadToolRequest.MaximumOperationKeyUtf8Bytes);
        if (resultUtf8Bytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(resultUtf8Bytes));
        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        ResultUtf8Bytes = resultUtf8Bytes;
        DurationMilliseconds = durationMilliseconds;
    }

    /// <summary>The exact executed tool descriptor.</summary>
    public DescriptorReference Tool { get; }
    /// <summary>The stable operation key that was executed.</summary>
    public string OperationKey { get; }
    /// <summary>UTF-8 length of the canonical result JSON, when recorded.</summary>
    public int? ResultUtf8Bytes { get; }
    /// <summary>Elapsed host time in milliseconds, when recorded.</summary>
    public long? DurationMilliseconds { get; }
}

/// <summary>Result returned by a host-owned read-tool executor.</summary>
public sealed class InferenceReadToolResult
{
    private InferenceReadToolResult(RuntimeValue? output, ExecutionFailure? failure, InferenceReadToolEvidence? evidence)
    {
        if ((output is null) == (failure is null))
            throw new ArgumentException("A read-tool result must contain exactly one of output or failure.");
        Output = output is null ? null : RuntimeValueSnapshot.CloneRuntimeValue(output, nameof(output));
        Failure = failure;
        Evidence = evidence;
    }

    /// <summary>Whether this result contains a successful output.</summary>
    public bool IsSuccess => Failure is null;
    /// <summary>The successful output, or null for a failure.</summary>
    public RuntimeValue? Output { get; }
    /// <summary>The failure, or null for a success.</summary>
    public ExecutionFailure? Failure { get; }
    /// <summary>Bounded evidence for the execution.</summary>
    public InferenceReadToolEvidence? Evidence { get; }

    /// <summary>Creates a successful result.</summary>
    public static InferenceReadToolResult Succeeded(RuntimeValue output, InferenceReadToolEvidence? evidence = null) =>
        new(output ?? throw new ArgumentNullException(nameof(output)), null, evidence);

    /// <summary>Creates a failed result.</summary>
    public static InferenceReadToolResult Failed(ExecutionFailure failure, InferenceReadToolEvidence? evidence = null) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)), evidence);
}

/// <summary>Executes one exact admitted read-tool request.</summary>
public interface IInferenceReadToolExecutor
{
    /// <summary>Executes a request without embedding retry, authorization, or scheduling policy.</summary>
    ValueTask<InferenceReadToolResult> ExecuteAsync(InferenceReadToolRequest request, CancellationToken cancellationToken = default);
}
