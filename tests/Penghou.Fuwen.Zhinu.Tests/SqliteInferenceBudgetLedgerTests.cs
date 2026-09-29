using FluentAssertions;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed class SqliteInferenceBudgetLedgerTests
{
    private static readonly InferenceLimitSet Limits = new([
        new InferenceLimit(InferenceLimitDimension.PromptTokens, 20),
        new InferenceLimit(InferenceLimitDimension.TotalTokens, 30),
        new InferenceLimit(InferenceLimitDimension.CostMicrounits, 100),
    ]);

    [Fact]
    public async Task Reservation_survives_reopen_and_settlement_releases_only_known_unused_allowance()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "budget.db");
            var first = new SqliteInferenceBudgetLedger(path);
            var one = Request("op/1", "digest/1", prompt: 10, total: 15, cost: 60);
            (await first.ReserveAsync(one, TestContext.Current.CancellationToken)).Should().Be(InferenceBudgetReservationDecision.Reserved);

            var recovered = new SqliteInferenceBudgetLedger(path);
            (await recovered.ReserveAsync(one, TestContext.Current.CancellationToken)).Should().Be(InferenceBudgetReservationDecision.Existing);
            var conflict = Request("op/1", "different-digest", prompt: 10, total: 15, cost: 60);
            var conflictAct = async () => await recovered.ReserveAsync(conflict, TestContext.Current.CancellationToken);
            await conflictAct.Should().ThrowAsync<InvalidOperationException>();

            (await recovered.ReserveAsync(Request("op/2", "digest/2", prompt: 12, total: 20, cost: 50), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.ExceedsCeiling);
            await recovered.SettleAsync("interaction/1", "op/1", Usage(5, 8, 20), TestContext.Current.CancellationToken);
            (await recovered.ReserveAsync(Request("op/2", "digest/2", prompt: 12, total: 20, cost: 50), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.Reserved);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Uncertain_or_unknown_usage_keeps_the_full_quote_and_conflicting_settlement_fails()
    {
        var root = NewRoot();
        try
        {
            var ledger = new SqliteInferenceBudgetLedger(Path.Combine(root, "budget.db"));
            var one = Request("op/1", "digest/1", prompt: 15, total: 20, cost: 70);
            (await ledger.ReserveAsync(one, TestContext.Current.CancellationToken)).Should().Be(InferenceBudgetReservationDecision.Reserved);
            await ledger.RetainUncertainAsync("interaction/1", "op/1", TestContext.Current.CancellationToken);
            (await ledger.ReserveAsync(Request("op/2", "digest/2", prompt: 10, total: 15, cost: 40), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.ExceedsCeiling);
            var releaseAct = async () => await ledger.ReleaseProvenUnusedAsync("interaction/1", "op/1", TestContext.Current.CancellationToken);
            await releaseAct.Should().ThrowAsync<InvalidOperationException>();

            await ledger.SettleAsync("interaction/1", "op/1", new InferenceTurnUsage(), TestContext.Current.CancellationToken);
            (await ledger.ReserveAsync(Request("op/2", "digest/2", prompt: 10, total: 15, cost: 40), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.ExceedsCeiling);
            await ledger.SettleAsync("interaction/1", "op/1", new InferenceTurnUsage(), TestContext.Current.CancellationToken);
            var conflicting = async () => await ledger.SettleAsync("interaction/1", "op/1", Usage(5, 8, 20), TestContext.Current.CancellationToken);
            await conflicting.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task First_priced_quote_sets_currency_after_unpriced_reservations()
    {
        var root = NewRoot();
        try
        {
            var ledger = new SqliteInferenceBudgetLedger(Path.Combine(root, "budget.db"));
            var limits = new InferenceLimitSet([new InferenceLimit(InferenceLimitDimension.TotalTokens, 30)]);
            InferenceBudgetReservationRequest RequestWithCost(string operation, string? currency, string? revision) => new(
                "mixed-quotes", operation, operation, limits,
                new InferenceTurnBudgetQuote("binding/1", 3, 2, 5,
                    currency is null ? null : new InferenceCostEvidence(currency, 10, false, revision!)));

            var ct = TestContext.Current.CancellationToken;
            (await ledger.ReserveAsync(RequestWithCost("unpriced", null, null), ct)).Should().Be(InferenceBudgetReservationDecision.Reserved);
            (await ledger.ReserveAsync(RequestWithCost("priced", "USD", "pricing/1"), ct)).Should().Be(InferenceBudgetReservationDecision.Reserved);
            (await ledger.ReserveAsync(RequestWithCost("unpriced-again", null, null), ct)).Should().Be(InferenceBudgetReservationDecision.Reserved);
            var changed = async () => await ledger.ReserveAsync(RequestWithCost("changed", "USD", "pricing/2"), ct);
            await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*currency and pricing revision*");
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Pricing_revision_and_actual_over_quote_fail_without_releasing_reservation()
    {
        var root = NewRoot();
        try
        {
            var ledger = new SqliteInferenceBudgetLedger(Path.Combine(root, "budget.db"));
            (await ledger.ReserveAsync(Request("op/1", "digest/1", prompt: 10, total: 15, cost: 60), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.Reserved);
            var changedPrice = new InferenceBudgetReservationRequest(
                "interaction/1", "op/2", "digest/2", Limits,
                new InferenceTurnBudgetQuote("binding/1", 5, 3, 8,
                    new InferenceCostEvidence("USD", 30, false, "pricing/2")));
            var changedAct = async () => await ledger.ReserveAsync(changedPrice, TestContext.Current.CancellationToken);
            await changedAct.Should().ThrowAsync<InvalidOperationException>();

            var overQuote = async () => await ledger.SettleAsync("interaction/1", "op/1", Usage(5, 8, 61), TestContext.Current.CancellationToken);
            await overQuote.Should().ThrowAsync<InvalidOperationException>();
            (await ledger.ReserveAsync(Request("op/2", "digest/2", prompt: 12, total: 20, cost: 50), TestContext.Current.CancellationToken))
                .Should().Be(InferenceBudgetReservationDecision.ExceedsCeiling);
            await ledger.RetainUncertainAsync("interaction/1", "op/1", TestContext.Current.CancellationToken);
            await ledger.FinalizeAsync("interaction/1", TestContext.Current.CancellationToken);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Concurrent_reservations_share_one_durable_ceiling_across_connections()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "budget.db");
            var limits = new InferenceLimitSet([new InferenceLimit(InferenceLimitDimension.TotalTokens, 10)]);
            var left = new SqliteInferenceBudgetLedger(path);
            var right = new SqliteInferenceBudgetLedger(path);
            var requests = new[]
            {
                new InferenceBudgetReservationRequest("race", "left", "digest-left", limits,
                    new InferenceTurnBudgetQuote("binding", 5, 3, 8)),
                new InferenceBudgetReservationRequest("race", "right", "digest-right", limits,
                    new InferenceTurnBudgetQuote("binding", 5, 3, 8)),
            };
            var results = await Task.WhenAll(left.ReserveAsync(requests[0], TestContext.Current.CancellationToken).AsTask(), right.ReserveAsync(requests[1], TestContext.Current.CancellationToken).AsTask());
            results.Count(x => x == InferenceBudgetReservationDecision.Reserved).Should().Be(1);
            results.Count(x => x == InferenceBudgetReservationDecision.ExceedsCeiling).Should().Be(1);

            var reopened = new SqliteInferenceBudgetLedger(path);
            var retry = await reopened.ReserveAsync(requests[0], TestContext.Current.CancellationToken);
            var other = await reopened.ReserveAsync(requests[1], TestContext.Current.CancellationToken);
            new[] { retry, other }.Should().Contain(InferenceBudgetReservationDecision.Existing);
            new[] { retry, other }.Should().Contain(InferenceBudgetReservationDecision.ExceedsCeiling);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Non_billable_limits_do_not_need_to_be_present_in_turn_quotes_and_finalize_missing_account_is_safe()
    {
        var root = NewRoot();
        try
        {
            var ledger = new SqliteInferenceBudgetLedger(Path.Combine(root, "budget.db"));
            var limits = new InferenceLimitSet([
                new InferenceLimit(InferenceLimitDimension.Turns, 4),
                new InferenceLimit(InferenceLimitDimension.DurationMilliseconds, 1_000),
                new InferenceLimit(InferenceLimitDimension.TotalTokens, 10),
            ]);
            var request = new InferenceBudgetReservationRequest("interaction", "op", "digest", limits,
                new InferenceTurnBudgetQuote("binding", 5, 2, 8));
            (await ledger.ReserveAsync(request, TestContext.Current.CancellationToken)).Should().Be(InferenceBudgetReservationDecision.Reserved);
            var changedLimits = new InferenceBudgetReservationRequest("interaction", "op", "digest", Limits, request.Quote);
            var changed = async () => await ledger.ReserveAsync(changedLimits, TestContext.Current.CancellationToken);
            await changed.Should().ThrowAsync<InvalidOperationException>();
            await ledger.FinalizeAsync("absent-account", TestContext.Current.CancellationToken);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Store_identity_survives_reopen_and_stale_ledger_fails_after_database_replacement()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "budget.db");
            var original = new SqliteInferenceBudgetLedger(path);
            var identity = original.StoreIdentity;
            Guid.TryParseExact(identity, "D", out _).Should().BeTrue();
            var reopened = new SqliteInferenceBudgetLedger(path);
            reopened.StoreIdentity.Should().Be(identity);

            File.Delete(path);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = path + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            var replacement = new SqliteInferenceBudgetLedger(path);
            replacement.StoreIdentity.Should().NotBe(identity);
            var staleUse = async () => await original.ReserveAsync(
                Request("stale-op", "stale-digest", prompt: 5, total: 8, cost: 10), TestContext.Current.CancellationToken);
            await staleUse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*backing store was replaced*");
        }
        finally { DeleteRoot(root); }
    }

    private static InferenceBudgetReservationRequest Request(
        string operationId, string digest, long prompt, long total, long cost) => new(
        "interaction/1", operationId, digest, Limits,
        new InferenceTurnBudgetQuote("binding/1", prompt, 3, total,
            new InferenceCostEvidence("USD", cost, false, "pricing/1")));

    private static InferenceTurnUsage Usage(int prompt, int total, long cost) => new(
        prompt, 3, total, new InferenceCostEvidence("USD", cost, false, "pricing/1"));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuwen-budget-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
