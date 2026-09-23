using System.Text;
using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

/// <summary>
/// Runs one bounded durable protocol loop under its enclosing scope. Top-level
/// regions use the workflow context; repeat bodies pass their iteration so the
/// loop identity nests correctly.
/// </summary>
internal delegate Task<JsonElement> ProtocolLoopRunner(
    string name,
    JsonElement initial,
    Func<WorkflowLoopIteration<JsonElement>, CancellationToken, Task<LoopBodyOutcome<JsonElement>>> body,
    LoopOptions options,
    CancellationToken cancellationToken);

/// <summary>
/// Owns the bounded durable model → tool → model protocol behind one logical
/// Fuwen <c>infer</c> node. Internal operations run as durable loop-iteration
/// steps with stable operation identity; the journal is the persisted loop
/// (steps plus state), never an in-memory detail. Crash recovery reuses
/// completed steps and reconciles ambiguous operations instead of silently
/// issuing new work under a new identity.
/// </summary>
internal static class FuwenInferenceCoordinator
{
    private const string RequestFingerprintContract = "fuwen-request/v1";
    private const int DefaultConversationCapUtf8Bytes = 262_144;
    private const int DefaultStepArgumentCapUtf8Bytes = 65_536;

    internal static async Task<RuntimeValue> ExecuteAsync(
        InferenceNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        ProtocolLoopRunner loopRunner,
        FuwenInterpreterState state,
        IReadOnlyList<InferenceContextInput> contextInputs,
        string runtimeScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contextInputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeScope);
        if (node.Protocol is null)
            throw new FuwenZhinuExecutionException(
                $"Inference node '{node.StructuralPath}' reached the protocol coordinator without aggregate limits.");
        if (ports.TurnExecutor is null)
            throw new FuwenZhinuExecutionException(
                $"Inference node '{node.StructuralPath}' requires a turn executor for coordinated inference.");

        var effective = EffectiveLimits(node.Protocol.Limits, ports.InferenceHostCeilings);
        var required = RequireFiniteBounds(node, effective);
        if (required is not null)
            throw new FuwenZhinuExecutionException(required);

        var prepared = Prepare(node, plan, state, contextInputs, effective, runtimeScope);
        if (prepared.Failure is not null)
            throw new FuwenZhinuExecutionException(prepared.Failure);

