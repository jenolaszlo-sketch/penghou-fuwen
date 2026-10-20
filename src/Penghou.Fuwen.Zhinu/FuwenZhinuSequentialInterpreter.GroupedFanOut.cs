using System.Text.Json;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

internal static partial class FuwenZhinuSequentialInterpreter
{
    private const int MaximumGroupedFanOutVectorBytes = 1_048_576;
    private static string? NodeRuntimePath(FuwenInterpreterState state, string path) =>
        state.FanOutScope is null ? null : RuntimeNodeIdentity.CreateFanOutItem(state.FanOutRuntimePath ?? state.FanOutPath!,
            state.FanOutKey!, FuwenZhinuStepKeys.RepeatBodyStepName(path, state.FanOutPath!, true));

    private static ExecutionInvocation CreateNodeInvocation(FuwenInterpreterState state, string fingerprint,
        string path, JsonElement request, WorkflowStepContext step) => NodeRuntimePath(state, path) is { } runtimePath
            ? CreateInvocation(fingerprint, path, $"{step.WorkflowRunId:D}/{runtimePath}", request, step)
            : CreateInvocation(fingerprint, path, request, step);

    private static Task<JsonElement> ExecuteNodeStepAsync(FuwenInterpreterState state, WorkflowContext context,
        string path, JsonElement input, Func<JsonElement, WorkflowStepContext, CancellationToken, Task<JsonElement>> operation,
        StepOptions? stepOptions, CancellationToken cancellationToken) => state.FanOutScope is { } scope
            ? scope.StepAsync(FuwenZhinuStepKeys.RepeatBodyStepName(path, state.FanOutPath!, true),
                input, operation, stepOptions, cancellationToken)
            : context.StepAsync(path, input, operation, stepOptions, cancellationToken);

