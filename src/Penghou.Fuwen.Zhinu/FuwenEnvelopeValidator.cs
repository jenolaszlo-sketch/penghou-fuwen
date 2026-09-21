using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Validates durable provider outcomes before the interpreter consumes them.</summary>
internal static class FuwenEnvelopeValidator
{
    internal static void ThrowIfFailed(NodeExecutionEnvelope envelope, string nodePath)
    {
        if (envelope.Failure is not null)
            throw new FuwenZhinuExecutionException(envelope.Failure);
        if (envelope.Output is null)
            throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' has no output.");
    }

    internal static NodeExecutionEnvelope Read(
        JsonElement value,
        string nodePath,
        WorkflowPlan plan,
        string executionFingerprint,
        string effectiveRequestFingerprint,
        Guid workflowRunId,
        IReadOnlySet<string> priorExecutionFingerprints)
    {
        try
        {
            var envelope = CanonicalJson.Deserialize<NodeExecutionEnvelope>(CanonicalJson.Canonicalize(value));
            if (envelope is null)
                throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' is null.");

            var invocation = envelope.Invocation;
            var isCurrentPlan =
                string.Equals(invocation?.ExecutionFingerprint, executionFingerprint, StringComparison.Ordinal);
            var isDeclaredReusable = invocation?.ExecutionFingerprint is not null &&
                priorExecutionFingerprints.Contains(invocation.ExecutionFingerprint);
            var isPriorPlan = !isCurrentPlan && isDeclaredReusable;
            // Reused fork evidence names its source run. Cross-plan reuse must
            // name an accepted prior fingerprint; same-plan reuse is allowed
            // only when the host explicitly lists the current fingerprint.
            var runtimeMatches = string.Equals(invocation?.RuntimePath, $"{workflowRunId:D}/{nodePath}", StringComparison.Ordinal) ||
                (isDeclaredReusable && invocation!.RuntimePath.EndsWith("/" + nodePath, StringComparison.Ordinal));
            if (invocation is null ||
                (!isCurrentPlan && !isPriorPlan) ||
                !string.Equals(invocation.StructuralPath, nodePath, StringComparison.Ordinal) ||
                !string.Equals(invocation.EffectiveRequestFingerprint, effectiveRequestFingerprint, StringComparison.Ordinal) ||
                !runtimeMatches)
                throw new FuwenZhinuExecutionException(
                    $"Persisted result for '{nodePath}' has invocation evidence that does not match the current step.");

            if (envelope.Publications is null)
                throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' has no publication evidence list.");
            foreach (var receipt in envelope.Publications)
            {
                if (receipt is null)
                    throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' contains a null publication receipt.");
                if (!string.Equals(receipt.IdempotencyKey, invocation.OperationKey, StringComparison.Ordinal))
                    throw new FuwenZhinuExecutionException(
                        $"Persisted publication receipt '{receipt.ProviderReceiptId}' is not bound to the invocation operation key.");
                if (!plan.CatalogueBindings.Contains(receipt.Artifact.ArtifactDescriptor))
                    throw new FuwenZhinuExecutionException(
                        $"Persisted publication receipt '{receipt.ProviderReceiptId}' uses an artifact descriptor that was not admitted by the workflow plan.");
            }

            var inferenceNode = FlattenNodes(plan.Nodes)
                .OfType<InferenceNode>()
                .FirstOrDefault(inference => string.Equals(inference.StructuralPath, nodePath, StringComparison.Ordinal));
            DescriptorReference? expectedPromptTemplate = inferenceNode?.PromptTemplate;
            string? expectedPromptDigest = null;
            if (inferenceNode?.PromptName is not null)
            {
                var prompt = plan.Prompts!.Single(candidate =>
                    string.Equals(candidate.Name, inferenceNode.PromptName, StringComparison.Ordinal));
                expectedPromptTemplate = prompt.RegisteredSource;
                if (expectedPromptTemplate is null)
                    expectedPromptDigest = prompt.GetSemanticDigest();
            }
            if (envelope.Evidence is not null &&
                (inferenceNode is null ||
                 envelope.Evidence.Profile != inferenceNode.Profile ||
                 envelope.Evidence.PromptTemplate != expectedPromptTemplate ||
                 !string.Equals(envelope.Evidence.PromptDigest, expectedPromptDigest, StringComparison.Ordinal)))
                throw new FuwenZhinuExecutionException(
                    $"Persisted inference evidence for '{nodePath}' does not match the admitted profile and prompt identity.");

            if (envelope.Failure is not null && envelope.Publications.Count != 0)
                throw new FuwenZhinuExecutionException($"Persisted failed result for '{nodePath}' contains publication evidence.");
            return envelope;
        }
        catch (FuwenZhinuExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' is malformed: {exception.Message}");
        }
    }

    private static IEnumerable<WorkflowNode> FlattenNodes(IEnumerable<WorkflowNode> nodes)
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
            foreach (var child in FlattenNodes(children))
                yield return child;
        }
    }
}

internal sealed class NodeExecutionEnvelope
{
    [JsonConstructor]
    public NodeExecutionEnvelope(
        ExecutionInvocation invocation,
        RuntimeValue? output,
        ExecutionFailure? failure,
        ContextSnapshotReference? contextSnapshot,
        IReadOnlyList<ArtifactPublicationReceipt>? publications,
        InferenceExecutionEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if ((output is null) == (failure is null))
            throw new ArgumentException("A persisted execution envelope must contain exactly one of output or failure.");
        if (failure is not null && contextSnapshot is not null)
            throw new ArgumentException("A failed execution envelope cannot contain context snapshot evidence.");

        Invocation = invocation;
        Output = output;
        Failure = failure;
        ContextSnapshot = contextSnapshot;
        Publications = publications ?? Array.Empty<ArtifactPublicationReceipt>();
        Evidence = evidence;
    }

    public ExecutionInvocation Invocation { get; }
    public RuntimeValue? Output { get; }
    public ExecutionFailure? Failure { get; }
    public ContextSnapshotReference? ContextSnapshot { get; }
    public IReadOnlyList<ArtifactPublicationReceipt> Publications { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InferenceExecutionEvidence? Evidence { get; }

    internal static NodeExecutionEnvelope Succeeded(
        ExecutionInvocation invocation,
        RuntimeValue output,
        ContextSnapshotReference? contextSnapshot,
        IReadOnlyList<ArtifactPublicationReceipt> publications,
        InferenceExecutionEvidence? evidence = null) =>
        new(invocation, output, null, contextSnapshot, publications, evidence);

    internal static NodeExecutionEnvelope Failed(
        ExecutionInvocation invocation,
        ExecutionFailure failure,
        InferenceExecutionEvidence? evidence = null) =>
        new(invocation, null, failure, null, Array.Empty<ArtifactPublicationReceipt>(), evidence);
}