        var initial = InitialState(prepared, effective);
        var maxIterations = (int)Math.Min(int.MaxValue, Math.Min((long)int.MaxValue, effective.MaxTurns!.Value)
            + Math.Min((long)int.MaxValue, effective.MaxToolCalls ?? 0) + 2);
        JsonElement finalStateJson;
        try
        {
            finalStateJson = await loopRunner(
                ProtocolLoopName(node.StructuralPath),
                FuwenRuntimeValueWire.Serialize(initial),
                (iteration, token) => IterateAsync(
                    node, plan, ports, prepared, effective, iteration, token),
                new LoopOptions(maxIterations)
                {
                    // Zhinu measures the time budget once from the loop's first
                    // durable entry and persists the resolved boundary, so the
                    // bound survives worker restarts without a wall-clock seed.
                    TimeBudget = ToTimeSpan(effective.MaxDurationMilliseconds!.Value),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (LoopLimitExceededException)
        {
            throw new FuwenZhinuExecutionException(new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.TurnLimitExceeded,
                $"Inference node '{node.StructuralPath}' exceeded its coordinated iteration bound."));
        }
        catch (WorkflowTimeoutException)
        {
            throw new FuwenZhinuExecutionException(new ExecutionFailure(
                ExecutionFailureKind.Timeout,
                ExecutionFailureCode.Timeout,
                $"Inference node '{node.StructuralPath}' exceeded its {effective.MaxDurationMilliseconds}ms duration bound."));
        }

        var finalState = CanonicalJson.Deserialize<CoordinatorState>(
            CanonicalJson.Canonicalize(finalStateJson));
        if (finalState.Failure is not null)
        {
            await RecordEvidenceAsync(ports, BuildEvidence(effective, finalState), cancellationToken).ConfigureAwait(false);
            throw new FuwenZhinuExecutionException(RebuildFailure(finalState.Failure));
        }
        if (finalState.OutputJson is null)
        {
            finalState.Failure = ToRecord(new ExecutionFailure(
                ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError,
                $"Inference node '{node.StructuralPath}' completed coordination without a typed output."));
            await RecordEvidenceAsync(ports, BuildEvidence(effective, finalState), cancellationToken).ConfigureAwait(false);
            throw new FuwenZhinuExecutionException(RebuildFailure(finalState.Failure));
        }
        await RecordEvidenceAsync(ports, BuildEvidence(effective, finalState), cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(
            JsonDocument.Parse(finalState.OutputJson).RootElement, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"coordinated inference '{node.StructuralPath}' output");
        return output;
    }

    private static InferenceProtocolEvidence BuildEvidence(EffectiveBounds effective, CoordinatorState state)
    {
        var operations = state.Operations.Select(static operation => new InferenceOperationEvidence(
            operation.Ordinal,
            (InferenceOperationKind)operation.Kind,
            operation.OperationId,
            (InferenceOperationDisposition)operation.Disposition,
            operation.MayHaveCommitted,
            operation.PromptTokens,
            operation.CompletionTokens,
            operation.TotalTokens,
            operation.CostMicrounits is long amount && operation.CostCurrency is not null
                ? new InferenceCostEvidence(
                    operation.CostCurrency, amount, operation.CostEstimated, operation.CostRevision)
                : null,
            (InferenceUsageQuality)operation.UsageQuality,
            (InferencePricingQuality)operation.PricingQuality,
            operation.DurationMilliseconds,
            operation.FailureCode)).ToArray();
        var toolOutcomes = state.ToolOutcomes.Select(static outcome => new InferenceToolOutcomeEvidence(
            new DescriptorReference(
                DescriptorKind.Tool, outcome.ToolName, outcome.ToolVersion,
                new ContentDigest(outcome.DigestAlgorithm, outcome.DigestContract, outcome.DigestValue)),
            outcome.OperationKey,
            (InferenceOperationDisposition)outcome.Disposition,
            outcome.MayHaveCommitted,
            outcome.ResultDigestValue is null
                ? null
                : new ContentDigest(outcome.ResultDigestAlgorithm!, outcome.ResultDigestContract!, outcome.ResultDigestValue),
            outcome.ResultUtf8Bytes,
            (InferenceReadToolRetrySafety)outcome.RetrySafety,
            outcome.DurationMilliseconds,
            outcome.FailureCode)).ToArray();

        var tokensKnown = !state.PromptUnknown && !state.CompletionUnknown && !state.TotalUnknown;
        InferenceCostEvidence? cost = !state.CostUnknown && state.CostCurrency is not null
            ? new InferenceCostEvidence(state.CostCurrency, state.CostMicrounits, state.CostEstimated)
            : null;
        return new InferenceProtocolEvidence(
            state.InteractionId,
            ToLimitSet(effective),
            operations,
            toolOutcomes,
            validationAttempts: state.ValidationAttempts,
            recoveryDisposition: state.CommitmentUncertainty
                ? InferenceRecoveryDisposition.Ambiguous
                : InferenceRecoveryDisposition.Fresh,
            commitmentUncertainty: state.CommitmentUncertainty,
            promptTokens: tokensKnown ? checked((int)Math.Min(state.PromptTokens, int.MaxValue)) : null,
            completionTokens: tokensKnown ? checked((int)Math.Min(state.CompletionTokens, int.MaxValue)) : null,
            totalTokens: tokensKnown ? checked((int)Math.Min(state.TotalTokens, int.MaxValue)) : null,
            usageQuality: tokensKnown ? InferenceUsageQuality.Exact : InferenceUsageQuality.Unknown,
            cost: cost,
            pricingQuality: cost is null
                ? InferencePricingQuality.Unknown
                : (state.CostEstimated ? InferencePricingQuality.Estimated : InferencePricingQuality.Exact),
            failure: state.Failure is null ? null : RebuildFailure(state.Failure),
            operationsTruncated: state.OperationsTruncated,
            toolOutcomesTruncated: state.ToolOutcomesTruncated);
    }

    private static InferenceLimitSet ToLimitSet(EffectiveBounds effective)
    {
        var limits = new List<InferenceLimit>();
        Add(InferenceLimitDimension.Turns, effective.MaxTurns);
        Add(InferenceLimitDimension.ModelCalls, effective.MaxModelCalls);
        Add(InferenceLimitDimension.ToolCalls, effective.MaxToolCalls);
        Add(InferenceLimitDimension.PromptTokens, effective.MaxPromptTokens);
        Add(InferenceLimitDimension.CompletionTokens, effective.MaxCompletionTokens);
        Add(InferenceLimitDimension.TotalTokens, effective.MaxTotalTokens);
        Add(InferenceLimitDimension.DurationMilliseconds, effective.MaxDurationMilliseconds);
        Add(InferenceLimitDimension.ToolArgumentBytes, effective.MaxToolArgumentBytes);
        Add(InferenceLimitDimension.ToolResultBytes, effective.MaxToolResultBytes);
        Add(InferenceLimitDimension.RetainedConversationBytes, effective.MaxRetainedConversationBytes);
        Add(InferenceLimitDimension.RetainedEvidenceBytes, effective.MaxRetainedEvidenceBytes);
        if (effective.CostCeilingMicrounits is long ceiling)
            limits.Add(new InferenceLimit(InferenceLimitDimension.CostMicrounits, ceiling));
        return new InferenceLimitSet(limits);

        void Add(InferenceLimitDimension dimension, long? maximum)
        {
            if (maximum is long value)
                limits.Add(new InferenceLimit(dimension, value));
        }
    }

    private static async ValueTask RecordEvidenceAsync(
        FuwenZhinuExecutionPorts ports,
        InferenceProtocolEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (ports.EvidenceSink is null)
            return;
        try
        {
            await ports.EvidenceSink.RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The evidence sink is explicitly non-authoritative; a sink failure
            // must never alter execution truth or the terminal outcome.
        }
    }

    private sealed record PreparedConversation(
        List<MessageRecord> Conversation,
        string InteractionId,
        ExecutionFailure? Failure);

    private static PreparedConversation Prepare(
        InferenceNode node,
        WorkflowPlan plan,
        FuwenInterpreterState state,
        IReadOnlyList<InferenceContextInput> contextInputs,
        EffectiveBounds effective,
        string runtimeScope)
    {
        try
        {
            if (node.PromptName is null)
                return Fail("The coordinator requires a workflow-owned prompt reference.");
            var definition = plan.Prompts?.FirstOrDefault(
                prompt => string.Equals(prompt.Name, node.PromptName, StringComparison.Ordinal));
            if (definition is null)
                return Fail($"Inference node '{node.StructuralPath}' references unknown prompt '{node.PromptName}'.");
            if (definition.RegisteredSource is not null)
                return Fail($"Inference node '{node.StructuralPath}' uses a registered prompt alias; the coordinator requires a workflow-owned prompt.");
            var values = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            foreach (var binding in node.PromptBindings ?? [])
                values[binding.ParameterName] = FuwenBindingEvaluator.Evaluate(binding.Value, plan, state);
            var rendered = PromptRenderer.Render(definition, values);
            var conversation = rendered.Select(static message => new MessageRecord(
                message.Role == PromptMessageRole.System ? "system" : "user",
                message.Text,
                null)).ToList();
            if (conversation.Count == 0)
                return Fail($"Prompt '{definition.Name}' rendered no messages.");
            if (contextInputs.Count > 0)
            {
                using var payload = JsonDocument.Parse(BuildContextJson(contextInputs));
                var canonical = CanonicalJson.Canonicalize(payload.RootElement);
                conversation.Add(new MessageRecord(
                    "user", Encoding.UTF8.GetString(canonical), null));
            }
            var cap = effective.MaxRetainedConversationBytes is long bound && bound <= int.MaxValue
                ? (int)bound
                : DefaultConversationCapUtf8Bytes;
            long total = 0;
            foreach (var message in conversation)
                total += Encoding.UTF8.GetByteCount(message.Text);
            if (total > cap)
                return Fail($"Inference node '{node.StructuralPath}' initial conversation exceeds its retained conversation bound.");

            var identity = new
            {
                kind = "inference-protocol",
                node = node.StructuralPath,
                // The runtime scope (loop iteration, fan-out item) keeps
                // distinct invocations of the same structural node from
                // sharing one interaction identity and operation journal.
                scope = runtimeScope,
                profile = new { kind = node.Profile.Kind.ToString(), node.Profile.Name, node.Profile.Version },
                prompt = definition.GetSemanticDigest(),
                tools = (node.Tools ?? []).Select(static tool => tool.Name + "@" + tool.Version).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            };
            var interactionId = RequestFingerprint(FuwenRuntimeValueWire.Serialize(identity));
            return new PreparedConversation(conversation, interactionId, null);

            PreparedConversation Fail(string message) => new(
                [], string.Empty,
                new ExecutionFailure(
                    ExecutionFailureKind.Contract,
                    ExecutionFailureCode.InvalidInput,
                    message));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new PreparedConversation(
                [], string.Empty,
                new ExecutionFailure(
                    ExecutionFailureKind.Contract,
                    ExecutionFailureCode.InvalidInput,
                    $"Inference node '{node.StructuralPath}' could not render its workflow-owned prompt: {exception.GetType().Name}."));
        }
    }

    private sealed record EffectiveBounds(
        long? MaxTurns, long? MaxModelCalls, long? MaxToolCalls,
        long? MaxPromptTokens, long? MaxCompletionTokens, long? MaxTotalTokens,
        long? MaxDurationMilliseconds,
        long? MaxToolArgumentBytes, long? MaxToolResultBytes,
        long? MaxRetainedConversationBytes, long? MaxRetainedEvidenceBytes,
        InferenceCostLimit? Cost,
        long? CostCeilingMicrounits);

    private static EffectiveBounds EffectiveLimits(
        InferenceProtocolLimits source, InferenceLimitSet? host)
    {
        static long? Min(long? sourceValue, InferenceLimitDimension dimension, InferenceLimitSet? hostSet)
        {
            var hostValue = hostSet?.GetMaximum(dimension);
            return (sourceValue, hostValue) switch
            {
                (null, null) => null,
                (long s, null) => s,
                (null, long h) => h,
                (long s, long h) => Math.Min(s, h),
            };
        }
        var hostCost = host?.GetMaximum(InferenceLimitDimension.CostMicrounits);
        // The cost ceiling is enforced in microunits regardless of currency so
        // a host-only ceiling still bounds spend; the currency-bearing source
        // limit additionally gates currency-mismatched cost evidence as
        // unknown rather than silently converting it.
        long? costCeiling = (source.Cost?.MaximumMicrounits, hostCost) switch
        {
            (long s, long h) => Math.Min(s, h),
            (long s, null) => s,
            (null, long h) => h,
            _ => null,
        };
        return new EffectiveBounds(
            Min(source.MaxTurns, InferenceLimitDimension.Turns, host),
            Min(source.MaxModelCalls, InferenceLimitDimension.ModelCalls, host),
            Min(source.MaxToolCalls, InferenceLimitDimension.ToolCalls, host),
            Min(source.MaxPromptTokens, InferenceLimitDimension.PromptTokens, host),
            Min(source.MaxCompletionTokens, InferenceLimitDimension.CompletionTokens, host),
            Min(source.MaxTotalTokens, InferenceLimitDimension.TotalTokens, host),
            Min(source.MaxDurationMilliseconds, InferenceLimitDimension.DurationMilliseconds, host),
            Min(source.MaxToolArgumentBytes, InferenceLimitDimension.ToolArgumentBytes, host),
            Min(source.MaxToolResultBytes, InferenceLimitDimension.ToolResultBytes, host),
            Min(source.MaxRetainedConversationBytes, InferenceLimitDimension.RetainedConversationBytes, host),
            Min(source.MaxRetainedEvidenceBytes, InferenceLimitDimension.RetainedEvidenceBytes, host),
            source.Cost is not null && hostCost is long ceiling && source.Cost.MaximumMicrounits > ceiling
                ? new InferenceCostLimit(source.Cost.Currency, ceiling)
                : source.Cost,
            costCeiling);
    }

    private static ExecutionFailure? RequireFiniteBounds(InferenceNode node, EffectiveBounds effective)
    {
        if (effective.MaxTurns is null || effective.MaxModelCalls is null || effective.MaxDurationMilliseconds is null)
            return new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.InvalidInput,
                $"Inference node '{node.StructuralPath}' requires finite turns, model-calls, and duration bounds for coordinated inference.");
        if ((node.Tools is { Count: > 0 }) && effective.MaxToolCalls is null)
            return new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.InvalidInput,
                $"Inference node '{node.StructuralPath}' declares tools and requires a finite tool-call bound.");
        return null;
    }

