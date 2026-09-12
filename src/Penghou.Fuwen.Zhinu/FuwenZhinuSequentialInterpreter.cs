using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

internal static class FuwenZhinuSequentialInterpreter
{
    private const string RequestFingerprintContract = "fuwen-request/v1";

    internal static async Task<JsonElement> ExecuteAsync(
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(context);
        WorkflowPlanValidator.Validate(plan);
        if (!string.Equals(plan.IrVersion, FuwenContracts.IrVersionV3, StringComparison.Ordinal) &&
            !string.Equals(plan.IrVersion, FuwenContracts.IrVersionV4, StringComparison.Ordinal))
            throw new FuwenZhinuAdapterException(
                $"The sequential adapter supports '{FuwenContracts.IrVersionV3}' and '{FuwenContracts.IrVersionV4}', not '{plan.IrVersion}'.");
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        if (input.ValueKind == JsonValueKind.Undefined)
            throw new FuwenZhinuExecutionException("Workflow input is undefined JSON.");

        var inputValue = RuntimeValueWire.FromJson(input, plan.InputType, plan.Schemas);
        EnsureType(inputValue, plan.InputType, plan.Schemas, "workflow input");

        var state = new InterpreterState(inputValue);
        var schedule = new ExecutionSchedule(plan);
        var result = await ExecuteRegionAsync(
            plan.Name,
            plan,
            executionFingerprint,
            ports,
            context,
            schedule,
            state,
            cancellationToken,
            inheritedDependencies: []).ConfigureAwait(false);

        if (!result.Returned)
            throw new FuwenZhinuExecutionException("The admitted workflow completed without a return node.");
        EnsureType(result.Value!, plan.OutputType, plan.Schemas, "workflow output");
        return RuntimeValueWire.ToJson(result.Value!);
    }

