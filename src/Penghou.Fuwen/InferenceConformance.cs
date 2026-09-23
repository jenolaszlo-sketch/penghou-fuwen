using System.Text;

namespace Penghou.Fuwen;

/// <summary>Deterministic scripted turn-executor fixture for conformance and coordinator tests.</summary>
public sealed class DeterministicFakeTurnExecutor : IInferenceTurnExecutor, IInferenceTurnExecutorManifest
{
    private readonly Queue<InferenceTurnResult> script;
    private readonly Func<InferenceTurnRequest, CancellationToken, ValueTask<InferenceTurnResult>>? responder;

    /// <summary>Creates a fake that replays one scripted result per turn.</summary>
    public DeterministicFakeTurnExecutor(IEnumerable<InferenceTurnResult> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var copy = script.Select(static result =>
            result ?? throw new ArgumentException("Scripted results cannot contain null values.", nameof(script))).ToArray();
        if (copy.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(script), "A scripted fake requires at least one result.");
        this.script = new Queue<InferenceTurnResult>(copy);
    }

    /// <summary>Creates a fake that answers every turn through a deterministic responder.</summary>
    public DeterministicFakeTurnExecutor(Func<InferenceTurnRequest, InferenceTurnResult> responder)
        : this((request, _) => ValueTask.FromResult(
            responder(request ?? throw new ArgumentNullException(nameof(request)))))
    {
    }

    private DeterministicFakeTurnExecutor(Func<InferenceTurnRequest, CancellationToken, ValueTask<InferenceTurnResult>> responder)
    {
        this.responder = responder ?? throw new ArgumentNullException(nameof(responder));
        script = new Queue<InferenceTurnResult>();
    }

    /// <summary>Creates a fake that answers through an async deterministic responder.</summary>
    public static DeterministicFakeTurnExecutor FromResponder(
        Func<InferenceTurnRequest, CancellationToken, ValueTask<InferenceTurnResult>> responder) =>
        new(responder ?? throw new ArgumentNullException(nameof(responder)));

    /// <summary>A fake that returns one final candidate with exact usage.</summary>
    public static DeterministicFakeTurnExecutor FinalCandidate(string candidateJson, InferenceTurnUsage? usage = null) =>
        new([new InferenceFinalCandidateResult(candidateJson, usage)]);

    /// <summary>A fake that proposes exact tool calls with unknown usage.</summary>
    public static DeterministicFakeTurnExecutor ToolCalls(params InferenceToolCallProposal[] proposals) =>
        new([new InferenceToolCallTurnResult(proposals, usage: null)]);

    /// <summary>A fake that observes cancellation without producing a result.</summary>
    public static DeterministicFakeTurnExecutor Cancelling() =>
        FromResponder((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("A cancelling fake must observe cancellation before producing a result.");
        });

    /// <summary>The turn requests observed by this fake, in call order.</summary>
    public IReadOnlyList<InferenceTurnRequest> ObservedRequests => observed.AsReadOnly();
    private readonly List<InferenceTurnRequest> observed = [];

    /// <summary>
    /// An accept-all manifest mirror: the fake executes whatever the test
    /// scripts, so preflight succeeds exactly for the checked requirement.
    /// </summary>
    public InferenceFeatureManifest TurnFeatureManifest => new(
        [InferencePromptForm.RegisteredTemplate, InferencePromptForm.WorkflowOwned],
        [InferenceModality.StructuredText],
        supportsContextDelivery: true,
        maximumContextPayloadUtf8Bytes: int.MaxValue,
        [InferenceToolEffect.ReadOnly],
        Enum.GetValues<InferenceLimitDimension>()
            .Select(static dimension => new InferenceLimit(dimension, 1_000_000_000_000_000))
            .ToArray(),
        InferenceRecoveryQuality.Reconcilable,
        InferenceUsageQuality.Exact,
        InferencePricingQuality.Exact);