    private sealed record MessageRecord(string Role, string Text, string? ToolCallId);
    private sealed record PendingRecord(int ToolIndex, string CallId, string ArgumentsJson);
    private sealed record FailureRecord(int Kind, int Code, string Message, bool MayHaveCommitted, string? ProviderCode);
    private sealed record OperationEvidenceRecord(
        int Ordinal, int Kind, string OperationId, int Disposition, bool MayHaveCommitted,
        int? PromptTokens, int? CompletionTokens, int? TotalTokens,
        long? CostMicrounits, string? CostCurrency, bool CostEstimated, string? CostRevision,
        int UsageQuality, int PricingQuality, long? DurationMilliseconds, string? FailureCode);
    private sealed record ToolEvidenceRecord(
        string ToolName, string ToolVersion, string DigestAlgorithm, string DigestContract, string DigestValue,
        string OperationKey, int Disposition, bool MayHaveCommitted,
        string? ResultDigestAlgorithm, string? ResultDigestContract, string? ResultDigestValue,
        int? ResultUtf8Bytes, int RetrySafety, long? DurationMilliseconds, string? FailureCode);

    private sealed record CoordinatorState
    {
        public string Phase { get; set; } = "model";
        public int TurnOrdinal { get; set; }
        public int ModelCalls { get; set; }
        public int ToolOrdinal { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long TotalTokens { get; set; }
        public bool PromptUnknown { get; set; }
        public bool CompletionUnknown { get; set; }
        public bool TotalUnknown { get; set; }
        public long CostMicrounits { get; set; }
        public bool CostUnknown { get; set; }
        public bool CostEstimated { get; set; }
        public string? CostCurrency { get; set; }
        public int OperationOrdinal { get; set; }
        public string InteractionId { get; set; } = string.Empty;
        public List<MessageRecord> Conversation { get; set; } = [];
        public List<PendingRecord> Pending { get; set; } = [];
        public int PendingIndex { get; set; }
        public int ValidationAttempts { get; set; }
        public bool CommitmentUncertainty { get; set; }
        public List<OperationEvidenceRecord> Operations { get; set; } = [];
        public bool OperationsTruncated { get; set; }
        public List<ToolEvidenceRecord> ToolOutcomes { get; set; } = [];
        public bool ToolOutcomesTruncated { get; set; }
        public string? CandidateJson { get; set; }
        public string? OutputJson { get; set; }
        public FailureRecord? Failure { get; set; }
    }

    private static CoordinatorState InitialState(PreparedConversation prepared, EffectiveBounds effective)
    {
        _ = effective;
        return new CoordinatorState
        {
            InteractionId = prepared.InteractionId,
            Conversation = prepared.Conversation,
        };
    }

    private static FailureRecord ToRecord(ExecutionFailure failure) => new(
        (int)failure.Kind, (int)failure.Code, failure.Message, failure.MayHaveCommittedEffect, failure.ProviderCode);

    private static ExecutionFailure RebuildFailure(FailureRecord record) => new(
        (ExecutionFailureKind)record.Kind,
        (ExecutionFailureCode)record.Code,
        record.Message,
        ExecutionRetryDisposition.Never,
        record.MayHaveCommitted,
        record.ProviderCode);

