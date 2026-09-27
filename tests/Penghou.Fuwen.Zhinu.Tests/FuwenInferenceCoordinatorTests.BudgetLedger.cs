using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenInferenceCoordinatorTests
{
    [Theory]
    [InlineData(InferenceBudgetReservationDecision.Reserved, 1, 1, 0)]
    [InlineData(InferenceBudgetReservationDecision.Existing, 0, 0, 1)]
    [InlineData(InferenceBudgetReservationDecision.ExceedsCeiling, 0, 0, 0)]
    public async Task Budget_reservation_precedes_model_work_and_never_resubmits_existing_operation(
        InferenceBudgetReservationDecision decision, int expectedCalls, int expectedSettlements, int expectedUncertainty)
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = new RecordingBudgetLedger(decision);
        var turns = new QuotedTurnExecutor(
            ledger,
            new InferenceTurnBudgetQuote("binding/1", 5, 3, 8),
            new InferenceFinalCandidateResult("\"ok\"", ExactUsage()));
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, totalTokens: 20)),
            turns, budgetLedger: ledger, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);

            ledger.Reservations.Should().ContainSingle();
            turns.Executions.Should().Be(expectedCalls);
            ledger.Settlements.Should().Be(expectedSettlements);
            ledger.Finalizations.Should().Be(1);
            ((await engine.GetRunAsync(run, ct))!.Status == Penghou.Zhinu.WorkflowStatus.Failed)
                .Should().Be(expectedUncertainty == 1 || decision == InferenceBudgetReservationDecision.ExceedsCeiling);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Quote_larger_than_remaining_budget_stops_before_reservation_and_provider_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = new RecordingBudgetLedger(InferenceBudgetReservationDecision.Reserved);
        var turns = new QuotedTurnExecutor(
            ledger, new InferenceTurnBudgetQuote("binding/1", 5, 3, 21),
            new InferenceFinalCandidateResult("\"ok\"", ExactUsage()));
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, totalTokens: 20)),
            turns, budgetLedger: ledger, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            ledger.Reservations.Should().BeEmpty();
            turns.Executions.Should().Be(0);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(Penghou.Zhinu.WorkflowStatus.Failed);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Usage_exceeding_trusted_quote_retains_uncertain_reservation()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = new RecordingBudgetLedger(InferenceBudgetReservationDecision.Reserved);
        var turns = new QuotedTurnExecutor(
            ledger, new InferenceTurnBudgetQuote("binding/1", 5, 3, 8),
            new InferenceFinalCandidateResult("\"ok\"", new InferenceTurnUsage(5, 3, 10)));
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2, totalTokens: 20)),
            turns, budgetLedger: ledger, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            ledger.Uncertainties.Should().Be(1);
            ledger.Settlements.Should().Be(0);
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(Penghou.Zhinu.WorkflowStatus.Failed);
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class QuotedTurnExecutor : IInferenceTurnExecutor, IInferenceTurnExecutorManifest, IInferenceTurnBudgetAuthority
    {
        private readonly RecordingBudgetLedger ledger;
        private readonly InferenceTurnBudgetQuote quote;
        private readonly DeterministicFakeTurnExecutor inner;

        public QuotedTurnExecutor(RecordingBudgetLedger ledger, InferenceTurnBudgetQuote quote, InferenceTurnResult result)
        {
            this.ledger = ledger;
            this.quote = quote;
            inner = new DeterministicFakeTurnExecutor([result]);
        }

        public int Executions { get; private set; }
        public InferenceFeatureManifest TurnFeatureManifest => inner.TurnFeatureManifest;
        public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement) =>
            inner.PreflightTurnDetailed(requirement);
        public ValueTask<InferenceTurnBudgetQuote> QuoteMaximumAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(quote);
        public ValueTask<InferenceTurnResult> ExecuteTurnAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default)
        {
            ledger.Reservations.Should().ContainSingle("a durable reserve must precede the provider call");
            Executions++;
            return inner.ExecuteTurnAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingBudgetLedger(InferenceBudgetReservationDecision decision) : IInferenceBudgetLedger
    {
        public List<InferenceBudgetReservationRequest> Reservations { get; } = [];
        public int Settlements { get; private set; }
        public int Uncertainties { get; private set; }
        public int Finalizations { get; private set; }

        public ValueTask<InferenceBudgetReservationDecision> ReserveAsync(
            InferenceBudgetReservationRequest request, CancellationToken cancellationToken = default)
        {
            Reservations.Add(request);
            return ValueTask.FromResult(decision);
        }
        public ValueTask SettleAsync(string interactionId, string operationId, InferenceTurnUsage actual,
            CancellationToken cancellationToken = default)
        {
            Settlements++;
            return ValueTask.CompletedTask;
        }
        public ValueTask RetainUncertainAsync(string interactionId, string operationId,
            CancellationToken cancellationToken = default)
        {
            Uncertainties++;
            return ValueTask.CompletedTask;
        }
        public ValueTask ReleaseProvenUnusedAsync(string interactionId, string operationId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(string interactionId, CancellationToken cancellationToken = default)
        {
            Finalizations++;
            return ValueTask.CompletedTask;
        }
    }
}