    /// <inheritdoc />
    public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var manifest = new InferenceFeatureManifest(
            [requirement.PromptForm],
            requirement.Modality is null ? [] : [requirement.Modality.Value],
            supportsContextDelivery: requirement.HasContextInputs,
            maximumContextPayloadUtf8Bytes: requirement.MaximumContextPayloadUtf8Bytes
                ?? (requirement.HasContextInputs ? int.MaxValue : null),
            requirement.ToolRequirements.Select(static tool => tool.Effect).Distinct().ToArray(),
            requirement.Limits.Limits,
            requirement.MinimumRecoveryQuality,
            requirement.RequiresExactUsageEvidence ? InferenceUsageQuality.Exact : InferenceUsageQuality.Unknown,
            requirement.RequiresExactPricingEvidence ? InferencePricingQuality.Exact : InferencePricingQuality.Unknown,
            supportsStructuredOutput: requirement.RequiresStructuredOutput,
            profiles: [requirement.Profile],
            promptTemplates: requirement.PromptTemplate is null ? [] : [requirement.PromptTemplate],
            tools: requirement.Tools,
            workflowPromptDigests: requirement.PromptDigest is null ? [] : [requirement.PromptDigest]);
        return InferencePreflight.Evaluate(requirement, manifest);
    }

    /// <inheritdoc />
    public ValueTask<InferenceTurnResult> ExecuteTurnAsync(InferenceTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        observed.Add(request);
        if (responder is not null)
            return responder(request, cancellationToken);
        if (script.Count == 0)
            throw new InvalidOperationException("The scripted fake has no remaining results.");
        return ValueTask.FromResult(script.Dequeue());
    }
}

/// <summary>Deterministic host read-tool fixture for conformance and coordinator tests.</summary>
public sealed class DeterministicFakeReadToolExecutor : IInferenceReadToolExecutor
{
    private readonly Dictionary<DescriptorReference, Func<InferenceReadToolRequest, InferenceReadToolResult>> handlers = new();
    private readonly List<InferenceReadToolRequest> observed = [];

    /// <summary>Registers a deterministic handler for one exact tool descriptor.</summary>
    public DeterministicFakeReadToolExecutor Register(
        DescriptorReference tool,
        Func<InferenceReadToolRequest, InferenceReadToolResult> handler)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(handler);
        if (tool.Kind != DescriptorKind.Tool)
            throw new ArgumentException("Fake read tools must reference tool descriptors.", nameof(tool));
        handlers.Add(tool, handler);
        return this;
    }

    /// <summary>Registers a fixed successful output for one exact tool descriptor.</summary>
    public DeterministicFakeReadToolExecutor RegisterSuccess(DescriptorReference tool, RuntimeValue output) =>
        Register(tool, request => InferenceReadToolResult.Succeeded(
            output,
            new InferenceReadToolEvidence(tool, request.OperationKey)));

    /// <summary>The tool requests observed by this fake, in call order.</summary>
    public IReadOnlyList<InferenceReadToolRequest> ObservedRequests => observed.AsReadOnly();

    /// <inheritdoc />
    public ValueTask<InferenceReadToolResult> ExecuteAsync(InferenceReadToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        observed.Add(request);
        if (!handlers.TryGetValue(request.Tool, out var handler))
            return ValueTask.FromResult(InferenceReadToolResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError,
                $"The fake host does not implement tool '{request.Tool.Name}@{request.Tool.Version}'.")));
        return ValueTask.FromResult(handler(request));
    }
}

/// <summary>Shared provider-neutral conformance suites for turn and read-tool adapters.</summary>
public static class InferenceTurnToolConformance
{
    /// <summary>
    /// Verifies a turn executor against the deterministic matrix: final success,
    /// exact tool proposals, malformed calls, duplicate identities, cancellation,
    /// and unknown usage. Throws on the first violation.
    /// </summary>
    public static async Task VerifyTurnExecutorAsync(IInferenceTurnExecutor executor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var tool = new DescriptorReference(
            DescriptorKind.Tool, "conformance.search", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));
        var visible = new[] { new InferenceToolRequirement(tool) };
        var conversation = new[] { new InferenceConversationMessage(InferenceTurnRole.User, "{\"question\":\"ok\"}") };

        // Final success round-trips candidate and exact usage.
        var finalExecutor = new DeterministicFakeTurnExecutor([
            new InferenceFinalCandidateResult(
                "{\"answer\":\"ok\"}",
                new InferenceTurnUsage(promptTokens: 10, completionTokens: 5, totalTokens: 15))]);
        var finalResult = await finalExecutor.ExecuteTurnAsync(
            new InferenceTurnRequest("conformance/1", 0, conversation, visible), cancellationToken).ConfigureAwait(false);
        if (finalResult is not InferenceFinalCandidateResult final || final.CandidateJson != "{\"answer\":\"ok\"}")
            throw new InvalidOperationException("Conformance requires a final candidate to round-trip its JSON.");
        if (final.Usage?.TotalTokens != 15)
            throw new InvalidOperationException("Conformance requires exact turn usage to round-trip.");