    private static void AddOperation(
        CoordinatorState state,
        int ordinal,
        InferenceOperationKind kind,
        string operationId,
        InferenceOperationDisposition disposition,
        UsageRecord? usage,
        long? durationMilliseconds,
        string? failureCode,
        bool mayHaveCommitted)
    {
        var usageComplete = usage is not null &&
            usage.Prompt is not null && usage.Completion is not null && usage.Total is not null;
        var usageQuality = usageComplete ? InferenceUsageQuality.Exact : InferenceUsageQuality.Unknown;
        var pricingQuality = usage?.CostMicrounits is null
            ? InferencePricingQuality.Unknown
            : (usage.CostEstimated ? InferencePricingQuality.Estimated : InferencePricingQuality.Exact);
        if (mayHaveCommitted)
            state.CommitmentUncertainty = true;
        if (state.Operations.Count >= InferenceProtocolEvidence.MaximumOperations)
        {
            state.OperationsTruncated = true;
            return;
        }
        state.Operations.Add(new OperationEvidenceRecord(
            ordinal, (int)kind, operationId, (int)disposition, mayHaveCommitted,
            usage?.Prompt, usage?.Completion, usage?.Total,
            usage?.CostMicrounits, usage?.CostCurrency, usage?.CostEstimated ?? false, usage?.CostRevision,
            (int)usageQuality, (int)pricingQuality, durationMilliseconds, failureCode));
    }

