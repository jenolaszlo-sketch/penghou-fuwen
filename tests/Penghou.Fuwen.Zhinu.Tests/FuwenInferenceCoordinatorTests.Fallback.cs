using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenInferenceCoordinatorTests
{
    private static WorkflowPlan WithFallback(
        WorkflowPlan plan, ExecutionFailureCode code, string value = "fallback")
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return plan with
        {
            IrVersion = FuwenContracts.InferenceFallbackIrVersion,
            Nodes = plan.Nodes.Select(node => node is InferenceNode inference
                ? inference with
                {
                    FailureFallback = new InferenceFailureFallback(
                        [code], new LiteralBinding(document.RootElement.Clone())),
                }
                : node).ToArray(),
        };
    }

    [Fact]
    public async Task Exhausted_turn_bound_uses_a_durable_typed_fallback_without_running_tool()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = WithFallback(CreatePlan(Limits(turns: 1, modelCalls: 1, toolCalls: 1), [Search]),
            ExecutionFailureCode.TurnLimitExceeded);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
            ValueTask.FromResult<InferenceTurnResult>(new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("fallback");
            turns.ObservedRequests.Should().ContainSingle();
            tools.ObservedRequests.Should().BeEmpty();
            sink.Items.Should().ContainSingle().Which.Failure!.Code.Should().Be(ExecutionFailureCode.TurnLimitExceeded);
            (await engine.GetStepsAsync(run, ct)).Should().Contain(step =>
                step.StepKey == "coord/infer/$fallback" && step.Status == StepStatus.Completed);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Fallback_does_not_absorb_a_possibly_committed_model_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = WithFallback(CreatePlan(Limits(turns: 1, modelCalls: 1)),
            ExecutionFailureCode.MalformedOutput);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
            throw new InferenceTurnFailureException(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.MalformedOutput,
                "Provider response was malformed after uncertain submission.",
                mayHaveCommittedEffect: true)));
        var registration = await RegisterAsync(plan, turns, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            turns.ObservedRequests.Should().ContainSingle();
            (await engine.GetStepsAsync(run, ct)).Should().NotContain(step =>
                step.StepKey == "coord/infer/$fallback");
        }
        finally { DeleteDirectory(root); }
    }
}
