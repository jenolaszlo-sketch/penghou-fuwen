using System.Text.Json;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Evaluates admitted bindings against one isolated interpreter state.</summary>
internal static class FuwenBindingEvaluator
{
    internal static IReadOnlyList<RuntimeArgument> EvaluateArguments(
        IReadOnlyList<ArgumentBinding> bindings,
        WorkflowPlan plan,
        FuwenInterpreterState state) => bindings
        .Select(binding => new RuntimeArgument(
            binding.Name,
            Evaluate(binding.Value, plan, state)))
        .ToArray();

    internal static RuntimeValue Evaluate(Binding binding, WorkflowPlan plan, FuwenInterpreterState state) => binding switch
    {
        InputBinding input => Project(state.Input, input.Projection),
        FanOutItemValueBinding item when state.CurrentItem is not null => Project(state.CurrentItem, item.Projection),
        FanOutItemValueBinding => throw new FuwenZhinuExecutionException("A fan-out item binding was evaluated outside an item body."),
        LoopStateBinding loop when state.CurrentLoopState is not null => Project(state.CurrentLoopState, loop.Projection),
        LoopStateBinding => throw new FuwenZhinuExecutionException("A loop state binding was evaluated outside a repeat body."),
        LoopIterationBinding iter when state.CurrentLoopIteration is not null => Project(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(state.CurrentLoopIteration.Value)), iter.Projection),
        LoopIterationBinding => throw new FuwenZhinuExecutionException("A loop iteration binding was evaluated outside a repeat body."),
        NodeOutputBinding output when state.Outputs.TryGetValue(output.NodePath, out var value) => Project(value, output.Projection),
        NodeOutputBinding output => throw new FuwenZhinuExecutionException($"Binding refers to unavailable node '{output.NodePath}'."),
        LiteralBinding literal => RuntimeValueJson.Normalize(literal.Value, new PrimitiveType(FuwenPrimitiveKind.Json), plan.Schemas),
        ListBinding list => RuntimeValue.FromList(list.Items.Select(item => Evaluate(item, plan, state)).ToArray()),
        ObjectBinding @object => RuntimeValue.FromObject(@object.Properties.ToDictionary(
            property => property.Key,
            property => Evaluate(property.Value, plan, state),
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
}

internal sealed class FuwenInterpreterState(RuntimeValue input)
{
    internal RuntimeValue Input { get; } = input;
    internal Dictionary<string, RuntimeValue> Outputs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ContextSnapshotReference> Snapshots { get; } = new(StringComparer.Ordinal);
    internal RuntimeValue? CurrentItem { get; set; }
    internal RuntimeValue? CurrentLoopState { get; set; }
    internal int? CurrentLoopIteration { get; set; }
}