    private static void AddToolOutcome(
        CoordinatorState state,
        DescriptorReference tool,
        string operationKey,
        InferenceOperationDisposition disposition,
        bool mayHaveCommitted,
        string? resultJson,
        long? durationMilliseconds,
        string? failureCode)
    {
        if (mayHaveCommitted)
            state.CommitmentUncertainty = true;
        if (state.ToolOutcomes.Count >= InferenceProtocolEvidence.MaximumToolOutcomes)
        {
            state.ToolOutcomesTruncated = true;
            return;
        }
        ContentDigest? digest = null;
        int? byteLength = null;
        if (resultJson is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(resultJson);
            byteLength = bytes.Length;
            digest = new ContentDigest(
                "sha256", "inference-tool-result/v1",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        state.ToolOutcomes.Add(new ToolEvidenceRecord(
            tool.Name, tool.Version,
            tool.ContentDigest.Algorithm, tool.ContentDigest.Contract, tool.ContentDigest.Value,
            operationKey, (int)disposition, mayHaveCommitted,
            digest?.Algorithm, digest?.Contract, digest?.Value,
            byteLength, (int)InferenceReadToolRetrySafety.Safe, durationMilliseconds, failureCode));
    }

    private static async Task<LoopBodyOutcome<JsonElement>> IterateAsync(
        InferenceNode node,
        WorkflowPlan plan,
        FuwenZhinuExecutionPorts ports,
        PreparedConversation prepared,
        EffectiveBounds effective,
        WorkflowLoopIteration<JsonElement> iteration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = CanonicalJson.Deserialize<CoordinatorState>(
            CanonicalJson.Canonicalize(iteration.State));
        if (state.Failure is not null || state.OutputJson is not null)
            return iteration.Break(FuwenRuntimeValueWire.Serialize(state));

        var precheck = CheckBoundsBeforeOperation(node, effective, state);
        if (precheck is not null)
        {
            state.Failure = ToRecord(precheck);
            AddOperation(
                state,
                state.OperationOrdinal++,
                state.Phase == "tool" ? InferenceOperationKind.ToolCall : InferenceOperationKind.ModelTurn,
                state.Phase == "tool"
                    ? $"{state.InteractionId}/tool/{state.ToolOrdinal + 1:0000}/blocked"
                    : $"{state.InteractionId}/model/{state.TurnOrdinal + 1:0000}",
                InferenceOperationDisposition.Failed,
                usage: null,
                durationMilliseconds: null,
                failureCode: precheck.Code.ToString(),
                mayHaveCommitted: precheck.MayHaveCommittedEffect);
            return iteration.Break(FuwenRuntimeValueWire.Serialize(state));
        }

        if (state.Phase == "tool")
            await ExecuteToolOperationAsync(node, ports, effective, state, iteration, cancellationToken).ConfigureAwait(false);
        else
            await ExecuteModelOperationAsync(node, plan, ports, effective, state, iteration, cancellationToken).ConfigureAwait(false);

        if (state.Failure is not null || state.OutputJson is not null)
            return iteration.Break(FuwenRuntimeValueWire.Serialize(state));
        return iteration.Continue(FuwenRuntimeValueWire.Serialize(state));
    }

    private static ExecutionFailure? CheckBoundsBeforeOperation(
        InferenceNode node, EffectiveBounds effective, CoordinatorState state)
    {
        if (state.Phase == "tool")
        {
            if (state.ToolOrdinal >= effective.MaxToolCalls!.Value)
                return Limit(node, ExecutionFailureCode.ToolCallLimitExceeded,
                    $"Inference node '{node.StructuralPath}' exhausted its {effective.MaxToolCalls} tool-call bound.");
            // A tool result is only useful if another model turn may consume it.
            // Stop before the tool when no turn or model call remains so a
            // doomed read is never issued.
            if (state.TurnOrdinal >= effective.MaxTurns!.Value)
                return Limit(node, ExecutionFailureCode.TurnLimitExceeded,
                    $"Inference node '{node.StructuralPath}' exhausted its {effective.MaxTurns} turn bound before the proposed tool call.");
            if (state.ModelCalls >= effective.MaxModelCalls!.Value)
                return Limit(node, ExecutionFailureCode.ModelCallLimitExceeded,
                    $"Inference node '{node.StructuralPath}' exhausted its {effective.MaxModelCalls} model-call bound before the proposed tool call.");
        }
        else
        {
            if (state.TurnOrdinal >= effective.MaxTurns!.Value)
                return Limit(node, ExecutionFailureCode.TurnLimitExceeded,
                    $"Inference node '{node.StructuralPath}' exhausted its {effective.MaxTurns} turn bound.");
            if (state.ModelCalls >= effective.MaxModelCalls!.Value)
                return Limit(node, ExecutionFailureCode.ModelCallLimitExceeded,
                    $"Inference node '{node.StructuralPath}' exhausted its {effective.MaxModelCalls} model-call bound.");
        }
        if (!FitsTokens(effective, state, out var tokenCode))
            return Limit(node, tokenCode,
                $"Inference node '{node.StructuralPath}' cannot prove the next paid operation fits its token budget.");
        if (!FitsCost(effective, state))
            return Limit(node, ExecutionFailureCode.CostLimitExceeded,
                $"Inference node '{node.StructuralPath}' cannot prove the next paid operation fits its cost budget.");
        return null;

        static ExecutionFailure Limit(InferenceNode node, ExecutionFailureCode code, string message) => new(
            ExecutionFailureKind.Contract, code, message);
    }

    private static bool FitsTokens(EffectiveBounds effective, CoordinatorState state, out ExecutionFailureCode code)
    {
        code = ExecutionFailureCode.TokenLimitExceeded;
        if (effective.MaxPromptTokens is long prompt && (state.PromptUnknown || state.PromptTokens >= prompt))
        {
            if (state.PromptUnknown)
                code = ExecutionFailureCode.BudgetUnknown;
            return false;
        }
        if (effective.MaxCompletionTokens is long completion && (state.CompletionUnknown || state.CompletionTokens >= completion))
        {
            if (state.CompletionUnknown)
                code = ExecutionFailureCode.BudgetUnknown;
            return false;
        }
        if (effective.MaxTotalTokens is long total && (state.TotalUnknown || state.TotalTokens >= total))
        {
            if (state.TotalUnknown)
                code = ExecutionFailureCode.BudgetUnknown;
            return false;
        }
        return true;
    }

    private static bool FitsCost(EffectiveBounds effective, CoordinatorState state)
    {
        if (effective.CostCeilingMicrounits is null)
            return true;
        if (state.CostUnknown)
            return false;
        return state.CostMicrounits < effective.CostCeilingMicrounits.Value;
    }

    private static async Task ExecuteModelOperationAsync(
        InferenceNode node,
        WorkflowPlan plan,
        FuwenZhinuExecutionPorts ports,
        EffectiveBounds effective,
        CoordinatorState state,
        WorkflowLoopIteration<JsonElement> iteration,
        CancellationToken cancellationToken)
    {
        var ordinal = state.TurnOrdinal;
        var operationId = $"{state.InteractionId}/model/{ordinal + 1:0000}";
        var visibleTools = (node.Tools ?? []).Select(static tool => new InferenceToolRequirement(tool)).ToArray();
        var turnInput = FuwenRuntimeValueWire.Serialize(new
        {
            kind = "inference-turn",
            operation = operationId,
            ordinal,
            interaction = state.InteractionId,
        });
        // When no tool budget remains the model turn is still allowed to
        // finalize, so omit the advisory cap rather than passing an invalid
        // zero. Any proposal it makes still fails closed at the tool pre-check.
        int? maxNewToolCalls = effective.MaxToolCalls is long toolBound && toolBound - state.ToolOrdinal > 0
            ? (int)Math.Min((long)InferenceTurnRequest.MaximumProposals, toolBound - state.ToolOrdinal)
            : null;
        var output = await iteration.StepAsync(
            $"infer-model-turn-{ordinal:0000}",
            turnInput,
            async (_, _, token) =>
            {
                var conversation = state.Conversation.Select(static message => new InferenceConversationMessage(
                    message.Role switch
                    {
                        "system" => InferenceTurnRole.System,
                        "assistant" => InferenceTurnRole.Assistant,
                        "tool" => InferenceTurnRole.Tool,
                        _ => InferenceTurnRole.User,
                    },
                    message.Text,
                    message.ToolCallId)).ToArray();
                var request = new InferenceTurnRequest(
                    state.InteractionId,
                    ordinal,
                    conversation,
                    visibleTools,
                    RemainingLimits(effective, state),
                    maxNewToolCalls,
                    maxCompletionTokens: node.Limits?.MaxTokens,
                    timeoutSeconds: node.Limits?.TimeoutSeconds);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                InferenceTurnResult turnResult;
                try
                {
                    turnResult = await ports.TurnExecutor!.ExecuteTurnAsync(request, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Penghou.Zhinu.ZhinuException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    return TurnRecord(new FailureRecord(
                        (int)ExecutionFailureKind.Infrastructure,
                        (int)ExecutionFailureCode.AmbiguousOperation,
                        $"Model turn {operationId} transport failed without a result and may have committed: {exception.GetType().Name}.",
                        true, exception.GetType().Name), stopwatch.ElapsedMilliseconds);
                }
                stopwatch.Stop();
                if (node.Limits?.TimeoutSeconds is int timeout &&
                    stopwatch.Elapsed > TimeSpan.FromSeconds(timeout))
                {
                    return TurnRecord(new FailureRecord(
                        (int)ExecutionFailureKind.Timeout,
                        (int)ExecutionFailureCode.Timeout,
                        $"Model turn {operationId} exceeded its {timeout}s per-call timeout.",
                        true, nameof(TimeoutException)), stopwatch.ElapsedMilliseconds);
                }
                return TurnRecord(turnResult, stopwatch.ElapsedMilliseconds);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        ApplyTurnOutput(node, plan, effective, state, output, ordinal, operationId);
    }

    private static JsonElement TurnRecord(InferenceTurnResult result, long elapsedMilliseconds)
    {
        if (result is InferenceFinalCandidateResult final)
            return FuwenRuntimeValueWire.Serialize(new TurnOutputRecord(
                "final", final.CandidateJson, [], ToUsageRecord(result.Usage), null, elapsedMilliseconds));
        var calls = (InferenceToolCallTurnResult)result;
        return FuwenRuntimeValueWire.Serialize(new TurnOutputRecord(
            "tools",
            null,
            calls.Proposals.Select(static proposal => new ProposalRecord(
                proposal.CallId, proposal.Tool.Name, proposal.Tool.Version,
                proposal.Tool.ContentDigest.Algorithm, proposal.Tool.ContentDigest.Contract, proposal.Tool.ContentDigest.Value,
                proposal.ArgumentsJson)).ToList(),
            ToUsageRecord(result.Usage), null, elapsedMilliseconds));
    }

    private static JsonElement TurnRecord(FailureRecord failure, long elapsedMilliseconds) =>
        FuwenRuntimeValueWire.Serialize(new TurnOutputRecord("transport-error", null, [], null, failure, elapsedMilliseconds));

    private sealed record UsageRecord(int? Prompt, int? Completion, int? Total, long? CostMicrounits, string? CostCurrency, string? CostRevision, bool CostEstimated);
    private sealed record ProposalRecord(string CallId, string ToolName, string ToolVersion, string DigestAlgorithm, string DigestContract, string DigestValue, string ArgumentsJson);
    private sealed record TurnOutputRecord(string Status, string? Candidate, List<ProposalRecord> Proposals, UsageRecord? Usage, FailureRecord? Failure, long? ElapsedMs = null);

    private static UsageRecord? ToUsageRecord(InferenceTurnUsage? usage) => usage is null
        ? null
        : new UsageRecord(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
            usage.Cost?.AmountMicrounits, usage.Cost?.CurrencyCode, usage.Cost?.PricingRevision, usage.Cost?.IsEstimated ?? false);

    private static void ApplyTurnOutput(
        InferenceNode node, WorkflowPlan plan, EffectiveBounds effective, CoordinatorState state,
        JsonElement output, int ordinal, string operationId)
    {
        var record = CanonicalJson.Deserialize<TurnOutputRecord>(CanonicalJson.Canonicalize(output));
        if (record.Status == "transport-error")
        {
            state.Failure = record.Failure!;
            AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ModelTurn, operationId,
                record.Failure!.MayHaveCommitted
                    ? InferenceOperationDisposition.Ambiguous
                    : InferenceOperationDisposition.Failed,
                usage: null, durationMilliseconds: record.ElapsedMs,
                failureCode: ((ExecutionFailureCode)record.Failure.Code).ToString(),
                mayHaveCommitted: record.Failure.MayHaveCommitted);
            return;
        }
        AccumulateUsage(effective, state, record.Usage);
        state.TurnOrdinal = ordinal + 1;
        state.ModelCalls += 1;
        state.CostEstimated |= record.Usage?.CostEstimated ?? false;
        AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ModelTurn, operationId,
            InferenceOperationDisposition.Succeeded, record.Usage, durationMilliseconds: record.ElapsedMs,
            failureCode: null, mayHaveCommitted: false);
        if (node.Limits?.MaxTokens is int maxTokens)
        {
            if (record.Usage?.Completion is not int completion)
            {
                var unknown = new ExecutionFailure(
                    ExecutionFailureKind.Contract,
                    ExecutionFailureCode.BudgetUnknown,
                    $"Model turn {operationId} reported unknown completion usage against a {maxTokens}-token per-call bound.");
                state.Failure = ToRecord(unknown);
                AddOperation(state, state.OperationOrdinal, InferenceOperationKind.Validation,
                    $"{state.InteractionId}/validation/{state.OperationOrdinal:0000}",
                    InferenceOperationDisposition.Failed, usage: null, durationMilliseconds: null,
                    failureCode: unknown.Code.ToString(), mayHaveCommitted: false);
                state.OperationOrdinal++;
                return;
            }
            if (completion > maxTokens)
            {
                var exceeded = new ExecutionFailure(
                    ExecutionFailureKind.Contract,
                    ExecutionFailureCode.PerCallLimitExceeded,
                    $"Model turn {operationId} used {completion} completion tokens against a {maxTokens}-token per-call bound.");
                state.Failure = ToRecord(exceeded);
                AddOperation(state, state.OperationOrdinal, InferenceOperationKind.Validation,
                    $"{state.InteractionId}/validation/{state.OperationOrdinal:0000}",
                    InferenceOperationDisposition.Failed, usage: null, durationMilliseconds: null,
                    failureCode: exceeded.Code.ToString(), mayHaveCommitted: false);
                state.OperationOrdinal++;
                return;
            }
        }
        if (record.Status == "final")
        {
            ValidateCandidate(node, plan, effective, state, record.Candidate!);
            return;
        }
        var maxArgBytes = effective.MaxToolArgumentBytes is long bound
            ? (int)Math.Min(bound, int.MaxValue)
            : DefaultStepArgumentCapUtf8Bytes;
        var proposals = record.Proposals.Select(proposal => new InferenceToolCallProposal(
            proposal.CallId,
            new DescriptorReference(
                DescriptorKind.Tool, proposal.ToolName, proposal.ToolVersion,
                new ContentDigest(proposal.DigestAlgorithm, proposal.DigestContract, proposal.DigestValue)),
            proposal.ArgumentsJson)).ToArray();
        var visibleTools = (node.Tools ?? []).Select(static tool => new InferenceToolRequirement(tool)).ToArray();
        var failure = InferenceTurnValidation.ValidateProposals(proposals, visibleTools, maxArgBytes);
        if (failure is not null)
        {
            state.Failure = ToRecord(failure);
            AddOperation(state, state.OperationOrdinal, InferenceOperationKind.Validation,
                $"{state.InteractionId}/validation/{state.OperationOrdinal:0000}",
                InferenceOperationDisposition.Failed, usage: null, durationMilliseconds: null,
                failureCode: failure.Code.ToString(), mayHaveCommitted: false);
            state.OperationOrdinal++;
            return;
        }
        state.Pending = record.Proposals.Select((proposal, index) =>
        {
            // Match the exact admitted descriptor, not merely the tool name, so
            // duplicate names across versions cannot select the wrong binding.
            var toolIndex = node.Tools!.Select((tool, i) => (tool, i)).First(
                candidate =>
                    string.Equals(candidate.tool.Name, proposal.ToolName, StringComparison.Ordinal) &&
                    string.Equals(candidate.tool.Version, proposal.ToolVersion, StringComparison.Ordinal) &&
                    string.Equals(candidate.tool.ContentDigest.Algorithm, proposal.DigestAlgorithm, StringComparison.Ordinal) &&
                    string.Equals(candidate.tool.ContentDigest.Contract, proposal.DigestContract, StringComparison.Ordinal) &&
                    string.Equals(candidate.tool.ContentDigest.Value, proposal.DigestValue, StringComparison.Ordinal)).i;
            return new PendingRecord(toolIndex, proposal.CallId, proposal.ArgumentsJson);
        }).ToList();
        state.PendingIndex = 0;
        state.Phase = "tool";
        var summary = new StringBuilder("{\"toolCalls\":[");
        for (var i = 0; i < record.Proposals.Count; i++)
        {
            if (i > 0)
                summary.Append(',');
            summary.Append("{\"callId\":")
                .Append(JsonSerializer.Serialize(record.Proposals[i].CallId))
                .Append(",\"tool\":")
                .Append(JsonSerializer.Serialize(record.Proposals[i].ToolName + "@" + record.Proposals[i].ToolVersion))
                .Append(",\"arguments\":")
                .Append(record.Proposals[i].ArgumentsJson)
                .Append('}');
        }
        summary.Append("]}");
        if (!TryAppendMessage(effective, state, new MessageRecord("assistant", summary.ToString(), null)))
        {
            state.Failure = ToRecord(new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.PayloadLimitExceeded,
                $"Inference node '{node.StructuralPath}' exceeded its retained conversation bound."));
        }
    }

    private static void ValidateCandidate(
        InferenceNode node, WorkflowPlan plan, EffectiveBounds effective, CoordinatorState state, string candidate)
    {
        _ = effective;
        state.ValidationAttempts += 1;
        // The identity uses the global operation sequence so validation
        // records can never collide with turn- or tool-ordinal identities.
        var operationId = $"{state.InteractionId}/validation/{state.OperationOrdinal:0000}";

        void Fail(ExecutionFailureCode code, string message)
        {
            state.Failure = ToRecord(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, code, message));
            AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.Validation, operationId,
                InferenceOperationDisposition.Failed, usage: null, durationMilliseconds: null,
                failureCode: code.ToString(), mayHaveCommitted: false);
        }

        JsonElement element;
        try
        {
            element = JsonDocument.Parse(candidate).RootElement.Clone();
        }
        catch (JsonException)
        {
            Fail(ExecutionFailureCode.MalformedOutput,
                $"Inference node '{node.StructuralPath}' produced a final candidate that is not valid JSON.");
            return;
        }
        RuntimeValue value;
        try
        {
            value = FuwenRuntimeValueWire.FromJson(element, node.OutputType, plan.Schemas);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Fail(ExecutionFailureCode.SchemaMismatch,
                $"Inference node '{node.StructuralPath}' produced a final candidate that fails its declared schema.");
            return;
        }
        var validation = RuntimeValueValidator.Validate(value, node.OutputType, plan.Schemas, $"coordinated inference '{node.StructuralPath}' output");
        if (!validation.Succeeded)
        {
            Fail(ExecutionFailureCode.SchemaMismatch,
                $"Inference node '{node.StructuralPath}' produced a final candidate that fails its declared schema.");
            return;
        }
        AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.Validation, operationId,
            InferenceOperationDisposition.Succeeded, usage: null, durationMilliseconds: null,
            failureCode: null, mayHaveCommitted: false);
        state.CandidateJson = candidate;
        state.OutputJson = candidate;
        state.Phase = "done";
    }

