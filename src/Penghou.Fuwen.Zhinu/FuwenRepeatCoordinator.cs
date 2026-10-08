using System.Text.Json;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Owns bounded durable-loop coordination and repeat step naming.</summary>
internal static class FuwenRepeatCoordinator
{
    internal static async Task<JsonElement> ExecuteAsync(
        WorkflowContext context,
        RepeatNode node,
        JsonElement initialState,
        Func<WorkflowLoopIteration<JsonElement>, CancellationToken, Task<LoopBodyOutcome<JsonElement>>> executeIteration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.LoopAsync(
                node.Name,
                initialState,
                _ => true,
                executeIteration,
                new LoopOptions(node.MaxIterations),
                cancellationToken).ConfigureAwait(false);
        }
        catch (LoopLimitExceededException)
        {
            throw new FuwenZhinuExecutionException(new ExecutionFailure(
                ExecutionFailureKind.Contract,
                ExecutionFailureCode.LoopLimitExceeded,
                $"Repeat '{node.StructuralPath}' exceeded its maximum of {node.MaxIterations} iterations without break.",
                providerCode: "LoopLimitExceeded"));
        }
    }

    internal static string StepSuffix(string nodePath, string repeatPath) =>
        FuwenZhinuStepKeys.RepeatBodyStepName(nodePath, repeatPath);
}