    private static async Task<RegionResult> ExecuteRegionAsync(
        string regionPath,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        ExecutionSchedule schedule,
        InterpreterState state,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string> inheritedDependencies)
    {
        foreach (var phase in schedule.GetPhases(regionPath))
        {
            // A phase is unordered by contract. Sorting makes the sequential
            // fallback deterministic while preserving the phase barrier.
            foreach (var nodePath in phase.OrderBy(static path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = schedule.GetNode(nodePath);
                switch (node)
                {
                    case ContextNode contextNode:
                        state.Outputs[nodePath] = await ExecuteContextAsync(
                            contextNode, plan, executionFingerprint, ports, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case InferenceNode inferenceNode:
                        state.Outputs[nodePath] = await ExecuteInferenceAsync(
                            inferenceNode, plan, executionFingerprint, ports, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case ActivityNode activityNode:
                        state.Outputs[nodePath] = await ExecuteActivityAsync(
                            activityNode, plan, executionFingerprint, ports, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConditionalNode conditionalNode:
                        {
                            var condition = await EvaluateConditionAsync(
                                conditionalNode, plan, executionFingerprint, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                            var selectedRegion = condition
                                ? $"{conditionalNode.StructuralPath}/$then"
                                : $"{conditionalNode.StructuralPath}/$else";
                            var branch = await ExecuteRegionAsync(
                                selectedRegion, plan, executionFingerprint, ports, context, schedule, state, cancellationToken,
                                inheritedDependencies.Append(conditionalNode.StructuralPath).Distinct(StringComparer.Ordinal).ToArray()).ConfigureAwait(false);
                            if (branch.Returned)
                                return branch;
                            break;
                        }
                    case FanOutNode fanOutNode:
                        state.Outputs[nodePath] = await ExecuteFanOutAsync(
                            fanOutNode,
                            plan,
                            executionFingerprint,
                            ports,
                            context,
                            schedule,
                            state,
                            inheritedDependencies,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case ReturnNode returnNode:
                        {
                            var value = EvaluateBinding(returnNode.Value, plan, state);
                            EnsureType(value, plan.OutputType, plan.Schemas, $"return node '{nodePath}'");
                            var output = await ExecuteReturnAsync(
                                returnNode, value, plan, executionFingerprint, context, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                            return new RegionResult(output, true);
                        }
                    default:
                        throw new FuwenZhinuAdapterException(
                            $"The admitted plan contains unsupported node '{node.GetType().Name}' at '{nodePath}'.");
                }
            }
        }

        return default;
    }

    private static async Task<RuntimeValue> ExecuteContextAsync(
        ContextNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        InterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = EvaluateArguments(node.Arguments, plan, state);
        var identity = new NodeRequestIdentity("context", node.StructuralPath, node.Provider, null, arguments, null);
        var requestJson = RuntimeValueWire.Serialize(identity);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return RuntimeValueWire.Serialize(await ExecuteProviderAsync(
                    ports,
                    invocation,
                    node.StructuralPath,
                    token => ports.ContextProvider.ExecuteAsync(
                        new ContextExecutionRequest(invocation, node.Provider, arguments, node.OutputType), token),
                    result =>
                    {
                        if (result is not ContextExecutionResult contextResult ||
                            contextResult.ContextSnapshot is null ||
                            !Equals(contextResult.ContextSnapshot.Provider, node.Provider))
                        {
                            throw new FuwenZhinuExecutionException(
                                $"Context provider '{node.Provider.Name}' returned missing or mismatched snapshot evidence.");
                        }

                        EnsureType(result.Output!, node.OutputType, plan.Schemas, $"context node '{node.StructuralPath}' output");
                        return Task.FromResult(NodeExecutionEnvelope.Succeeded(
                            invocation,
                            result.Output!,
                            contextResult.ContextSnapshot,
                            result.Publications));
                    },
                    token).ConfigureAwait(false));
            },
            stepOptions: StepOptionsFor(inheritedDependencies, node.Arguments),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var envelope = ReadEnvelope(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId);
        ThrowIfFailed(envelope, node.StructuralPath);
        EnsureType(envelope.Output!, node.OutputType, plan.Schemas, $"context node '{node.StructuralPath}' output");
        if (envelope.ContextSnapshot is null || !Equals(envelope.ContextSnapshot.Provider, node.Provider))
            throw new FuwenZhinuExecutionException(
                $"Persisted context result for '{node.StructuralPath}' has missing or mismatched snapshot evidence.");
        state.Snapshots[node.StructuralPath] = envelope.ContextSnapshot;
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteInferenceAsync(
        InferenceNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        InterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = EvaluateArguments(node.Arguments, plan, state);
        var contextInputs = new List<InferenceContextInput>(node.ContextRequirements!.Count);
        foreach (var requirement in node.ContextRequirements)
        {
            if (!state.Outputs.TryGetValue(requirement.Source.NodePath, out var value) ||
                !state.Snapshots.TryGetValue(requirement.Source.NodePath, out var snapshot))
                throw new FuwenZhinuExecutionException(
                    $"Inference node '{node.StructuralPath}' requires unavailable context '{requirement.Source.NodePath}'.");
            EnsureType(value, requirement.ExpectedType, plan.Schemas,
                $"inference context '{requirement.Name}' for '{node.StructuralPath}'");
            contextInputs.Add(new InferenceContextInput(
                requirement.Name,
                requirement.ExpectedType,
                value,
                snapshot));
        }

        var identity = new NodeRequestIdentity("inference", node.StructuralPath, node.Profile, node.PromptTemplate, arguments, contextInputs);
        var requestJson = RuntimeValueWire.Serialize(identity);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return RuntimeValueWire.Serialize(await ExecuteProviderAsync(
                    ports,
                    invocation,
                    node.StructuralPath,
                    token => ports.InferenceExecutor.ExecuteAsync(
                        new InferenceExecutionRequest(
                            invocation,
                            node.Profile,
                            node.PromptTemplate,
                            arguments,
                            contextInputs,
                            node.OutputType), token),
                    async result =>
                    {
                        EnsureType(result.Output!, node.OutputType, plan.Schemas, $"inference node '{node.StructuralPath}' output");
                        await PublishReceiptsAsync(
                            result.Publications,
                            step,
                            invocation,
                            plan,
                            executionFingerprint,
                            node.StructuralPath,
                            token).ConfigureAwait(false);
                        return NodeExecutionEnvelope.Succeeded(
                            invocation,
                            result.Output!,
                            null,
                            result.Publications,
                            result.Evidence);
                    },
                    token).ConfigureAwait(false));
            },
            stepOptions: StepOptionsFor(
                inheritedDependencies,
                node.Arguments,
                node.ContextRequirements.Select(static requirement => requirement.Source)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var envelope = ReadEnvelope(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId);
        ThrowIfFailed(envelope, node.StructuralPath);
        EnsureType(envelope.Output!, node.OutputType, plan.Schemas, $"inference node '{node.StructuralPath}' output");
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteActivityAsync(
        ActivityNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        InterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = EvaluateArguments(node.Arguments, plan, state);
        var identity = new NodeRequestIdentity("activity", node.StructuralPath, node.Activity, null, arguments, null);
        var requestJson = RuntimeValueWire.Serialize(identity);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return RuntimeValueWire.Serialize(await ExecuteProviderAsync(
                    ports,
                    invocation,
                    node.StructuralPath,
                    token => ports.ActivityExecutor.ExecuteAsync(
                        new ActivityExecutionRequest(invocation, node.Activity, arguments, node.OutputType), token),
                    async result =>
                    {
                        EnsureType(result.Output!, node.OutputType, plan.Schemas, $"activity node '{node.StructuralPath}' output");
                        await PublishReceiptsAsync(
                            result.Publications,
                            step,
                            invocation,
                            plan,
                            executionFingerprint,
                            node.StructuralPath,
                            token).ConfigureAwait(false);
                        return NodeExecutionEnvelope.Succeeded(
                            invocation,
                            result.Output!,
                            null,
                            result.Publications);
                    },
                    token).ConfigureAwait(false));
            },
            stepOptions: StepOptionsFor(inheritedDependencies, node.Arguments),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var envelope = ReadEnvelope(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId);
        ThrowIfFailed(envelope, node.StructuralPath);
        EnsureType(envelope.Output!, node.OutputType, plan.Schemas, $"activity node '{node.StructuralPath}' output");
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteReturnAsync(
        ReturnNode node,
        RuntimeValue value,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var requestJson = RuntimeValueWire.Serialize(new ReturnRequestIdentity(node.StructuralPath, value));
        var outputJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            (_, _, _) => Task.FromResult(RuntimeValueWire.ToJson(value)),
            stepOptions: StepOptionsForBindings(inheritedDependencies, [node.Value]),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = RuntimeValueWire.FromJson(outputJson, plan.OutputType, plan.Schemas);
        EnsureType(output, plan.OutputType, plan.Schemas, "workflow output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteFanOutAsync(
        FanOutNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        ExecutionSchedule schedule,
        InterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var source = EvaluateBinding(node.Source, plan, state);
        if (source is not ListRuntimeValue list || list.Items.Count > node.MaximumItems)
            throw new FuwenZhinuExecutionException($"Fan-out source '{node.StructuralPath}' exceeded its declared bounded collection.");

        var keyed = new List<(RuntimeIdentityKey Key, RuntimeValue Item, string RuntimePath)>();
        foreach (var item in list.Items)
        {
            var itemState = new InterpreterState(state.Input) { CurrentItem = item };
            var key = ToRuntimeKey(EvaluateBinding(node.Key, plan, itemState));
            keyed.Add((key, item, RuntimeNodeIdentity.CreateFanOutItem(node.StructuralPath, key)));
        }
        RuntimeNodeIdentity.ValidateUniqueKeys(keyed.Select(static value => value.Key));

        var hostConcurrency = Math.Min(ports.ExecutionOptions.MaximumFanOutConcurrency, node.MaximumConcurrency);
        using var gate = new SemaphoreSlim(hostConcurrency, hostConcurrency);
        var itemTasks = keyed.Select(async value =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var request = RuntimeValueWire.Serialize(new FanOutItemRequestIdentity(node.StructuralPath, value.RuntimePath, value.Key, value.Item));
                var output = await context.StepAsync<JsonElement, JsonElement>(
                    value.RuntimePath,
                    request,
                    async (_, step, token) =>
                    {
                        var itemState = new InterpreterState(state.Input) { CurrentItem = value.Item };
                        try
                        {
                            var yielded = await ExecuteFanOutBodyAsync(
                                node,
                                plan,
                                executionFingerprint,
                                ports,
                                context,
                                schedule,
                                step,
                                itemState,
                                value.RuntimePath,
                                token).ConfigureAwait(false);
                            EnsureType(yielded, node.ResultType is ListType result ? result.ItemType : node.ResultType, plan.Schemas, $"fan-out item '{value.RuntimePath}' yield");
                            return RuntimeValueWire.Serialize(new FanOutItemOutcome(value.Key, value.RuntimePath, RuntimeValueWire.ToJson(yielded), null));
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            var failure = exception is FuwenZhinuExecutionException execution && execution.Failure is not null
                                ? execution.Failure
                                : ProviderFailure(exception, node.StructuralPath);
                            return RuntimeValueWire.Serialize(new FanOutItemOutcome(value.Key, value.RuntimePath, null, failure));
                        }
                    },
                    stepOptions: StepOptionsForBindings(inheritedDependencies, [node.Source]),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return (value, Outcome: ReadFanOutOutcome(output, value.Key, value.RuntimePath));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        var outcomes = await Task.WhenAll(itemTasks).ConfigureAwait(false);
        var aggregateRequest = RuntimeValueWire.Serialize(new FanOutAggregateRequestIdentity(node.StructuralPath, outcomes.Select(static item => item.value.RuntimePath).ToArray()));
        var aggregate = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            aggregateRequest,
            (_, _, _) => Task.FromResult(RuntimeValueWire.Serialize(outcomes.Select(static item => item.Outcome).ToArray())),
            stepOptions: new StepOptions
            {
                DependsOn = outcomes.Select(static item => item.value.RuntimePath).ToArray(),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var persisted = ReadFanOutOutcomes(aggregate, keyed);
        var failureOutcome = persisted.FirstOrDefault(static item => item.Failure is not null);
        if (failureOutcome is not null)
            throw new FuwenZhinuExecutionException(failureOutcome.Failure!);

        var resultItemType = ((ListType)node.ResultType).ItemType;
        var values = persisted.Select(item => RuntimeValueWire.FromJson(item.Output!.Value, resultItemType, plan.Schemas)).ToArray();
        return RuntimeValue.FromList(values);
    }

    private static async Task<RuntimeValue> ExecuteFanOutBodyAsync(
        FanOutNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        ExecutionSchedule schedule,
        WorkflowStepContext itemStep,
        InterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        await ExecuteFanOutRegionAsync(
            $"{node.StructuralPath}/$body",
            plan,
            executionFingerprint,
            ports,
            schedule,
            itemStep,
            state,
            runtimePath,
            cancellationToken).ConfigureAwait(false);
        return EvaluateBinding(node.Yield, plan, state);
    }

    private static async Task ExecuteFanOutRegionAsync(
        string regionPath,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        ExecutionSchedule schedule,
        WorkflowStepContext itemStep,
        InterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        foreach (var phase in schedule.GetPhases(regionPath))
        {
            foreach (var nodePath in phase.OrderBy(static path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bodyNode = schedule.GetNode(nodePath);
                switch (bodyNode)
                {
                    case ActivityNode activity:
                        state.Outputs[activity.StructuralPath] = await ExecuteFanOutActivityAsync(
                            activity, plan, executionFingerprint, ports, itemStep, state, runtimePath, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConditionalNode conditional:
                        var left = EvaluateBinding(conditional.Condition.Left, plan, state);
                        var right = conditional.Condition.Right is null ? null : EvaluateBinding(conditional.Condition.Right, plan, state);
                        var selectedRegion = EvaluateCondition(conditional.Condition.Operator, left, right)
                            ? $"{conditional.StructuralPath}/$then"
                            : $"{conditional.StructuralPath}/$else";
                        await ExecuteFanOutRegionAsync(
                            selectedRegion,
                            plan,
                            executionFingerprint,
                            ports,
                            schedule,
                            itemStep,
                            state,
                            runtimePath,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new FuwenZhinuAdapterException("Fan-out bodies currently support activity nodes and control-only conditionals.");
                }
            }
        }
    }

    private static async Task<RuntimeValue> ExecuteFanOutActivityAsync(
        ActivityNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowStepContext itemStep,
        InterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        var arguments = EvaluateArguments(node.Arguments, plan, state);
        var bodyMarker = node.StructuralPath.IndexOf("/$body/", StringComparison.Ordinal);
        var bodySuffix = bodyMarker >= 0 ? node.StructuralPath[(bodyMarker + "/$body/".Length)..] : node.Name;
        var runtimeActivityPath = $"{runtimePath}/{bodySuffix}";
        var identity = new NodeRequestIdentity("activity", runtimeActivityPath, node.Activity, null, arguments, null);
        var requestJson = RuntimeValueWire.Serialize(identity);
        var invocation = CreateInvocation(
            executionFingerprint,
            node.StructuralPath,
            runtimeActivityPath,
            requestJson,
            itemStep);
        var envelope = await ExecuteProviderAsync(
            ports,
            invocation,
            node.StructuralPath,
            token => ports.ActivityExecutor.ExecuteAsync(new ActivityExecutionRequest(invocation, node.Activity, arguments, node.OutputType), token),
            async result =>
            {
                EnsureType(result.Output!, node.OutputType, plan.Schemas, $"fan-out activity '{node.StructuralPath}' output");
                await PublishReceiptsAsync(result.Publications, itemStep, invocation, plan, executionFingerprint, node.StructuralPath, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Succeeded(invocation, result.Output!, null, result.Publications);
            },
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(envelope, node.StructuralPath);
        return envelope.Output!;
    }

    private static RuntimeIdentityKey ToRuntimeKey(RuntimeValue value)
    {
        var json = RuntimeValueWire.ToJson(value);
        return json.ValueKind switch
        {
            JsonValueKind.String => new StringRuntimeKey(json.GetString()!),
            JsonValueKind.Number when json.TryGetInt64(out var integer) => new IntegerRuntimeKey(integer),
            JsonValueKind.Null => throw new FuwenZhinuExecutionException("Fan-out keys cannot be null."),
            _ => throw new FuwenZhinuExecutionException("Fan-out keys must be strings or signed integers."),
        };
    }

    private static FanOutItemOutcome ReadFanOutOutcome(JsonElement value, RuntimeIdentityKey expectedKey, string runtimePath)
    {
        var outcome = CanonicalJson.Deserialize<FanOutItemOutcome>(CanonicalJson.Canonicalize(value))
            ?? throw new FuwenZhinuExecutionException($"Persisted fan-out item '{runtimePath}' is malformed.");
        if (!string.Equals(outcome.RuntimePath, runtimePath, StringComparison.Ordinal) || !Equals(outcome.Key, expectedKey) ||
            (outcome.Output is null) == (outcome.Failure is null))
            throw new FuwenZhinuExecutionException($"Persisted fan-out item '{runtimePath}' has invalid identity or outcome evidence.");
        return outcome;
    }

    private static IReadOnlyList<FanOutItemOutcome> ReadFanOutOutcomes(
        JsonElement value,
        IReadOnlyList<(RuntimeIdentityKey Key, RuntimeValue Item, string RuntimePath)> expected)
    {
        var outcomes = CanonicalJson.Deserialize<FanOutItemOutcome[]>(CanonicalJson.Canonicalize(value))
            ?? throw new FuwenZhinuExecutionException("Persisted fan-out aggregate is malformed.");
        if (outcomes.Length != expected.Count)
            throw new FuwenZhinuExecutionException("Persisted fan-out aggregate item count does not match its source collection.");
        for (var index = 0; index < outcomes.Length; index++)
        {
            var outcome = outcomes[index];
            if (outcome is null || !string.Equals(outcome.RuntimePath, expected[index].RuntimePath, StringComparison.Ordinal) ||
                !Equals(outcome.Key, expected[index].Key) || (outcome.Output is null) == (outcome.Failure is null))
                throw new FuwenZhinuExecutionException("Persisted fan-out aggregate item identity or outcome is invalid.");
        }
        RuntimeNodeIdentity.ValidateUniqueKeys(outcomes.Select(static outcome => outcome.Key));
        return outcomes;
    }

    private static async Task<bool> EvaluateConditionAsync(
        ConditionalNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        InterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var left = EvaluateBinding(node.Condition.Left, plan, state);
        var right = node.Condition.Right is null ? null : EvaluateBinding(node.Condition.Right, plan, state);
        var identity = new ConditionRequestIdentity(node.StructuralPath, node.Condition.Operator, left, right);
        var requestJson = RuntimeValueWire.Serialize(identity);
        return await context.StepAsync<JsonElement, bool>(
            node.StructuralPath,
            requestJson,
            (_, _, _) => Task.FromResult(EvaluateCondition(node.Condition.Operator, left, right)),
            stepOptions: StepOptionsForBindings(
                inheritedDependencies,
                node.Condition.Right is null
                    ? [node.Condition.Left]
                    : [node.Condition.Left, node.Condition.Right]),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool EvaluateCondition(ConditionOperator op, RuntimeValue left, RuntimeValue? right)
    {
        var leftJson = RuntimeValueWire.ToJson(left);
        switch (op)
        {
            case ConditionOperator.Exists:
                return leftJson.ValueKind != JsonValueKind.Null;
            case ConditionOperator.Not:
                return !ReadBoolean(leftJson, "not");
            case ConditionOperator.And:
                return ReadBoolean(leftJson, "and") && ReadBoolean(RuntimeValueWire.ToJson(right!), "and");
            case ConditionOperator.Or:
                return ReadBoolean(leftJson, "or") || ReadBoolean(RuntimeValueWire.ToJson(right!), "or");
            case ConditionOperator.Equal:
                return CanonicalJson.Canonicalize(leftJson).AsSpan().SequenceEqual(CanonicalJson.Canonicalize(RuntimeValueWire.ToJson(right!)));
            case ConditionOperator.NotEqual:
                return !CanonicalJson.Canonicalize(leftJson).AsSpan().SequenceEqual(CanonicalJson.Canonicalize(RuntimeValueWire.ToJson(right!)));
            case ConditionOperator.LessThan:
                return Compare(leftJson, RuntimeValueWire.ToJson(right!), "less-than") < 0;
            case ConditionOperator.LessThanOrEqual:
                return Compare(leftJson, RuntimeValueWire.ToJson(right!), "less-than-or-equal") <= 0;
            case ConditionOperator.GreaterThan:
                return Compare(leftJson, RuntimeValueWire.ToJson(right!), "greater-than") > 0;
            case ConditionOperator.GreaterThanOrEqual:
                return Compare(leftJson, RuntimeValueWire.ToJson(right!), "greater-than-or-equal") >= 0;
            default:
                throw new FuwenZhinuAdapterException($"Condition operator '{op}' is unsupported.");
        }
    }

    private static int Compare(JsonElement left, JsonElement right, string operation)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
            left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return string.CompareOrdinal(left.GetString(), right.GetString());
        throw new FuwenZhinuExecutionException($"Condition '{operation}' requires two comparable strings or finite numbers.");
    }

    private static bool ReadBoolean(JsonElement value, string operation) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FuwenZhinuExecutionException($"Condition '{operation}' requires Boolean operands."),
    };

    private static IReadOnlyList<RuntimeArgument> EvaluateArguments(
        IReadOnlyList<ArgumentBinding> bindings,
        WorkflowPlan plan,
        InterpreterState state) => bindings
        .Select(binding => new RuntimeArgument(
            binding.Name,
            EvaluateBinding(binding.Value, plan, state)))
        .ToArray();

    private static RuntimeValue EvaluateBinding(Binding binding, WorkflowPlan plan, InterpreterState state) => binding switch
    {
        InputBinding input => Project(state.Input, input.Projection),
        FanOutItemValueBinding item when state.CurrentItem is not null => Project(state.CurrentItem, item.Projection),
        FanOutItemValueBinding => throw new FuwenZhinuExecutionException("A fan-out item binding was evaluated outside an item body."),
        NodeOutputBinding output when state.Outputs.TryGetValue(output.NodePath, out var value) => Project(value, output.Projection),
        NodeOutputBinding output => throw new FuwenZhinuExecutionException($"Binding refers to unavailable node '{output.NodePath}'."),
        LiteralBinding literal => RuntimeValueWire.FromJson(literal.Value, new PrimitiveType(FuwenPrimitiveKind.Json), plan.Schemas),
        ListBinding list => RuntimeValue.FromList(list.Items.Select(item => EvaluateBinding(item, plan, state)).ToArray()),
        ObjectBinding @object => RuntimeValue.FromObject(@object.Properties.ToDictionary(
            property => property.Key,
            property => EvaluateBinding(property.Value, plan, state),
            StringComparer.Ordinal)),
        _ => throw new FuwenZhinuAdapterException($"Binding '{binding.GetType().Name}' is unsupported by the sequential adapter."),
    };

    private static RuntimeValue Project(RuntimeValue value, IReadOnlyList<string> projection)
    {
        var current = value;
        foreach (var segment in projection)
        {
            current = current switch
            {
                JsonRuntimeValue json when json.Value.ValueKind == JsonValueKind.Object && json.Value.TryGetProperty(segment, out var child)
                    => RuntimeValue.FromJson(child),
                ObjectRuntimeValue @object when @object.Properties.TryGetValue(segment, out var child)
                    => child,
                _ => throw new FuwenZhinuExecutionException($"Projection segment '{segment}' is not available on the bound value."),
            };
        }
        return current;
    }

    private static ExecutionInvocation CreateInvocation(
        string fingerprint,
        string path,
        JsonElement requestJson,
        WorkflowStepContext step) => CreateInvocation(
            fingerprint,
            path,
            $"{step.WorkflowRunId:D}/{path}",
            requestJson,
            step);

    private static ExecutionInvocation CreateInvocation(
        string fingerprint,
        string structuralPath,
        string runtimePath,
        JsonElement requestJson,
        WorkflowStepContext step) => new(
            fingerprint,
            structuralPath,
            runtimePath,
            step.Revision,
            RequestFingerprint(requestJson));

    private static async ValueTask ObserveAsync(
        FuwenZhinuExecutionPorts ports,
        ExecutionInvocation invocation,
        ExecutionObservationKind kind,
        CancellationToken cancellationToken)
    {
        if (ports.Observer is null)
            return;

        try
        {
            await ports.Observer.ObserveAsync(
                new ExecutionObservation(invocation, kind),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Observation is explicitly non-authoritative and must never alter
            // durable workflow state or provider execution.
        }
    }

    private static async Task PublishReceiptsAsync(
        IReadOnlyList<ArtifactPublicationReceipt> receipts,
        WorkflowStepContext step,
        ExecutionInvocation invocation,
        WorkflowPlan plan,
        string executionFingerprint,
        string structuralPath,
        CancellationToken cancellationToken)
    {
        foreach (var receipt in receipts)
        {
            var artifact = receipt.Artifact;
            var descriptor = artifact.ArtifactDescriptor;
            if (!string.Equals(receipt.IdempotencyKey, invocation.OperationKey, StringComparison.Ordinal))
            {
                throw new FuwenZhinuExecutionException(
                    $"Artifact publication receipt '{receipt.ProviderReceiptId}' is not bound to the current operation key.");
            }
            if (!plan.CatalogueBindings.Contains(descriptor))
            {
                throw new FuwenZhinuExecutionException(
                    $"Artifact publication receipt '{receipt.ProviderReceiptId}' uses an artifact descriptor that was not admitted by the workflow plan.");
            }
            var location = $"fuwen://{Uri.EscapeDataString(artifact.Provider)}/{Uri.EscapeDataString(artifact.ArtifactId)}";
            await step.PublishArtifactAsync(
                new WorkflowArtifactDescriptor
                {
                    Name = artifact.LogicalName ?? $"fuwen/{descriptor.Name}/{artifact.ArtifactId}",
                    ArtifactType = descriptor.Name,
                    ArtifactVersion = descriptor.Version,
                    Location = location,
                    ContentHash = $"{artifact.ContentDigest.Algorithm}:{artifact.ContentDigest.Contract}:{artifact.ContentDigest.Value}",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fuwen.executionFingerprint"] = executionFingerprint,
                        ["fuwen.structuralPath"] = structuralPath,
                        ["fuwen.operationKey"] = invocation.OperationKey,
                        ["fuwen.publicationIdempotencyKey"] = receipt.IdempotencyKey,
                        ["fuwen.providerReceiptId"] = receipt.ProviderReceiptId,
                        ["fuwen.publicationDisposition"] = receipt.Disposition.ToString(),
                        ["fuwen.artifactProvider"] = artifact.Provider,
                        ["fuwen.artifactId"] = artifact.ArtifactId,
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static StepOptions? StepOptionsFor(
        IReadOnlyCollection<string> inheritedDependencies,
        IEnumerable<ArgumentBinding> arguments,
        IEnumerable<NodeOutputBinding>? extraBindings = null)
    {
        var dependencies = new HashSet<string>(inheritedDependencies, StringComparer.Ordinal);
        foreach (var argument in arguments)
            CollectDependencies(argument.Value, dependencies);
        if (extraBindings is not null)
            foreach (var binding in extraBindings)
                dependencies.Add(binding.NodePath);
        return dependencies.Count == 0
            ? null
            : new StepOptions { DependsOn = dependencies.OrderBy(static value => value, StringComparer.Ordinal).ToArray() };
    }

    private static StepOptions? StepOptionsForBindings(
        IReadOnlyCollection<string> inheritedDependencies,
        IEnumerable<Binding> bindings)
    {
        var dependencies = new HashSet<string>(inheritedDependencies, StringComparer.Ordinal);
        foreach (var binding in bindings)
            CollectDependencies(binding, dependencies);
        return dependencies.Count == 0
            ? null
            : new StepOptions { DependsOn = dependencies.OrderBy(static value => value, StringComparer.Ordinal).ToArray() };
    }

    private static void CollectDependencies(Binding binding, ISet<string> dependencies)
    {
        switch (binding)
        {
            case NodeOutputBinding output:
                dependencies.Add(output.NodePath);
                break;
            case ListBinding list:
                foreach (var item in list.Items) CollectDependencies(item, dependencies);
                break;
            case ObjectBinding @object:
                foreach (var item in @object.Properties.Values) CollectDependencies(item, dependencies);
                break;
            case FanOutItemValueBinding:
                break;
        }
    }

    private static string RequestFingerprint(JsonElement requestJson) =>
        $"sha256:{RequestFingerprintContract}:{Convert.ToHexString(SHA256.HashData(CanonicalJson.Canonicalize(requestJson))).ToLowerInvariant()}";

    private static async Task<NodeExecutionEnvelope> ExecuteProviderAsync<TResult>(
        FuwenZhinuExecutionPorts ports,
        ExecutionInvocation invocation,
        string nodePath,
        Func<CancellationToken, ValueTask<TResult>> execute,
        Func<TResult, Task<NodeExecutionEnvelope>> buildSuccess,
        CancellationToken cancellationToken)
        where TResult : ExecutionResult
    {
        for (var attempt = 1; ; attempt++)
        {
            await ObserveAsync(ports, invocation, ExecutionObservationKind.Requested, cancellationToken).ConfigureAwait(false);

            TResult? result;
            try
            {
                result = await execute(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A caller/fence cancellation is authoritative and must not be
                // converted into a durable provider failure.
                throw;
            }
            catch (Exception exception)
            {
                var failure = ProviderFailure(exception, nodePath);
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Failed, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Failed(invocation, failure);
            }

            if (result is null)
            {
                var failure = new ExecutionFailure(
                    ExecutionFailureKind.Provider,
                    ExecutionFailureCode.ProviderError,
                    $"Provider '{nodePath}' returned no execution result.",
                    mayHaveCommittedEffect: true);
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Failed, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Failed(invocation, failure);
            }

            if (result.Failure is not null)
            {
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Failed, cancellationToken).ConfigureAwait(false);
                if (result.Failure.RetryDisposition == ExecutionRetryDisposition.InfrastructureOnly &&
                    !result.Failure.MayHaveCommittedEffect &&
                    attempt < ports.ExecutionOptions.MaximumInfrastructureAttempts)
                {
                    continue;
                }

                return NodeExecutionEnvelope.Failed(
                    invocation,
                    result.Failure,
                    result is InferenceExecutionResult failedInference ? failedInference.Evidence : null);
            }

            if (result.Output is null)
            {
                var failure = new ExecutionFailure(
                    ExecutionFailureKind.ProviderOutput,
                    ExecutionFailureCode.MalformedOutput,
                    $"Provider '{nodePath}' returned a successful result without output.",
                    mayHaveCommittedEffect: true);
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Failed, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Failed(invocation, failure);
            }

            try
            {
                var envelope = await buildSuccess(result).ConfigureAwait(false);
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Succeeded, cancellationToken).ConfigureAwait(false);
                return envelope;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = ProviderFailure(exception, nodePath);
                await ObserveAsync(ports, invocation, ExecutionObservationKind.Failed, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Failed(invocation, failure);
            }
        }
    }

    private static ExecutionFailure ProviderFailure(Exception exception, string nodePath)
    {
        if (exception is FuwenZhinuExecutionException executionException && executionException.Failure is not null)
            return executionException.Failure;

        var kind = exception is FuwenZhinuExecutionException
            ? ExecutionFailureKind.Contract
            : ExecutionFailureKind.Provider;
        var code = exception is FuwenZhinuExecutionException
            ? ExecutionFailureCode.BindingFailure
            : ExecutionFailureCode.ProviderError;
        return new ExecutionFailure(
            kind,
            code,
            $"Provider execution for '{nodePath}' failed.",
            mayHaveCommittedEffect: true,
            providerCode: exception.GetType().Name);
    }

    private static void ThrowIfFailed(NodeExecutionEnvelope envelope, string nodePath)
    {
        if (envelope.Failure is not null)
            throw new FuwenZhinuExecutionException(envelope.Failure);
        if (envelope.Output is null)
            throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' has no output.");
    }

    private static NodeExecutionEnvelope ReadEnvelope(
        JsonElement value,
        string nodePath,
        WorkflowPlan plan,
        string executionFingerprint,
        string effectiveRequestFingerprint,
        Guid workflowRunId)
    {
        try
        {
            var envelope = CanonicalJson.Deserialize<NodeExecutionEnvelope>(CanonicalJson.Canonicalize(value));
            if (envelope is null)
                throw new FuwenZhinuExecutionException($"Persisted result for '{nodePath}' is null.");

            var invocation = envelope.Invocation;
            if (invocation is null ||
                !string.Equals(invocation.ExecutionFingerprint, executionFingerprint, StringComparison.Ordinal) ||
                !string.Equals(invocation.StructuralPath, nodePath, StringComparison.Ordinal) ||
                !string.Equals(invocation.EffectiveRequestFingerprint, effectiveRequestFingerprint, StringComparison.Ordinal) ||
                !string.Equals(invocation.RuntimePath, $"{workflowRunId:D}/{nodePath}", StringComparison.Ordinal))
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
            if (envelope.Evidence is not null &&
                (inferenceNode is null ||
                 envelope.Evidence.Profile != inferenceNode.Profile ||
                 envelope.Evidence.PromptTemplate != inferenceNode.PromptTemplate))
                throw new FuwenZhinuExecutionException(
                    $"Persisted inference evidence for '{nodePath}' does not match the admitted profile and prompt template.");

            if (envelope.Failure is not null && envelope.Publications.Count != 0)
                throw new FuwenZhinuExecutionException($"Persisted failed result for '{nodePath}' contains publication evidence.");
            _ = executionFingerprint;
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
            if (node is ConditionalNode conditional)
            {
                foreach (var child in FlattenNodes(conditional.Then)) yield return child;
                foreach (var child in FlattenNodes(conditional.Else)) yield return child;
            }
            else if (node is FanOutNode fanOut)
            {
                foreach (var child in FlattenNodes(fanOut.Body)) yield return child;
            }
        }
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

    private sealed class InterpreterState(RuntimeValue input)
    {
        internal RuntimeValue Input { get; } = input;
        internal Dictionary<string, RuntimeValue> Outputs { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ContextSnapshotReference> Snapshots { get; } = new(StringComparer.Ordinal);
        internal RuntimeValue? CurrentItem { get; set; }
    }

    private readonly record struct RegionResult(RuntimeValue? Value, bool Returned);

    private sealed class ExecutionSchedule
    {
        private readonly IReadOnlyDictionary<string, WorkflowNode> nodes;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> regions;

        internal ExecutionSchedule(WorkflowPlan plan)
        {
            nodes = Flatten(plan.Nodes).ToDictionary(node => node.StructuralPath, StringComparer.Ordinal);
            regions = plan.ExecutionOrder!.Regions.ToDictionary(
                region => region.RegionPath,
                region => (IReadOnlyList<IReadOnlyList<string>>)region.Phases.Select(phase => phase.NodePaths).ToArray(),
                StringComparer.Ordinal);
        }

        internal IReadOnlyList<IReadOnlyList<string>> GetPhases(string regionPath) =>
            regions.TryGetValue(regionPath, out var phases)
                ? phases
                : throw new FuwenZhinuAdapterException($"The admitted plan has no execution region '{regionPath}'.");

        internal WorkflowNode GetNode(string nodePath) =>
            nodes.TryGetValue(nodePath, out var node)
                ? node
                : throw new FuwenZhinuAdapterException($"The admitted plan has no node '{nodePath}'.");

        private static IEnumerable<WorkflowNode> Flatten(IEnumerable<WorkflowNode> source)
        {
            foreach (var node in source)
            {
                yield return node;
                if (node is ConditionalNode conditional)
                {
                    foreach (var child in Flatten(conditional.Then)) yield return child;
                    foreach (var child in Flatten(conditional.Else)) yield return child;
                }
                else if (node is FanOutNode fanOut)
                {
                    foreach (var child in Flatten(fanOut.Body)) yield return child;
                }
            }
        }
    }

    private sealed record NodeRequestIdentity(
        string Kind,
        string NodePath,
        DescriptorReference Descriptor,
        DescriptorReference? SecondaryDescriptor,
        IReadOnlyList<RuntimeArgument> Arguments,
        IReadOnlyList<InferenceContextInput>? ContextInputs);

    private sealed record ConditionRequestIdentity(
        string NodePath,
        ConditionOperator Operator,
        RuntimeValue Left,
        RuntimeValue? Right);

    private sealed record ReturnRequestIdentity(string NodePath, RuntimeValue Value);

    private sealed record FanOutItemRequestIdentity(
        string FanOutPath,
        string RuntimePath,
        RuntimeIdentityKey Key,
        RuntimeValue Item);

    private sealed record FanOutAggregateRequestIdentity(
        string FanOutPath,
        IReadOnlyList<string> RuntimeItemPaths);

    private sealed record FanOutItemOutcome(
        RuntimeIdentityKey Key,
        string RuntimePath,
        JsonElement? Output,
        ExecutionFailure? Failure);

    private sealed class NodeExecutionEnvelope
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
}

internal static class RuntimeValueWire
{
    internal static JsonElement Serialize<T>(T value)
    {
        using var document = JsonDocument.Parse(CanonicalJson.Serialize(value));
        return document.RootElement.Clone();
    }

    internal static RuntimeValue FromJson(JsonElement value, FuwenType expectedType, IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        if (expectedType is OptionalType optional && value.ValueKind != JsonValueKind.Null)
            return FromJson(value, optional.ValueType, schemas);
        if (expectedType is ListType list && value.ValueKind == JsonValueKind.Array)
            return RuntimeValue.FromList(value.EnumerateArray()
                .Select(item => FromJson(item, list.ItemType, schemas))
                .ToArray());
        if (expectedType is NamedTypeReference named && value.ValueKind == JsonValueKind.Object &&
            schemas.FirstOrDefault(schema => schema.Descriptor == named.Schema) is ObjectSchemaDefinition objectSchema)
        {
            var properties = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var field = objectSchema.Fields.FirstOrDefault(candidate => candidate.Name == property.Name);
                properties[property.Name] = field is null
                    ? RuntimeValue.FromJson(property.Value)
                    : FromJson(property.Value, field.Type, schemas);
            }
            return RuntimeValue.FromObject(properties);
        }
        if (expectedType is ArtifactType && value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("$kind", out var kind) && string.Equals(kind.GetString(), "artifact", StringComparison.Ordinal))
        {
            var artifact = new ArtifactReference(
                value.GetProperty("provider").GetString()!,
                value.GetProperty("artifactId").GetString()!,
                CanonicalJson.Deserialize<DescriptorReference>(CanonicalJson.Canonicalize(value.GetProperty("artifactDescriptor"))),
                CanonicalJson.Deserialize<ContentDigest>(CanonicalJson.Canonicalize(value.GetProperty("contentDigest"))),
                value.TryGetProperty("byteLength", out var length) && length.ValueKind != JsonValueKind.Null ? length.GetInt64() : null,
                value.TryGetProperty("logicalName", out var logical) && logical.ValueKind != JsonValueKind.Null ? logical.GetString() : null);
            return RuntimeValue.FromArtifact(artifact);
        }
        return RuntimeValue.FromJson(value);
    }

    internal static JsonElement ToJson(RuntimeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, RuntimeValue value)
    {
        switch (value)
        {
            case JsonRuntimeValue json:
                json.Value.WriteTo(writer);
                break;
            case ListRuntimeValue list:
                writer.WriteStartArray();
                foreach (var item in list.Items) Write(writer, item);
                writer.WriteEndArray();
                break;
            case ObjectRuntimeValue @object:
                writer.WriteStartObject();
                foreach (var property in @object.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case ArtifactRuntimeValue artifact:
                writer.WriteStartObject();
                writer.WriteString("$kind", "artifact");
                writer.WriteString("provider", artifact.Artifact.Provider);
                writer.WriteString("artifactId", artifact.Artifact.ArtifactId);
                writer.WritePropertyName("artifactDescriptor");
                using (var descriptor = JsonDocument.Parse(CanonicalJson.Serialize(artifact.Artifact.ArtifactDescriptor)))
                    descriptor.RootElement.WriteTo(writer);
                writer.WritePropertyName("contentDigest");
                using (var digest = JsonDocument.Parse(CanonicalJson.Serialize(artifact.Artifact.ContentDigest)))
                    digest.RootElement.WriteTo(writer);
                if (artifact.Artifact.ByteLength is null) writer.WriteNull("byteLength");
                else writer.WriteNumber("byteLength", artifact.Artifact.ByteLength.Value);
                if (artifact.Artifact.LogicalName is null) writer.WriteNull("logicalName");
                else writer.WriteString("logicalName", artifact.Artifact.LogicalName);
                writer.WriteEndObject();
                break;
            default:
                throw new FuwenZhinuAdapterException($"Runtime value kind '{value.GetType().Name}' is unsupported.");
        }
    }
}
