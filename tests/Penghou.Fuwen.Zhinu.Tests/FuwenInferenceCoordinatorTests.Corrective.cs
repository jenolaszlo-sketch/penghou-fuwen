using System.Text;
using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenInferenceCoordinatorTests
{
    private sealed class ReviewUnmanifestedTurnExecutor : IInferenceTurnExecutor
    {
        public ValueTask<InferenceTurnResult> ExecuteTurnAsync(InferenceTurnRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<InferenceTurnResult>(new InferenceFinalCandidateResult("\"ok\"", ExactUsage()));
    }

    [Fact]
    public async Task Review_Unmanifested_turn_executor_must_not_use_other_executors_preflight()
    {
        var act = async () => await RegisterAsync(CreatePlan(Limits(2, 2)), new ReviewUnmanifestedTurnExecutor(), ct: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>();
    }

    [Fact]
    public async Task Review_Valid_200_byte_tool_call_id_must_be_executable()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult([new InferenceToolCallProposal(new string('c', 200), Search, "{\"q\":1}")], ExactUsage())
                : new InferenceFinalCandidateResult("\"ok\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(CreatePlan(Limits(3, 3, 2), [Search]), turns, tools, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Review_Different_runs_and_inputs_must_have_different_interaction_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) => ValueTask.FromResult<InferenceTurnResult>(new InferenceFinalCandidateResult("\"ok\"", ExactUsage())));
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2)), turns, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            foreach (var input in new[] { "first request", "different request" })
            {
                var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement(input), cancellationToken: ct);
                await engine.ExecuteAsync(run, ct);
                (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Completed);
            }
            turns.ObservedRequests[0].InteractionId.Should().NotBe(turns.ObservedRequests[1].InteractionId);
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_Final_result_must_not_bypass_aggregate_token_limit(bool unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"ok\"", unknown ? null : new InferenceTurnUsage(10, 10, 20));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, totalTokens: 1)), turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            sink.Items.Should().ContainSingle().Subject.Failure!.Code.Should().Be(
                unknown ? ExecutionFailureCode.BudgetUnknown : ExecutionFailureCode.TokenLimitExceeded);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Review_Final_result_must_not_bypass_aggregate_cost_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"ok\"", new InferenceTurnUsage(1, 1, 2, new InferenceCostEvidence("USD", 500, false)));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(CostLimit(1)), turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            sink.Items.Should().ContainSingle().Subject.Failure!.Code.Should().Be(ExecutionFailureCode.CostLimitExceeded);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Review_Currency_mismatch_must_stop_before_tool_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult([new InferenceToolCallProposal("c", Search, "{\"q\":1}")], new InferenceTurnUsage(1, 1, 2, new InferenceCostEvidence("EUR", 1, false)))
                : new InferenceFinalCandidateResult("\"ok\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(CostLimit(100), [Search]), turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            turns.ObservedRequests.Should().ContainSingle();
            sink.Items.Should().ContainSingle().Subject.Failure!.Code.Should().Be(ExecutionFailureCode.BudgetUnknown);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Aggregate_cost_overflow_is_reported_as_unknown_instead_of_wrapping()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")],
                    new InferenceTurnUsage(1, 1, 2, new InferenceCostEvidence("USD", long.MaxValue - 10, false)))
                : new InferenceFinalCandidateResult(
                    "\"done\"",
                    new InferenceTurnUsage(1, 1, 2, new InferenceCostEvidence("USD", 100, false)))));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, toolCalls: 1), [Search]), turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Completed);
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Cost.Should().BeNull();
            evidence.PricingQuality.Should().Be(InferencePricingQuality.Unknown);
        }
        finally { DeleteDirectory(root); }
    }

}