    private static async Task<RuntimeValue> ExecuteGroupedFanOutAsync(FanOutNode node, WorkflowPlan plan,
        string fingerprint, FuwenZhinuExecutionPorts ports, WorkflowContext context,
        FuwenExecutionSchedule schedule, FuwenInterpreterState state,
        IReadOnlyCollection<string> inheritedDependencies, CancellationToken cancellationToken)
    {
        var explicitSelection = node.Selection;
        GroupedFanOutInput[] selected;
        int? epoch = null;
        int replacesEpoch = 0;
        var runtimePath = node.StructuralPath;
        if (explicitSelection is not null)
        {
            // Evaluated values are output of this boundary, not its input identity.
            // Restart is explicit; replay never consults today's selector values.
            var selection = await context.StepAsync(
                FuwenZhinuStepKeys.GroupedFanOutSelection(node.StructuralPath),
                FuwenRuntimeValueWire.Serialize(new { Contract = "fuwen-selection/v1", Node = node.StructuralPath, Fingerprint = fingerprint }),
                (_, _, _) =>
                {
                    var next = ReadEpoch(explicitSelection.Epoch, plan, state);
                    var previous = ReadEpoch(explicitSelection.ReplacesEpoch, plan, state);
                    ValidateEpochs(next, previous, explicitSelection.MaximumEpochs);
                    var vector = FuwenRuntimeValueWire.Serialize(new GroupedSelectionVector(next, previous,
                        BuildSelectedInputs(node, plan, state, FuwenZhinuStepKeys.SelectionRuntimePath(node.StructuralPath, next))));
                    if (CanonicalJson.Canonicalize(vector).Length > MaximumGroupedFanOutVectorBytes)
                        throw new FuwenZhinuExecutionException("Selection exceeds its 1 MiB vector budget.");
                    return Task.FromResult(vector);
                },
                GroupOptions(state, context, node.StructuralPath,
                    StepOptionsForBindings(inheritedDependencies, [node.Source, explicitSelection.Epoch, explicitSelection.ReplacesEpoch]),
                    ActivityGroupMemberRole.Control), cancellationToken).ConfigureAwait(false);
            var bytes = CanonicalJson.Canonicalize(selection);
            if (bytes.Length > MaximumGroupedFanOutVectorBytes)
                throw new FuwenZhinuExecutionException("Persisted selection exceeds its vector budget.");
            var vector = CanonicalJson.Deserialize<GroupedSelectionVector>(bytes)
                ?? throw new FuwenZhinuExecutionException("Persisted selection is missing.");
            ValidateEpochs(vector.Epoch, vector.ReplacesEpoch, explicitSelection.MaximumEpochs);
            epoch = vector.Epoch;
            replacesEpoch = vector.ReplacesEpoch;
            runtimePath = FuwenZhinuStepKeys.SelectionRuntimePath(node.StructuralPath, vector.Epoch);
            selected = vector.Items;
        }
        else
        {
            var inputs = BuildSelectedInputs(node, plan, state, runtimePath);
            var selection = await context.StepAsync(FuwenZhinuStepKeys.GroupedFanOutSelection(node.StructuralPath),
                FuwenRuntimeValueWire.Serialize(inputs), (value, _, _) => Task.FromResult(value),
                GroupOptions(state, context, node.StructuralPath,
                    StepOptionsForBindings(inheritedDependencies, [node.Source]), ActivityGroupMemberRole.Control),
                cancellationToken).ConfigureAwait(false);
            var bytes = CanonicalJson.Canonicalize(selection);
            if (bytes.Length > MaximumGroupedFanOutVectorBytes || !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(inputs)))
                throw new FuwenZhinuExecutionException("Persisted grouped selection differs from its admitted input or exceeds its budget.");
            selected = CanonicalJson.Deserialize<GroupedFanOutInput[]>(bytes)
                ?? throw new FuwenZhinuExecutionException("Persisted grouped selection is missing.");
        }
        RuntimeNodeIdentity.ValidateUniqueKeys(selected.Select(item => item.Key));
        if (selected.Length > node.MaximumItems)
            throw new FuwenZhinuExecutionException("Persisted grouped selection exceeds its bound.");
        foreach (var item in selected)
        {
            if (item.Token != RuntimeNodeIdentity.GetKeyPathSegment(item.Key) ||
                item.RuntimePath != FuwenZhinuStepKeys.FanOutItem(runtimePath, item.Key))
                throw new FuwenZhinuExecutionException("Persisted grouped selection has inconsistent identity.");
            _ = FuwenRuntimeValueWire.FromJson(item.Value, node.Item.Type, plan.Schemas);
        }
        var prefix = epoch is { } currentEpoch ? FuwenZhinuStepKeys.SelectionPrefix(node.StructuralPath, currentEpoch)
            : FuwenZhinuStepKeys.GroupedFanOutPrefix(node.StructuralPath);

        var manifest = state.Groups!.Manifest;
        var binding = state.Groups.FanOutBinding(manifest, node,
            replacesEpoch == 0 ? null : FuwenZhinuStepKeys.SelectionPrefix(node.StructuralPath, replacesEpoch));
        long outcomeBytes = 2;
        FanOutItemOutcome BudgetOutcome(FanOutItemOutcome outcome)
        {
            var bytes = CanonicalJson.Serialize(outcome).Length + 1L;
            if (Interlocked.Add(ref outcomeBytes, bytes) > MaximumGroupedFanOutVectorBytes)
                throw new FuwenZhinuExecutionException("Grouped fan-out outcomes exceed their 1 MiB vector budget.");
            return outcome;
        }
        var outcomes = await context.FanOutBodyAsync(prefix,
            selected, item => item.Token,
            async (item, scope, token) =>
            {
                var local = CopyItemState(state, FuwenRuntimeValueWire.FromJson(item.Value, node.Item.Type, plan.Schemas),
                    state.Groups.ForFanOut(node, scope), scope, node.StructuralPath, item.Key, runtimePath);
                try
                {
                    await ExecuteGroupedFanOutRegionAsync(node.StructuralPath + "/$body", plan, fingerprint,
                        ports, context, schedule, local, [], token).ConfigureAwait(false);
                    var value = FuwenBindingEvaluator.Evaluate(node.Yield, plan, local);
                    EnsureType(value, ((ListType)node.ResultType).ItemType, plan.Schemas, "grouped fan-out yield");
                    return BudgetOutcome(new FanOutItemOutcome(item.Key, item.RuntimePath, FuwenRuntimeValueWire.ToJson(value), null));
                }
                catch (FuwenZhinuExecutionException exception) when (exception.Failure is { } failure)
                {
                    // Definitive provider failures remain durable envelopes. Native claim,
                    // cancellation and fencing failures cannot be turned into completed work.
                    return BudgetOutcome(new FanOutItemOutcome(item.Key, item.RuntimePath, null, failure));
                }
            }, fingerprint,
            new FanOutOptions { MaxDegreeOfParallelism = Math.Min(node.MaximumConcurrency, ports.ExecutionOptions.MaximumFanOutConcurrency) },
            new StepOptions { FanOutGroup = binding, DependsOn = [binding.SelectionStepKey] }, cancellationToken).ConfigureAwait(false);