        // The executor under test must accept a well-formed request without throwing
        // a contract violation for the shared shape.
        var probe = await executor.ExecuteTurnAsync(
            new InferenceTurnRequest("conformance/1", 0, conversation, visible), cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(probe);

        // Shared rejection matrix: every row must fail closed before tool execution.
        // Oversized args must fit the proposal-level bound but exceed the
        // validation-time ceiling below so the ctor accepts them.
        var oversized = new string('x', 8_192 + 1);
        var cases = new (string Name, InferenceToolCallProposal Proposal)[]
        {
            ("undeclared", new InferenceToolCallProposal("call-1", new DescriptorReference(
                DescriptorKind.Tool, "conformance.unknown", "1",
                new ContentDigest("sha256", "descriptor/v1", new string('b', 64))), "{\"q\":1}")),
            ("duplicate", new InferenceToolCallProposal("call-1", tool, "{\"q\":1}")),
            ("oversized", new InferenceToolCallProposal("call-big", tool, "{\"q\":\"" + oversized + "\"}")),
            ("mistyped", new InferenceToolCallProposal("call-bad", tool, "not-json")),
        };
        foreach (var (name, proposal) in cases)
        {
            var proposals = name == "duplicate" ? new[] { proposal, proposal } : new[] { proposal };
            var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
            if (failure is null || failure.Code != ExecutionFailureCode.ToolMappingFailure)
                throw new InvalidOperationException($"Conformance case '{name}' must fail closed with ToolMappingFailure.");
        }

        // Write-capable tools are never executable through the read-only turn port.
        var writeRequirement = new InferenceToolRequirement(tool, InferenceToolEffect.IdempotentWrite);
        var writeFailure = InferenceTurnValidation.ValidateProposals(
            [new InferenceToolCallProposal("call-1", tool, "{\"q\":1}")],
            [writeRequirement],
            maximumArgumentBytes: 8_192);
        if (writeFailure is null || writeFailure.Code != ExecutionFailureCode.ToolMappingFailure)
            throw new InvalidOperationException("Conformance requires write-capable proposals to fail closed.");

        // Cancellation is observed, not converted into a result.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        try
        {
            await DeterministicFakeTurnExecutor.Cancelling().ExecuteTurnAsync(
                new InferenceTurnRequest("conformance/1", 0, conversation, visible), cancelled.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Conformance requires cancellation to be observed.");
        }
        catch (OperationCanceledException)
        {
        }

        // Unknown usage is carried as null, never zero.
        var unknown = await DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", tool, "{\"q\":1}")).ExecuteTurnAsync(
            new InferenceTurnRequest("conformance/1", 0, conversation, visible), cancellationToken).ConfigureAwait(false);
        if (unknown is not InferenceToolCallTurnResult unknownCalls || unknownCalls.Usage is not null)
            throw new InvalidOperationException("Conformance requires unknown turn usage to remain null.");
    }

    /// <summary>
    /// Verifies a read-tool executor: exact execution, undeclared rejection,
    /// typed output preservation, cancellation, and evidence identity. Throws on violation.
    /// </summary>
    public static async Task VerifyReadToolExecutorAsync(
        IInferenceReadToolExecutor executor,
        InferenceReadToolRequest sample,
        RuntimeValue expectedOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(expectedOutput);

        var result = await executor.ExecuteAsync(sample, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException("Conformance requires the sample tool request to succeed.");
        if (result.Output is null)
            throw new InvalidOperationException("Conformance requires a successful tool result to carry output.");
        if (result.Evidence is not null && !string.Equals(result.Evidence.OperationKey, sample.OperationKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Conformance requires tool evidence to preserve the operation key.");

        var undeclared = new InferenceReadToolRequest(
            new DescriptorReference(
                DescriptorKind.Tool, "conformance.undeclared", "1",
                new ContentDigest("sha256", "descriptor/v1", new string('d', 64))),
            sample.Arguments,
            sample.Scope,
            "conformance/op/undeclared");
        var rejected = await executor.ExecuteAsync(undeclared, cancellationToken).ConfigureAwait(false);
        if (rejected.IsSuccess)
            throw new InvalidOperationException("Conformance requires undeclared tools to fail without execution.");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        try
        {
            await executor.ExecuteAsync(sample, cancelled.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Conformance requires cancellation to be observed.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Builds the exact scope string for one admitted tool operation.</summary>
    public static string ScopeFor(DescriptorReference tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return $"{tool.Name}@{tool.Version}";
    }

    /// <summary>Builds a stable operation key for one internal tool operation.</summary>
    public static string OperationKeyFor(string interactionId, int toolOrdinal, string callId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(interactionId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(callId);
        if (toolOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(toolOrdinal));
        return $"{interactionId}/tool/{toolOrdinal:0000}/{callId}";
    }
}
