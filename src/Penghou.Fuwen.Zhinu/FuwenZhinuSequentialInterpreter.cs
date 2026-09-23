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
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        if (input.ValueKind == JsonValueKind.Undefined)
            throw new FuwenZhinuExecutionException("Workflow input is undefined JSON.");

        var inputValue = FuwenRuntimeValueWire.FromJson(input, plan.InputType, plan.Schemas);
        EnsureType(inputValue, plan.InputType, plan.Schemas, "workflow input");

        var state = new FuwenInterpreterState(inputValue);
        var schedule = new FuwenExecutionSchedule(plan);
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
        return FuwenRuntimeValueWire.ToJson(result.Value!);
    }

    private static async Task<RegionResult> ExecuteRegionAsync(
        string regionPath,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        FuwenInterpreterState state,
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
                            if (conditionalNode.Merge is not null)
                            {
                                var selectedBinding = condition ? conditionalNode.Merge.ThenValue : conditionalNode.Merge.ElseValue;
                                var mergedValue = FuwenBindingEvaluator.Evaluate(selectedBinding, plan, state);
                                EnsureType(mergedValue, conditionalNode.Merge.ResultType, plan.Schemas, $"conditional merge '{conditionalNode.StructuralPath}'");
                                var mergePath = conditionalNode.StructuralPath + "/$merge";
                                var requestJson = FuwenRuntimeValueWire.Serialize(new ConditionalMergeRequestIdentity(mergePath, mergedValue));
                                var outputJson = await context.StepAsync<JsonElement, JsonElement>(
                                    mergePath,
                                    requestJson,
                                    (_, _, _) => Task.FromResult(FuwenRuntimeValueWire.ToJson(mergedValue)),
                                    stepOptions: StepOptionsFor(inheritedDependencies, []),
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
                                var output = FuwenRuntimeValueWire.FromJson(outputJson, conditionalNode.Merge.ResultType, plan.Schemas);
                                EnsureType(output, conditionalNode.Merge.ResultType, plan.Schemas, $"conditional merge '{conditionalNode.StructuralPath}' output");
                                state.Outputs[conditionalNode.StructuralPath] = output;
                            }
                            break;
                        }
                    case RepeatNode repeatNode:
                        state.Outputs[nodePath] = await ExecuteRepeatAsync(
                            repeatNode, plan, executionFingerprint, ports, context, schedule, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case CheckpointNode checkpointNode:
                        state.Outputs[nodePath] = await ExecuteCheckpointAsync(
                            checkpointNode, plan, executionFingerprint, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case WaitNode waitNode:
                        state.Outputs[nodePath] = await ExecuteWaitAsync(
                            waitNode, plan, executionFingerprint, context, state, inheritedDependencies, cancellationToken).ConfigureAwait(false);
                        break;
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
                            var value = FuwenBindingEvaluator.Evaluate(returnNode.Value, plan, state);
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
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var identity = new NodeRequestIdentity("context", node.StructuralPath, node.Provider, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return FuwenRuntimeValueWire.Serialize(await ExecuteProviderAsync(
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

        var envelope = FuwenEnvelopeValidator.Read(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId, ports.PriorExecutionFingerprints);
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        EnsureType(envelope.Output!, node.OutputType, plan.Schemas, $"context node '{node.StructuralPath}' output");
        if (envelope.ContextSnapshot is null || !Equals(envelope.ContextSnapshot.Provider, node.Provider))
            throw new FuwenZhinuExecutionException(
                $"Persisted context result for '{node.StructuralPath}' has missing or mismatched snapshot evidence.");
        state.Snapshots[node.StructuralPath] = envelope.ContextSnapshot;
        return envelope.Output!;
    }

    private static ProtocolLoopRunner RootLoopRunner(WorkflowContext context) =>
        (name, initial, body, options, token) =>
            context.LoopAsync(name, initial, _ => true, body, options, token);

    private static ProtocolLoopRunner IterationLoopRunner(WorkflowLoopIteration<JsonElement> iteration) =>
        (name, initial, body, options, token) =>
            iteration.LoopAsync(name, initial, _ => true, body, options, token);

    private static List<InferenceContextInput> BuildContextInputs(
        InferenceNode node,
        WorkflowPlan plan,
        FuwenInterpreterState state)
    {
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
        return contextInputs;
    }

    private static (JsonElement RequestJson, Func<ExecutionInvocation, InferenceExecutionRequest> CreateRequest)
        BuildInferenceRequest(
            InferenceNode node,
            WorkflowPlan plan,
            FuwenInterpreterState state,
            IReadOnlyList<RuntimeArgument> arguments,
            IReadOnlyList<InferenceContextInput> contextInputs,
            string? nodePathOverride = null)
    {
        var nodePath = nodePathOverride ?? node.StructuralPath;
        if (node.PromptName is null)
        {
            var toolList = node.Tools is null || node.Tools.Count == 0 ? null : node.Tools.ToArray();
            var templateRequestTools = node.Tools?.ToArray() ?? [];
            var identity = new NodeRequestIdentity("inference", nodePath, node.Profile, node.PromptTemplate, arguments, contextInputs, toolList, node.Limits);
            var requestJson = FuwenRuntimeValueWire.Serialize(identity);
            return (requestJson, invocation => new InferenceExecutionRequest(
                invocation,
                node.Profile,
                node.PromptTemplate,
                arguments,
                contextInputs,
                node.OutputType,
                null,
                null,
                templateRequestTools,
                node.Limits));
        }

        var definition = plan.Prompts?.FirstOrDefault(
            prompt => string.Equals(prompt.Name, node.PromptName, StringComparison.Ordinal))
            ?? throw new FuwenZhinuExecutionException(
                $"Inference node '{node.StructuralPath}' references unknown prompt '{node.PromptName}'.");
        var values = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
        foreach (var binding in node.PromptBindings ?? [])
            values[binding.ParameterName] = FuwenBindingEvaluator.Evaluate(binding.Value, plan, state);
        var registeredArguments = definition.Parameters
            .Where(parameter => values.ContainsKey(parameter.Name))
            .Select(parameter => new RuntimeArgument(parameter.Name, values[parameter.Name]))
            .ToArray();
        if (definition.RegisteredSource is not null)
        {
            var aliasIdentity = new PromptNodeRequestIdentity(
                "inference-registered-prompt",
                nodePath,
                node.Profile,
                definition.GetSemanticDigest(),
                registeredArguments,
                node.Tools is null || node.Tools.Count == 0 ? null : node.Tools.ToArray(),
                contextInputs,
                node.Limits);
            var aliasJson = FuwenRuntimeValueWire.Serialize(aliasIdentity);
            return (aliasJson, invocation => new InferenceExecutionRequest(
                invocation,
                node.Profile,
                definition.RegisteredSource,
                registeredArguments,
                contextInputs,
                node.OutputType,
                tools: node.Tools?.ToArray() ?? [],
                limits: node.Limits));
        }
        var rendered = PromptRenderer.Render(definition, values);
        var tools = node.Tools is null || node.Tools.Count == 0 ? null : node.Tools.ToArray();
        var requestTools = node.Tools?.ToArray() ?? [];
        var promptIdentity = new PromptNodeRequestIdentity(
            "inference-prompt",
            nodePath,
            node.Profile,
            definition.GetSemanticDigest(),
            values.Select(pair => new RuntimeArgument(pair.Key, pair.Value)).ToArray(),
            tools,
            contextInputs,
            node.Limits);
        var promptJson = FuwenRuntimeValueWire.Serialize(promptIdentity);
        return (promptJson, invocation => new InferenceExecutionRequest(
            invocation,
            node.Profile,
            null,
            arguments,
            contextInputs,
            node.OutputType,
            definition,
            rendered,
            requestTools,
            node.Limits));
    }

    private static async Task<RuntimeValue> ExecuteInferenceAsync(
        InferenceNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var contextInputs = BuildContextInputs(node, plan, state);

        // The durable protocol coordinator owns its own loop behind the same
        // logical node. It runs only when the host supplies a turn executor;
        // otherwise the node keeps its established one-call step behavior.
        if (node.Protocol is not null && ports.TurnExecutor is not null)
            return await FuwenInferenceCoordinator.ExecuteAsync(
                node, plan, executionFingerprint, ports, RootLoopRunner(context), state, contextInputs,
                node.StructuralPath, cancellationToken).ConfigureAwait(false);

        var (requestJson, createRequest) = BuildInferenceRequest(node, plan, state, arguments, contextInputs);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return FuwenRuntimeValueWire.Serialize(await ExecuteProviderAsync(
                    ports,
                    invocation,
                    node.StructuralPath,
                    token => ports.InferenceExecutor.ExecuteAsync(createRequest(invocation), token),
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
                node.ContextRequirements!.Select(static requirement => requirement.Source)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var envelope = FuwenEnvelopeValidator.Read(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId, ports.PriorExecutionFingerprints);
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        EnsureType(envelope.Output!, node.OutputType, plan.Schemas, $"inference node '{node.StructuralPath}' output");
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteActivityAsync(
        ActivityNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var identity = new NodeRequestIdentity("activity", node.StructuralPath, node.Activity, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        var envelopeJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            async (_, step, token) =>
            {
                var invocation = CreateInvocation(executionFingerprint, node.StructuralPath, requestJson, step);
                return FuwenRuntimeValueWire.Serialize(await ExecuteProviderAsync(
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

        var envelope = FuwenEnvelopeValidator.Read(envelopeJson, node.StructuralPath, plan, executionFingerprint, RequestFingerprint(requestJson), context.WorkflowRunId, ports.PriorExecutionFingerprints);
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
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
        var requestJson = FuwenRuntimeValueWire.Serialize(new ReturnRequestIdentity(node.StructuralPath, value));
        var outputJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            requestJson,
            (_, _, _) => Task.FromResult(FuwenRuntimeValueWire.ToJson(value)),
            stepOptions: StepOptionsForBindings(inheritedDependencies, [node.Value]),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(outputJson, plan.OutputType, plan.Schemas);
        EnsureType(output, plan.OutputType, plan.Schemas, "workflow output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteFanOutAsync(
        FanOutNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var source = FuwenBindingEvaluator.Evaluate(node.Source, plan, state);
        // Provider adapters are allowed to return any representation that
        // validates against the admitted type. In particular, Baize returns
        // structured JSON as JsonRuntimeValue, while the interpreter's
        // composite execution uses ListRuntimeValue. Normalize at this
        // typed control-flow boundary so fan-out does not depend on the
        // provider's in-memory representation. The conversion also walks
        // nested values and reconstructs nominal artifacts from their wire
        // form, preserving artifact values during normalization.
        var list = FuwenRuntimeValueWire.NormalizeList(source, node.Item.Type, node.MaximumItems, plan.Schemas);
        if (list.Items.Count > node.MaximumItems)
            throw new FuwenZhinuExecutionException($"Fan-out source '{node.StructuralPath}' exceeded its declared bounded collection.");

        var keyed = new List<(RuntimeIdentityKey Key, RuntimeValue Item, string RuntimePath)>();
        foreach (var item in list.Items)
        {
            var itemState = new FuwenInterpreterState(state.Input) { CurrentItem = item };
            var key = FuwenFanOutCoordinator.ToRuntimeKey(FuwenBindingEvaluator.Evaluate(node.Key, plan, itemState));
            keyed.Add((key, item, RuntimeNodeIdentity.CreateFanOutItem(node.StructuralPath, key)));
        }
        RuntimeNodeIdentity.ValidateUniqueKeys(keyed.Select(static value => value.Key));

        var hostConcurrency = Math.Min(ports.ExecutionOptions.MaximumFanOutConcurrency, node.MaximumConcurrency);
        var outcomes = await FuwenFanOutCoordinator.ExecuteBoundedAsync(
            keyed,
            hostConcurrency,
            async (value, fanOutToken) =>
        {
            var request = FuwenRuntimeValueWire.Serialize(new FanOutItemRequestIdentity(node.StructuralPath, value.RuntimePath, value.Key, value.Item));
            var output = await context.StepAsync<JsonElement, JsonElement>(
                value.RuntimePath,
                request,
                async (_, step, token) =>
                {
                    var itemState = new FuwenInterpreterState(state.Input) { CurrentItem = value.Item };
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
                        return FuwenRuntimeValueWire.Serialize(new FanOutItemOutcome(value.Key, value.RuntimePath, FuwenRuntimeValueWire.ToJson(yielded), null));
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
                        return FuwenRuntimeValueWire.Serialize(new FanOutItemOutcome(value.Key, value.RuntimePath, null, failure));
                    }
                },
                stepOptions: StepOptionsForBindings(inheritedDependencies, [node.Source]),
                cancellationToken: fanOutToken).ConfigureAwait(false);
            return (value, Outcome: FuwenFanOutCoordinator.ReadOutcome(output, value.Key, value.RuntimePath));
        },
            cancellationToken).ConfigureAwait(false);
        var aggregateRequest = FuwenRuntimeValueWire.Serialize(new FanOutAggregateRequestIdentity(node.StructuralPath, outcomes.Select(static item => item.value.RuntimePath).ToArray()));
        var aggregate = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            aggregateRequest,
            (_, _, _) => Task.FromResult(FuwenRuntimeValueWire.Serialize(outcomes.Select(static item => item.Outcome).ToArray())),
            stepOptions: new StepOptions
            {
                DependsOn = outcomes.Select(static item => item.value.RuntimePath).ToArray(),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var persisted = FuwenFanOutCoordinator.ReadOutcomes(aggregate, keyed);
        var failureOutcome = persisted.FirstOrDefault(static item => item.Failure is not null);
        if (failureOutcome is not null)
            throw new FuwenZhinuExecutionException(failureOutcome.Failure!);

        var resultItemType = ((ListType)node.ResultType).ItemType;
        var values = persisted.Select(item => FuwenRuntimeValueWire.FromJson(item.Output!.Value, resultItemType, plan.Schemas)).ToArray();
        return RuntimeValue.FromList(values);
    }

    private static async Task<RuntimeValue> ExecuteRepeatAsync(
        RepeatNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var initialState = FuwenBindingEvaluator.Evaluate(node.InitialState, plan, state);
        EnsureType(initialState, node.StateType, plan.Schemas, $"repeat '{node.StructuralPath}' initial state");
        var initialJson = FuwenRuntimeValueWire.ToJson(initialState);

        // Zhinu persists the loop count via $loop/<name>/<iter>/condition/commit.
        // GetLoopProgressAsync exposes the persisted count for resume-testing:
        // restarting an iteration step reuses earlier committed iterations and
        // re-runs only the selected iteration and its dependents.
        var resultJson = await FuwenRepeatCoordinator.ExecuteAsync(
            context,
            node,
            initialJson,
            async (iteration, token) =>
            {
                var iterationState = FuwenRuntimeValueWire.FromJson(iteration.State, node.StateType, plan.Schemas);
                EnsureType(iterationState, node.StateType, plan.Schemas, $"repeat '{node.StructuralPath}' iteration state");
                state.CurrentLoopState = iterationState;
                state.CurrentLoopIteration = iteration.Iteration;
                try
                {
                    await ExecuteRepeatRegionAsync(
                        node.StructuralPath + "/$body", node, plan, executionFingerprint, ports, context, schedule, iteration, state, token).ConfigureAwait(false);
                    var nextState = FuwenBindingEvaluator.Evaluate(node.ContinueWith, plan, state);
                    EnsureType(nextState, node.StateType, plan.Schemas, $"repeat '{node.StructuralPath}' next state");
                    var nextJson = FuwenRuntimeValueWire.ToJson(nextState);
                    state.CurrentLoopState = nextState;
                    var breakLeft = FuwenBindingEvaluator.Evaluate(node.BreakWhen.Left, plan, state);
                    var breakRight = node.BreakWhen.Right is null ? null : FuwenBindingEvaluator.Evaluate(node.BreakWhen.Right, plan, state);
                    if (FuwenConditionEvaluator.Evaluate(node.BreakWhen.Operator, breakLeft, breakRight))
                        return iteration.Break(nextJson);
                    return iteration.Continue(nextJson);
                }
                finally
                {
                    state.CurrentLoopState = null;
                    state.CurrentLoopIteration = null;
                }
            },
            cancellationToken).ConfigureAwait(false);

        var resultValue = FuwenRuntimeValueWire.FromJson(resultJson, node.ResultType, plan.Schemas);
        EnsureType(resultValue, node.ResultType, plan.Schemas, $"repeat '{node.StructuralPath}' result");
        return resultValue;
    }

    private static async Task<RuntimeValue> ExecuteCheckpointAsync(
        CheckpointNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var value = FuwenBindingEvaluator.Evaluate(node.Value, plan, state);
        EnsureType(value, node.OutputType, plan.Schemas, $"checkpoint '{node.StructuralPath}' value");
        var valueJson = FuwenRuntimeValueWire.ToJson(value);
        var outputJson = await context.StepAsync<JsonElement, JsonElement>(
            node.StructuralPath,
            valueJson,
            (_, _, _) => Task.FromResult(valueJson),
            stepOptions: StepOptionsForBindings(inheritedDependencies, [node.Value]),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(outputJson, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"checkpoint '{node.StructuralPath}' output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteWaitAsync(
        WaitNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var timeout = node.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var outputJson = await context.WaitForSignalAsync<JsonElement>(
            node.StructuralPath,
            node.SignalName,
            timeout,
            cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(outputJson, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"wait '{node.StructuralPath}' signal output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteRepeatCheckpointAsync(
        RepeatNode repeat,
        CheckpointNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        var value = FuwenBindingEvaluator.Evaluate(node.Value, plan, state);
        EnsureType(value, node.OutputType, plan.Schemas, $"repeat checkpoint '{node.StructuralPath}' value");
        var valueJson = FuwenRuntimeValueWire.ToJson(value);
        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(node.StructuralPath, repeat.StructuralPath);
        var result = await iteration.StepAsync(
            stepSuffix,
            valueJson,
            (_, _, _) => Task.FromResult(valueJson),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(result, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"repeat checkpoint '{node.StructuralPath}' output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteRepeatWaitAsync(
        RepeatNode repeat,
        WaitNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        var timeout = node.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(node.StructuralPath, repeat.StructuralPath);
        var outputJson = await iteration.StepAsync(
            stepSuffix,
            node.SignalName,
            async (signalName, step, token) =>
            {
                var result = await context.WaitForSignalAsync<JsonElement>(
                    step.StepKey,
                    signalName,
                    timeout,
                    token).ConfigureAwait(false);
                return result;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(outputJson, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"repeat wait '{node.StructuralPath}' signal output");
        return output;
    }

    private static async Task ExecuteRepeatRegionAsync(
        string regionPath,
        RepeatNode repeat,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
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
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatActivityAsync(
                            repeat, activity, plan, executionFingerprint, ports, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    case ContextNode contextNode:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatContextAsync(
                            repeat, contextNode, plan, executionFingerprint, ports, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    case InferenceNode inference:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatInferenceAsync(
                            repeat, inference, plan, executionFingerprint, ports, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConditionalNode conditional:
                        await ExecuteRepeatConditionalAsync(
                            repeat, conditional, plan, executionFingerprint, ports, context, schedule, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    case CheckpointNode checkpoint:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatCheckpointAsync(
                            repeat, checkpoint, plan, executionFingerprint, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    case WaitNode wait:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatWaitAsync(
                            repeat, wait, plan, executionFingerprint, context, iteration, state, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new FuwenZhinuAdapterException($"Repeat bodies currently support activity nodes, context nodes, inference nodes, conditionals, checkpoints, and waits; '{bodyNode.GetType().Name}' at '{nodePath}' is not executable here.");
                }
            }
        }
    }

    private static async Task ExecuteRepeatConditionalAsync(
        RepeatNode repeat,
        ConditionalNode conditional,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        // Conditions are pure and deterministic over already-persisted values,
        // so they are evaluated inline like fan-out conditions: no durable step.
        var left = FuwenBindingEvaluator.Evaluate(conditional.Condition.Left, plan, state);
        var right = conditional.Condition.Right is null ? null : FuwenBindingEvaluator.Evaluate(conditional.Condition.Right, plan, state);
        var selectedRegion = FuwenConditionEvaluator.Evaluate(conditional.Condition.Operator, left, right)
            ? $"{conditional.StructuralPath}/$then"
            : $"{conditional.StructuralPath}/$else";
        await ExecuteRepeatRegionAsync(
            selectedRegion, repeat, plan, executionFingerprint, ports, context, schedule, iteration, state, cancellationToken).ConfigureAwait(false);
        if (conditional.Merge is null)
            return;
        var selectedBinding = FuwenConditionEvaluator.Evaluate(conditional.Condition.Operator, left, right)
            ? conditional.Merge.ThenValue
            : conditional.Merge.ElseValue;
        var mergedValue = FuwenBindingEvaluator.Evaluate(selectedBinding, plan, state);
        EnsureType(mergedValue, conditional.Merge.ResultType, plan.Schemas, $"repeat conditional merge '{conditional.StructuralPath}'");
        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(conditional.StructuralPath, repeat.StructuralPath) + "-merge";
        var mergePath = conditional.StructuralPath + "/$merge";
        var requestJson = FuwenRuntimeValueWire.Serialize(new ConditionalMergeRequestIdentity(mergePath, mergedValue));
        var outputJson = await iteration.StepAsync(
            stepSuffix,
            requestJson,
            (_, _, _) => Task.FromResult(FuwenRuntimeValueWire.ToJson(mergedValue)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(outputJson, conditional.Merge.ResultType, plan.Schemas);
        EnsureType(output, conditional.Merge.ResultType, plan.Schemas, $"repeat conditional merge '{conditional.StructuralPath}' output");
        state.Outputs[conditional.StructuralPath] = output;
    }

    private static async Task<RuntimeValue> ExecuteRepeatActivityAsync(
        RepeatNode repeat,
        ActivityNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(node.StructuralPath, repeat.StructuralPath);
        var identity = new NodeRequestIdentity("activity", node.StructuralPath, node.Activity, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        var runtimePath = RuntimeNodeIdentity.CreateIteration(repeat.StructuralPath, iteration.Iteration, stepSuffix);
        var result = await iteration.StepAsync(
            stepSuffix,
            requestJson,
            async (_, step, token) =>
            {
                var inv = CreateInvocation(executionFingerprint, node.StructuralPath, runtimePath, requestJson, step);
                var envelope = await ExecuteProviderAsync(
                    ports, inv, node.StructuralPath,
                    t => ports.ActivityExecutor.ExecuteAsync(new ActivityExecutionRequest(inv, node.Activity, arguments, node.OutputType), t),
                    async r =>
                    {
                        EnsureType(r.Output!, node.OutputType, plan.Schemas, $"repeat activity '{node.StructuralPath}' output");
                        await PublishReceiptsAsync(r.Publications, step, inv, plan, executionFingerprint, node.StructuralPath, token).ConfigureAwait(false);
                        return NodeExecutionEnvelope.Succeeded(inv, r.Output!, null, r.Publications);
                    }, token).ConfigureAwait(false);
                FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
                return FuwenRuntimeValueWire.ToJson(envelope.Output!);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(result, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"repeat activity '{node.StructuralPath}' output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteRepeatContextAsync(
        RepeatNode repeat,
        ContextNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(node.StructuralPath, repeat.StructuralPath);
        var identity = new NodeRequestIdentity("context", node.StructuralPath, node.Provider, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        var runtimePath = RuntimeNodeIdentity.CreateIteration(repeat.StructuralPath, iteration.Iteration, stepSuffix);
        // The full envelope (output + snapshot) is the persisted step
        // value so replay restores snapshot evidence without reinvoking
        // the provider; the snapshot slot below is populated on both
        // fresh execution and replay.
        var result = await iteration.StepAsync(
            stepSuffix,
            requestJson,
            async (_, step, token) =>
            {
                var inv = CreateInvocation(executionFingerprint, node.StructuralPath, runtimePath, requestJson, step);
                var envelope = await ExecuteProviderAsync(
                    ports, inv, node.StructuralPath,
                    t => ports.ContextProvider.ExecuteAsync(new ContextExecutionRequest(inv, node.Provider, arguments, node.OutputType), t),
                    t =>
                    {
                        if (t is not ContextExecutionResult contextResult ||
                            contextResult.ContextSnapshot is null ||
                            !Equals(contextResult.ContextSnapshot.Provider, node.Provider))
                        {
                            throw new FuwenZhinuExecutionException(
                                $"Repeat context provider '{node.Provider.Name}' returned missing or mismatched snapshot evidence.");
                        }
                        EnsureType(t.Output!, node.OutputType, plan.Schemas, $"repeat context '{node.StructuralPath}' output");
                        return Task.FromResult(NodeExecutionEnvelope.Succeeded(inv, t.Output!, contextResult.ContextSnapshot, t.Publications));
                    }, token).ConfigureAwait(false);
                FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
                return FuwenRuntimeValueWire.Serialize(envelope);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        NodeExecutionEnvelope envelope;
        try
        {
            envelope = CanonicalJson.Deserialize<NodeExecutionEnvelope>(CanonicalJson.Canonicalize(result))
                ?? throw new FuwenZhinuExecutionException($"Persisted repeat context result for '{node.StructuralPath}' is null.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new FuwenZhinuExecutionException($"Persisted repeat context result for '{node.StructuralPath}' is malformed: {exception.Message}");
        }
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        if (envelope.ContextSnapshot is null || !Equals(envelope.ContextSnapshot.Provider, node.Provider))
            throw new FuwenZhinuExecutionException(
                $"Persisted repeat context result for '{node.StructuralPath}' has missing or mismatched snapshot evidence.");
        // Loop-carried snapshot slot: keyed by structural path like
        // top-level snapshots, holding the current iteration's
        // evidence for same-iteration inference requirements.
        state.Snapshots[node.StructuralPath] = envelope.ContextSnapshot;
        var output = envelope.Output!;
        EnsureType(output, node.OutputType, plan.Schemas, $"repeat context '{node.StructuralPath}' output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteRepeatInferenceAsync(
        RepeatNode repeat,
        InferenceNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowLoopIteration<JsonElement> iteration,
        FuwenInterpreterState state,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var contextInputs = new List<InferenceContextInput>(node.ContextRequirements!.Count);
        foreach (var requirement in node.ContextRequirements)
        {
            if (!state.Outputs.TryGetValue(requirement.Source.NodePath, out var value) ||
                !state.Snapshots.TryGetValue(requirement.Source.NodePath, out var snapshot))
                throw new FuwenZhinuExecutionException(
                    $"Repeat inference node '{node.StructuralPath}' requires unavailable context '{requirement.Source.NodePath}'.");
            EnsureType(value, requirement.ExpectedType, plan.Schemas,
                $"repeat inference context '{requirement.Name}' for '{node.StructuralPath}'");
            contextInputs.Add(new InferenceContextInput(
                requirement.Name,
                requirement.ExpectedType,
                value,
                snapshot));
        }

        var stepSuffix = FuwenRepeatCoordinator.StepSuffix(node.StructuralPath, repeat.StructuralPath);

        // The durable protocol coordinator nests its loop under the repeat
        // iteration when the host supplies a turn executor.
        if (node.Protocol is not null && ports.TurnExecutor is not null)
            return await FuwenInferenceCoordinator.ExecuteAsync(
                node, plan, executionFingerprint, ports, IterationLoopRunner(iteration), state, contextInputs,
                RuntimeNodeIdentity.CreateIteration(repeat.StructuralPath, iteration.Iteration, stepSuffix),
                cancellationToken).ConfigureAwait(false);

        var (requestJson, createRequest) = BuildInferenceRequest(node, plan, state, arguments, contextInputs);
        var runtimePath = RuntimeNodeIdentity.CreateIteration(repeat.StructuralPath, iteration.Iteration, stepSuffix);
        var result = await iteration.StepAsync(
            stepSuffix,
            requestJson,
            async (_, step, token) =>
            {
                var inv = CreateInvocation(executionFingerprint, node.StructuralPath, runtimePath, requestJson, step);
                var envelope = await ExecuteProviderAsync(
                    ports, inv, node.StructuralPath,
                    t => ports.InferenceExecutor.ExecuteAsync(createRequest(inv), t),
                    async r =>
                    {
                        EnsureType(r.Output!, node.OutputType, plan.Schemas, $"repeat inference '{node.StructuralPath}' output");
                        await PublishReceiptsAsync(r.Publications, step, inv, plan, executionFingerprint, node.StructuralPath, token).ConfigureAwait(false);
                        return NodeExecutionEnvelope.Succeeded(inv, r.Output!, null, r.Publications, r.Evidence);
                    }, token).ConfigureAwait(false);
                FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
                return FuwenRuntimeValueWire.ToJson(envelope.Output!);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = FuwenRuntimeValueWire.FromJson(result, node.OutputType, plan.Schemas);
        EnsureType(output, node.OutputType, plan.Schemas, $"repeat inference '{node.StructuralPath}' output");
        return output;
    }

    private static async Task<RuntimeValue> ExecuteFanOutBodyAsync(
        FanOutNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowContext context,
        FuwenExecutionSchedule schedule,
        WorkflowStepContext itemStep,
        FuwenInterpreterState state,
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
        return FuwenBindingEvaluator.Evaluate(node.Yield, plan, state);
    }

    private static async Task ExecuteFanOutRegionAsync(
        string regionPath,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        FuwenExecutionSchedule schedule,
        WorkflowStepContext itemStep,
        FuwenInterpreterState state,
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
                    case ContextNode contextNode:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteFanOutContextAsync(
                            contextNode, plan, executionFingerprint, ports, itemStep, state, runtimePath, cancellationToken).ConfigureAwait(false);
                        break;
                    case InferenceNode inference:
                        state.Outputs[bodyNode.StructuralPath] = await ExecuteFanOutInferenceAsync(
                            inference, plan, executionFingerprint, ports, itemStep, state, runtimePath, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConditionalNode conditional:
                        var left = FuwenBindingEvaluator.Evaluate(conditional.Condition.Left, plan, state);
                        var right = conditional.Condition.Right is null ? null : FuwenBindingEvaluator.Evaluate(conditional.Condition.Right, plan, state);
                        var selectedRegion = FuwenConditionEvaluator.Evaluate(conditional.Condition.Operator, left, right)
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
                        throw new FuwenZhinuAdapterException("Fan-out bodies currently support activity nodes, context nodes, inference nodes, and control-only conditionals.");
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
        FuwenInterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var bodyMarker = node.StructuralPath.IndexOf("/$body/", StringComparison.Ordinal);
        var bodySuffix = bodyMarker >= 0 ? node.StructuralPath[(bodyMarker + "/$body/".Length)..] : node.Name;
        var runtimeActivityPath = $"{runtimePath}/{bodySuffix}";
        var identity = new NodeRequestIdentity("activity", runtimeActivityPath, node.Activity, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
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
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteFanOutContextAsync(
        ContextNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowStepContext itemStep,
        FuwenInterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        // The enclosing fan-out item step is the durable boundary, so the
        // provider runs directly like fan-out activities; the snapshot is
        // kept in item-scoped state for same-item inference requirements.
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var bodyMarker = node.StructuralPath.IndexOf("/$body/", StringComparison.Ordinal);
        var bodySuffix = bodyMarker >= 0 ? node.StructuralPath[(bodyMarker + "/$body/".Length)..] : node.Name;
        var runtimeContextPath = $"{runtimePath}/{bodySuffix}";
        var identity = new NodeRequestIdentity("context", runtimeContextPath, node.Provider, null, arguments, null);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        var invocation = CreateInvocation(
            executionFingerprint,
            node.StructuralPath,
            runtimeContextPath,
            requestJson,
            itemStep);
        var envelope = await ExecuteProviderAsync(
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
                        $"Fan-out context provider '{node.Provider.Name}' returned missing or mismatched snapshot evidence.");
                }
                EnsureType(result.Output!, node.OutputType, plan.Schemas, $"fan-out context '{node.StructuralPath}' output");
                return Task.FromResult(NodeExecutionEnvelope.Succeeded(
                    invocation,
                    result.Output!,
                    contextResult.ContextSnapshot,
                    result.Publications));
            },
            cancellationToken).ConfigureAwait(false);
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        if (envelope.ContextSnapshot is null || !Equals(envelope.ContextSnapshot.Provider, node.Provider))
            throw new FuwenZhinuExecutionException(
                $"Fan-out context result for '{node.StructuralPath}' has missing or mismatched snapshot evidence.");
        state.Snapshots[node.StructuralPath] = envelope.ContextSnapshot;
        return envelope.Output!;
    }

    private static async Task<RuntimeValue> ExecuteFanOutInferenceAsync(
        InferenceNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        FuwenZhinuExecutionPorts ports,
        WorkflowStepContext itemStep,
        FuwenInterpreterState state,
        string runtimePath,
        CancellationToken cancellationToken)
    {
        var arguments = FuwenBindingEvaluator.EvaluateArguments(node.Arguments, plan, state);
        var contextInputs = new List<InferenceContextInput>(node.ContextRequirements!.Count);
        foreach (var requirement in node.ContextRequirements)
        {
            if (!state.Outputs.TryGetValue(requirement.Source.NodePath, out var value) ||
                !state.Snapshots.TryGetValue(requirement.Source.NodePath, out var snapshot))
                throw new FuwenZhinuExecutionException(
                    $"Fan-out inference node '{node.StructuralPath}' requires unavailable context '{requirement.Source.NodePath}'.");
            EnsureType(value, requirement.ExpectedType, plan.Schemas,
                $"fan-out inference context '{requirement.Name}' for '{node.StructuralPath}'");
            contextInputs.Add(new InferenceContextInput(
                requirement.Name,
                requirement.ExpectedType,
                value,
                snapshot));
        }

        var bodyMarker = node.StructuralPath.IndexOf("/$body/", StringComparison.Ordinal);
        var bodySuffix = bodyMarker >= 0 ? node.StructuralPath[(bodyMarker + "/$body/".Length)..] : node.Name;
        var runtimeInferencePath = $"{runtimePath}/{bodySuffix}";
        var (requestJson, createRequest) = BuildInferenceRequest(node, plan, state, arguments, contextInputs, runtimeInferencePath);
        var invocation = CreateInvocation(
            executionFingerprint,
            node.StructuralPath,
            runtimeInferencePath,
            requestJson,
            itemStep);
        var envelope = await ExecuteProviderAsync(
            ports,
            invocation,
            node.StructuralPath,
            token => ports.InferenceExecutor.ExecuteAsync(createRequest(invocation), token),
            async result =>
            {
                EnsureType(result.Output!, node.OutputType, plan.Schemas, $"fan-out inference '{node.StructuralPath}' output");
                await PublishReceiptsAsync(result.Publications, itemStep, invocation, plan, executionFingerprint, node.StructuralPath, cancellationToken).ConfigureAwait(false);
                return NodeExecutionEnvelope.Succeeded(invocation, result.Output!, null, result.Publications, result.Evidence);
            },
            cancellationToken).ConfigureAwait(false);
        FuwenEnvelopeValidator.ThrowIfFailed(envelope, node.StructuralPath);
        return envelope.Output!;
    }

    private static async Task<bool> EvaluateConditionAsync(
        ConditionalNode node,
        WorkflowPlan plan,
        string executionFingerprint,
        WorkflowContext context,
        FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies,
        CancellationToken cancellationToken)
    {
        var left = FuwenBindingEvaluator.Evaluate(node.Condition.Left, plan, state);
        var right = node.Condition.Right is null ? null : FuwenBindingEvaluator.Evaluate(node.Condition.Right, plan, state);
        var identity = new ConditionRequestIdentity(node.StructuralPath, node.Condition.Operator, left, right);
        var requestJson = FuwenRuntimeValueWire.Serialize(identity);
        return await context.StepAsync<JsonElement, bool>(
            node.StructuralPath,
            requestJson,
            (_, _, _) => Task.FromResult(FuwenConditionEvaluator.Evaluate(node.Condition.Operator, left, right)),
            stepOptions: StepOptionsForBindings(
                inheritedDependencies,
                node.Condition.Right is null
                    ? [node.Condition.Left]
                    : [node.Condition.Left, node.Condition.Right]),
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
                return NodeExecutionEnvelope.Failed(
                    invocation,
                    failure,
                    result is InferenceExecutionResult missingOutputInference ? missingOutputInference.Evidence : null);
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
                return NodeExecutionEnvelope.Failed(
                    invocation,
                    failure,
                    result is InferenceExecutionResult failedProcessingInference ? failedProcessingInference.Evidence : null);
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

    private readonly record struct RegionResult(RuntimeValue? Value, bool Returned);

    private sealed record NodeRequestIdentity(
        string Kind,
        string NodePath,
        DescriptorReference Descriptor,
        DescriptorReference? SecondaryDescriptor,
        IReadOnlyList<RuntimeArgument> Arguments,
        IReadOnlyList<InferenceContextInput>? ContextInputs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<DescriptorReference>? Tools = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        InferenceLimits? Limits = null);

    private sealed record PromptNodeRequestIdentity(
        string Kind,
        string NodePath,
        DescriptorReference Profile,
        string PromptDigest,
        IReadOnlyList<RuntimeArgument> PromptBindings,
        IReadOnlyList<DescriptorReference>? Tools,
        IReadOnlyList<InferenceContextInput>? ContextInputs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        InferenceLimits? Limits = null);

    private sealed record ConditionRequestIdentity(
        string NodePath,
        ConditionOperator Operator,
        RuntimeValue Left,
        RuntimeValue? Right);

    private sealed record ReturnRequestIdentity(string NodePath, RuntimeValue Value);

    private sealed record ConditionalMergeRequestIdentity(string NodePath, RuntimeValue Value);

    private sealed record FanOutItemRequestIdentity(
        string FanOutPath,
        string RuntimePath,
        RuntimeIdentityKey Key,
        RuntimeValue Item);

    private sealed record FanOutAggregateRequestIdentity(
        string FanOutPath,
        IReadOnlyList<string> RuntimeItemPaths);

}