    private static async Task ExecuteToolOperationAsync(
        InferenceNode node,
        FuwenZhinuExecutionPorts ports,
        EffectiveBounds effective,
        CoordinatorState state,
        WorkflowLoopIteration<JsonElement> iteration,
        CancellationToken cancellationToken)
    {
        var pending = state.Pending[state.PendingIndex];
        var tool = node.Tools![pending.ToolIndex];
        var operationKey = InferenceTurnToolConformance.OperationKeyFor(
            state.InteractionId, state.ToolOrdinal, pending.CallId);
        var operationId = $"{state.InteractionId}/tool/{state.ToolOrdinal + 1:0000}/{pending.CallId}";
        var toolInput = FuwenRuntimeValueWire.Serialize(new
        {
            kind = "inference-tool",
            operation = operationId,
            key = operationKey,
            tool = tool.Name + "@" + tool.Version,
            call = pending.CallId,
        });
        var output = await iteration.StepAsync(
            $"infer-read-tool-{state.ToolOrdinal:0000}-{SanitizeStepSegment(pending.CallId)}",
            toolInput,
            async (_, _, token) =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                JsonDocument? argumentsDocument;
                try
                {
                    argumentsDocument = JsonDocument.Parse(pending.ArgumentsJson);
                }
                catch (JsonException)
                {
                    return ToolRecord(new FailureRecord(
                        (int)ExecutionFailureKind.ProviderOutput,
                        (int)ExecutionFailureCode.ToolMappingFailure,
                        $"Tool call '{pending.CallId}' arguments are not valid JSON.",
                        false, null), stopwatch.ElapsedMilliseconds);
                }
                using (argumentsDocument)
                {
                    if (ports.ReadToolExecutor is null)
                        return ToolRecord(new FailureRecord(
                            (int)ExecutionFailureKind.Contract,
                            (int)ExecutionFailureCode.InvalidInput,
                            $"Inference node '{node.StructuralPath}' proposed a tool call without a configured read-tool executor.",
                            false, null), stopwatch.ElapsedMilliseconds);
                    var request = new InferenceReadToolRequest(
                        tool,
                        RuntimeValue.FromJson(argumentsDocument.RootElement.Clone()),
                        InferenceTurnToolConformance.ScopeFor(tool),
                        operationKey,
                        maximumResultUtf8Bytes: effective.MaxToolResultBytes is long bound && bound <= int.MaxValue
                            ? (int)bound
                            : null);
                    InferenceReadToolResult toolResult;
                    try
                    {
                        toolResult = await ports.ReadToolExecutor.ExecuteAsync(request, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Penghou.Zhinu.ZhinuException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        return ToolRecord(new FailureRecord(
                            (int)ExecutionFailureKind.Infrastructure,
                            (int)ExecutionFailureCode.AmbiguousOperation,
                            $"Tool call '{pending.CallId}' transport failed without a result and may have committed: {exception.GetType().Name}.",
                            true, exception.GetType().Name), stopwatch.ElapsedMilliseconds);
                    }
                    stopwatch.Stop();
                    if (!toolResult.IsSuccess)
                    {
                        var failure = toolResult.Failure!;
                        if (failure.MayHaveCommittedEffect)
                            return ToolRecord(new FailureRecord(
                                (int)ExecutionFailureKind.Infrastructure,
                                (int)ExecutionFailureCode.AmbiguousOperation,
                                $"Tool call '{pending.CallId}' is ambiguous and may have committed: {failure.Message}.",
                                true, failure.ProviderCode), stopwatch.ElapsedMilliseconds);
                        return ToolRecord(new FailureRecord(
                            (int)failure.Kind, (int)failure.Code,
                            $"Tool call '{pending.CallId}' failed: {failure.Message}.",
                            false, failure.ProviderCode), stopwatch.ElapsedMilliseconds);
                    }
                    var outputText = Encoding.UTF8.GetString(CanonicalJson.Serialize(
                        FuwenRuntimeValueWire.ToJson(toolResult.Output!))).TrimEnd('\r', '\n');
                    // Reject an oversized result before it is persisted in the
                    // step so the byte ceiling truly bounds retained payloads.
                    if (effective.MaxToolResultBytes is long resultBound &&
                        Encoding.UTF8.GetByteCount(outputText) > resultBound)
                    {
                        return ToolRecord(new FailureRecord(
                            (int)ExecutionFailureKind.Contract,
                            (int)ExecutionFailureCode.PayloadLimitExceeded,
                            $"Tool call '{pending.CallId}' exceeded the {resultBound}-byte result ceiling.",
                            false, null), stopwatch.ElapsedMilliseconds);
                    }
                    return ToolRecord(
                        null,
                        outputText,
                        toolResult.Evidence?.ResultUtf8Bytes,
                        toolResult.Evidence?.DurationMilliseconds ?? stopwatch.ElapsedMilliseconds,
                        stopwatch.ElapsedMilliseconds);
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        ApplyToolOutput(node, effective, state, output, pending, tool, operationKey, operationId);
    }

    private sealed record ToolOutputRecord(string Status, string? Output, int? ResultBytes, long? DurationMs, FailureRecord? Failure, long? ElapsedMs = null);

    private static JsonElement ToolRecord(FailureRecord failure, long elapsedMilliseconds) =>
        FuwenRuntimeValueWire.Serialize(new ToolOutputRecord("failed", null, null, null, failure, elapsedMilliseconds));

    private static JsonElement ToolRecord(string? output, string? outputText, int? resultBytes, long? durationMs, long elapsedMilliseconds) =>
        FuwenRuntimeValueWire.Serialize(new ToolOutputRecord("ok", outputText ?? output, resultBytes, durationMs, null, elapsedMilliseconds));

    private static void ApplyToolOutput(
        InferenceNode node, EffectiveBounds effective, CoordinatorState state, JsonElement output,
        PendingRecord pending, DescriptorReference tool, string operationKey, string operationId)
    {
        var record = CanonicalJson.Deserialize<ToolOutputRecord>(CanonicalJson.Canonicalize(output));
        if (record.Status != "ok" || record.Output is null)
        {
            state.Failure = record.Failure!;
            var ambiguous = record.Failure!.MayHaveCommitted;
            AddToolOutcome(state, tool, operationKey,
                ambiguous ? InferenceOperationDisposition.Ambiguous : InferenceOperationDisposition.Failed,
                ambiguous, resultJson: null, durationMilliseconds: record.ElapsedMs,
                failureCode: ((ExecutionFailureCode)record.Failure.Code).ToString());
            AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ToolCall, operationId,
                ambiguous ? InferenceOperationDisposition.Ambiguous : InferenceOperationDisposition.Failed,
                usage: null, durationMilliseconds: record.ElapsedMs,
                failureCode: ((ExecutionFailureCode)record.Failure.Code).ToString(), mayHaveCommitted: ambiguous);
            return;
        }
        if (effective.MaxToolResultBytes is long resultBound &&
            Encoding.UTF8.GetByteCount(record.Output) > resultBound)
        {
            state.Failure = ToRecord(new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.PayloadLimitExceeded,
                $"Tool call '{pending.CallId}' exceeded the {resultBound}-byte result ceiling."));
            AddToolOutcome(state, tool, operationKey, InferenceOperationDisposition.Failed, false,
                record.Output, record.DurationMs ?? record.ElapsedMs, nameof(ExecutionFailureCode.PayloadLimitExceeded));
            AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ToolCall, operationId,
                InferenceOperationDisposition.Failed, usage: null, record.DurationMs ?? record.ElapsedMs,
                nameof(ExecutionFailureCode.PayloadLimitExceeded), mayHaveCommitted: false);
            return;
        }
        if (!TryAppendMessage(effective, state, new MessageRecord("tool", record.Output, pending.CallId)))
        {
            state.Failure = ToRecord(new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.PayloadLimitExceeded,
                $"Inference node '{node.StructuralPath}' exceeded its retained conversation bound."));
            AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ToolCall, operationId,
                InferenceOperationDisposition.Failed, usage: null, record.DurationMs ?? record.ElapsedMs,
                nameof(ExecutionFailureCode.PayloadLimitExceeded), mayHaveCommitted: false);
            return;
        }
        AddToolOutcome(state, tool, operationKey, InferenceOperationDisposition.Succeeded, false,
            record.Output, record.DurationMs ?? record.ElapsedMs, failureCode: null);
        AddOperation(state, state.OperationOrdinal++, InferenceOperationKind.ToolCall, operationId,
            InferenceOperationDisposition.Succeeded, usage: null, record.DurationMs ?? record.ElapsedMs,
            failureCode: null, mayHaveCommitted: false);
        state.ToolOrdinal += 1;
        state.PendingIndex += 1;
        if (state.PendingIndex >= state.Pending.Count)
        {
            state.Pending = [];
            state.PendingIndex = 0;
            state.Phase = "model";
        }
    }

    private static bool TryAppendMessage(EffectiveBounds effective, CoordinatorState state, MessageRecord message)
    {
        var cap = effective.MaxRetainedConversationBytes is long bound && bound <= int.MaxValue
            ? (int)bound
            : DefaultConversationCapUtf8Bytes;
        long total = 0;
        foreach (var existing in state.Conversation)
            total += Encoding.UTF8.GetByteCount(existing.Text);
        total += Encoding.UTF8.GetByteCount(message.Text);
        if (total > cap)
            return false;
        state.Conversation.Add(message);
        return true;
    }

    private static void AccumulateUsage(EffectiveBounds effective, CoordinatorState state, UsageRecord? usage)
    {
        _ = effective;
        if (usage is null)
        {
            state.PromptUnknown = true;
            state.CompletionUnknown = true;
            state.TotalUnknown = true;
            state.CostUnknown = true;
            return;
        }
        if (usage.Prompt is null)
            state.PromptUnknown = true;
        else
            state.PromptTokens += usage.Prompt.Value;
        if (usage.Completion is null)
            state.CompletionUnknown = true;
        else
            state.CompletionTokens += usage.Completion.Value;
        if (usage.Total is null)
            state.TotalUnknown = true;
        else
            state.TotalTokens += usage.Total.Value;
        if (usage.CostMicrounits is null || usage.CostCurrency is null)
        {
            state.CostUnknown = true;
        }
        else if (state.CostCurrency is not null &&
            !string.Equals(state.CostCurrency, usage.CostCurrency, StringComparison.Ordinal))
        {
            // Mixed cost currencies cannot be summed honestly; fail closed on
            // the next pre-check rather than silently converting.
            state.CostUnknown = true;
        }
        else
        {
            state.CostCurrency = usage.CostCurrency;
            state.CostMicrounits += usage.CostMicrounits.Value;
        }
    }

    private static InferenceLimitSet RemainingLimits(EffectiveBounds effective, CoordinatorState state)
    {
        var remaining = new List<InferenceLimit>();
        static void Add(List<InferenceLimit> target, InferenceLimitDimension dimension, long? bound, long settled)
        {
            if (bound is long value && value > settled)
                target.Add(new InferenceLimit(dimension, Math.Min(value - settled, 1_000_000_000_000_000)));
        }
        Add(remaining, InferenceLimitDimension.Turns, effective.MaxTurns, state.TurnOrdinal);
        Add(remaining, InferenceLimitDimension.ModelCalls, effective.MaxModelCalls, state.ModelCalls);
        Add(remaining, InferenceLimitDimension.ToolCalls, effective.MaxToolCalls, state.ToolOrdinal);
        Add(remaining, InferenceLimitDimension.PromptTokens, effective.MaxPromptTokens, state.PromptTokens);
        Add(remaining, InferenceLimitDimension.CompletionTokens, effective.MaxCompletionTokens, state.CompletionTokens);
        Add(remaining, InferenceLimitDimension.TotalTokens, effective.MaxTotalTokens, state.TotalTokens);
        return new InferenceLimitSet(remaining);
    }

    private static string SanitizeStepSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '-');
        var sanitized = builder.ToString().Trim('-');
        return sanitized.Length == 0 ? "call" : (sanitized.Length > 64 ? sanitized[..64] : sanitized);
    }

    private static string RequestFingerprint(JsonElement requestJson) =>
        $"sha256:{RequestFingerprintContract}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Canonicalize(requestJson))).ToLowerInvariant()}";

    /// <summary>
    /// Derives a stable durable loop name from the node's structural path.
    /// The name is restricted to the characters Zhinu permits while remaining
    /// deterministic and collision-resistant across sibling nodes.
    /// </summary>
    private static TimeSpan ToTimeSpan(long milliseconds) =>
        milliseconds >= (long)TimeSpan.MaxValue.TotalMilliseconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(milliseconds);

    private static string ProtocolLoopName(string structuralPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(structuralPath))).ToLowerInvariant()[..12];
        return $"infer-protocol-{SanitizeStepSegment(structuralPath)}-{hash}";
    }

    private static string BuildContextJson(IReadOnlyList<InferenceContextInput> contextInputs)
    {
        var builder = new StringBuilder("{");
        var ordered = contextInputs.OrderBy(static input => input.Name, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(JsonSerializer.Serialize(ordered[i].Name));
            builder.Append(':');
            builder.Append(Encoding.UTF8.GetString(CanonicalJson.Serialize(
                FuwenRuntimeValueWire.ToJson(ordered[i].Value))));
        }
        builder.Append('}');
        return builder.ToString();
    }

    private static void EnsureType(RuntimeValue value, FuwenType type, IReadOnlyList<ResolvedSchemaDefinition> schemas, string location)
    {
        var validation = RuntimeValueValidator.Validate(value, type, schemas, location);
        if (!validation.Succeeded)
        {
            var detail = string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new FuwenZhinuExecutionException(
                $"The value at {location} does not satisfy its admitted type. {detail}");
        }
    }
}