        var aggregateInput = FuwenRuntimeValueWire.Serialize(outcomes);
        var aggregate = explicitSelection is not null ? aggregateInput : await context.StepAsync(FuwenZhinuStepKeys.FanOutAggregate(node.StructuralPath), aggregateInput,
            (value, _, _) => Task.FromResult(value),
            GroupOptions(state, context, node.StructuralPath, new StepOptions
            {
                DependsOn = selected.Select(item => FuwenZhinuStepKeys.GroupedFanOutItem(node.StructuralPath, item.Token)).ToArray()
            }, ActivityGroupMemberRole.Control), cancellationToken).ConfigureAwait(false);
        var expected = selected.Select(item => (item.Key,
            Item: FuwenRuntimeValueWire.FromJson(item.Value, node.Item.Type, plan.Schemas), item.RuntimePath)).ToArray();
        var persisted = FuwenFanOutCoordinator.ReadOutcomes(aggregate, expected);
        if (persisted.FirstOrDefault(item => item.Failure is not null)?.Failure is { } failed)
            throw new FuwenZhinuExecutionException(failed);
        return RuntimeValue.FromList(persisted.Select(item =>
            FuwenRuntimeValueWire.FromJson(item.Output!.Value, ((ListType)node.ResultType).ItemType, plan.Schemas)).ToArray());
    }

    private static FuwenInterpreterState CopyItemState(FuwenInterpreterState source, RuntimeValue item,
        FuwenGroupExecution? groups = null, ActivityGroupFanOutItemScope? scope = null,
        string? path = null, RuntimeIdentityKey? key = null, string? runtimePath = null)
    {
        var state = new FuwenInterpreterState(source.Input)
        {
            CurrentItem = item,
            Groups = groups,
            FanOutScope = scope,
            FanOutPath = path,
            FanOutRuntimePath = runtimePath,
            FanOutKey = key,
            TrustedToolSignatures = source.TrustedToolSignatures
        };
        foreach (var value in source.Outputs) state.Outputs.Add(value.Key, value.Value);
        foreach (var value in source.Snapshots) state.Snapshots.Add(value.Key, value.Value);
        return state;
    }

    private static async Task ExecuteGroupedFanOutRegionAsync(string path, WorkflowPlan plan, string fingerprint,
        FuwenZhinuExecutionPorts ports, WorkflowContext context, FuwenExecutionSchedule schedule,
        FuwenInterpreterState state, IReadOnlyCollection<string> controlDependencies, CancellationToken cancellationToken)
    {
        foreach (var phase in schedule.GetPhases(path))
            foreach (var nodePath in phase.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (schedule.GetNode(nodePath))
                {
                    case ActivityNode activity:
                        state.Outputs[nodePath] = await ExecuteActivityAsync(activity, plan, fingerprint, ports,
                            context, state, controlDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case ContextNode contextNode:
                        state.Outputs[nodePath] = await ExecuteContextAsync(contextNode, plan, fingerprint, ports,
                            context, state, controlDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case InferenceNode inference:
                        state.Outputs[nodePath] = await ExecuteInferenceAsync(inference, plan, fingerprint, ports,
                            context, state, controlDependencies, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConditionalNode conditional:
                        var left = FuwenBindingEvaluator.Evaluate(conditional.Condition.Left, plan, state);
                        var right = conditional.Condition.Right is null ? null : FuwenBindingEvaluator.Evaluate(conditional.Condition.Right, plan, state);
                        var selected = FuwenConditionEvaluator.Evaluate(conditional.Condition.Operator, left, right) ? "/$then" : "/$else";
                        await ExecuteGroupedFanOutRegionAsync(conditional.StructuralPath + selected, plan, fingerprint,
                            ports, context, schedule, state,
                            StepOptionsForBindings(controlDependencies, conditional.Condition.Right is { } conditionRight
                                ? [conditional.Condition.Left, conditionRight] : [conditional.Condition.Left])?.DependsOn ?? [],
                            cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new FuwenZhinuAdapterException("Grouped fan-out supports independently durable providers and control-only conditionals.");
                }
            }
    }

    private static int ReadEpoch(Binding binding, WorkflowPlan plan, FuwenInterpreterState state)
    {
        var value = FuwenRuntimeValueWire.ToJson(FuwenBindingEvaluator.Evaluate(binding, plan, state));
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var epoch))
            throw new FuwenZhinuExecutionException("Selection epoch must be a bounded integer.");
        return epoch;
    }

    private static void ValidateEpochs(int epoch, int previous, int maximum)
    {
        if (epoch < 1 || epoch > maximum || previous < 0 || previous >= epoch || (epoch == 1) != (previous == 0))
            throw new FuwenZhinuExecutionException("Selection epochs require initial 1/0, then a fresh bounded epoch naming an earlier epoch.");
    }

    private static GroupedFanOutInput[] BuildSelectedInputs(FanOutNode node, WorkflowPlan plan,
        FuwenInterpreterState state, string runtimePath)
    {
        var source = FuwenBindingEvaluator.Evaluate(node.Source, plan, state);
        var list = FuwenRuntimeValueWire.NormalizeList(source, node.Item.Type, node.MaximumItems, plan.Schemas);
        if (list.Items.Count > node.MaximumItems)
            throw new FuwenZhinuExecutionException("Grouped fan-out source exceeded its declared bound.");
        var inputBuilder = new List<GroupedFanOutInput>();
        long selectionBytes = 2;
        foreach (var item in list.Items)
        {
            var itemState = CopyItemState(state, item);
            var key = FuwenFanOutCoordinator.ToRuntimeKey(FuwenBindingEvaluator.Evaluate(node.Key, plan, itemState));
            var selectedItem = new GroupedFanOutInput(key, RuntimeNodeIdentity.GetKeyPathSegment(key),
                FuwenRuntimeValueWire.ToJson(item), FuwenZhinuStepKeys.FanOutItem(runtimePath, key));
            selectionBytes += CanonicalJson.Serialize(selectedItem).Length + 1L;
            if (selectionBytes > MaximumGroupedFanOutVectorBytes)
                throw new FuwenZhinuExecutionException("Grouped fan-out selection exceeds its 1 MiB vector budget.");
            inputBuilder.Add(selectedItem);
        }
        var inputs = inputBuilder.ToArray();
        RuntimeNodeIdentity.ValidateUniqueKeys(inputs.Select(item => item.Key));
        return inputs;

    }

    private sealed record GroupedSelectionVector(int Epoch, int ReplacesEpoch, GroupedFanOutInput[] Items);
    private sealed record GroupedFanOutInput(RuntimeIdentityKey Key, string Token, JsonElement Value, string RuntimePath);
}
