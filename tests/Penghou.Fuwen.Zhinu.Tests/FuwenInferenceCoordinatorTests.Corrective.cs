using System.Text;
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
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

    [Fact]
    public async Task Host_only_cost_ceiling_without_currency_fails_admission()
    {
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"ok\"", ExactUsage());
        var hostCeiling = new InferenceLimitSet([
            new InferenceLimit(InferenceLimitDimension.CostMicrounits, 100),
        ]);
        var act = async () => await RegisterAsync(CreatePlan(Limits(2, 2)), turns,
            hostCeilings: hostCeiling, ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*host cost ceiling without an admitted source currency*");
        turns.ObservedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Stable_pricing_revision_is_preserved_in_aggregate_cost_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"done\"",
            new InferenceTurnUsage(1, 1, 2,
                new InferenceCostEvidence("USD", 25, true, "pricing/1")));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(CostLimit(100)), turns,
            evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Completed);
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Cost!.AmountMicrounits.Should().Be(25);
            evidence.Cost.PricingRevision.Should().Be("pricing/1");
            evidence.PricingQuality.Should().Be(InferencePricingQuality.Estimated);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Mixed_pricing_revisions_cannot_form_one_admitted_cost_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) =>
            ValueTask.FromResult<InferenceTurnResult>(request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")],
                    new InferenceTurnUsage(1, 1, 2,
                        new InferenceCostEvidence("USD", 10, true, "pricing/1")))
                : new InferenceFinalCandidateResult("\"done\"",
                    new InferenceTurnUsage(1, 1, 2,
                        new InferenceCostEvidence("USD", 10, true, "pricing/2")))));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(CostLimit(100), [Search]), turns, tools,
            evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            turns.ObservedRequests.Should().HaveCount(2);
            turns.ObservedRequests[0].RemainingLimits.GetMaximum(InferenceLimitDimension.CostMicrounits).Should().Be(100);
            turns.ObservedRequests[1].RemainingLimits.GetMaximum(InferenceLimitDimension.CostMicrounits).Should().Be(90);
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure!.Code.Should().Be(ExecutionFailureCode.BudgetUnknown);
            evidence.Cost.Should().BeNull();
            evidence.PricingQuality.Should().Be(InferencePricingQuality.Unknown);
        }
        finally { DeleteDirectory(root); }
    }

    private static CallableContract StrictReviewToolContract() => new(
        new CallableSignature([new CallableParameter("q", Text)], Text),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);

    [Fact]
    public async Task Admitted_tool_signature_accepts_matching_arguments_and_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) =>
            ValueTask.FromResult<InferenceTurnResult>(request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":\"ok\"}")], ExactUsage())
                : new InferenceFinalCandidateResult("\"done\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("\"result\""));
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, 1), [Search]), turns, tools,
            ct: ct, toolContract: StrictReviewToolContract());
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("done");
            tools.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"q\":1}")]
    [InlineData("{\"q\":\"ok\",\"extra\":1}")]
    [InlineData("{\"q\":\"first\",\"q\":\"second\"}")]
    public async Task Admitted_tool_signature_rejects_invalid_arguments_before_tool_io(string arguments)
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.ToolCalls(new InferenceToolCallProposal("call-1", Search, arguments));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("\"ok\""));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, 1), [Search]), turns, tools,
            evidenceSink: sink, ct: ct, toolContract: StrictReviewToolContract());
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
            sink.Items.Should().ContainSingle().Subject.Failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Admitted_tool_signature_rejects_wrong_result_before_storage()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":\"ok\"}"));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"wrong\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, 1), [Search]), turns, tools,
            evidenceSink: sink, ct: ct, toolContract: StrictReviewToolContract());
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().ContainSingle();
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure!.Code.Should().Be(ExecutionFailureCode.SchemaMismatch);
            evidence.ProtectedPayloads.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

}
