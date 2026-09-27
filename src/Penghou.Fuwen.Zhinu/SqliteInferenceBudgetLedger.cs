using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Durable SQLite implementation of the host inference budget ledger.</summary>
public sealed class SqliteInferenceBudgetLedger : IInferenceBudgetLedger
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Creates or opens a durable ledger database at <paramref name="databasePath"/>.</summary>
    public SqliteInferenceBudgetLedger(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS accounts (interaction_id TEXT PRIMARY KEY, limits_json TEXT NOT NULL, finalized INTEGER NOT NULL DEFAULT 0, currency TEXT NULL, pricing_revision TEXT NULL); CREATE TABLE IF NOT EXISTS operations (interaction_id TEXT NOT NULL, operation_id TEXT NOT NULL, digest TEXT NOT NULL, binding TEXT NOT NULL, prompt_quote INTEGER NULL, completion_quote INTEGER NULL, total_quote INTEGER NULL, cost_quote INTEGER NULL, currency TEXT NULL, pricing_revision TEXT NULL, status INTEGER NOT NULL, prompt_actual INTEGER NULL, completion_actual INTEGER NULL, total_actual INTEGER NULL, cost_actual INTEGER NULL, actual_currency TEXT NULL, actual_revision TEXT NULL, PRIMARY KEY(interaction_id, operation_id), FOREIGN KEY(interaction_id) REFERENCES accounts(interaction_id));";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async ValueTask<InferenceBudgetReservationDecision> ReserveAsync(InferenceBudgetReservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var limitsJson = JsonSerializer.Serialize(request.EffectiveLimits.Limits.Select(x => new LimitDto((int)x.Dimension, x.Maximum)), JsonOptions);
        var account = await ReadAccountAsync(connection, transaction, request.InteractionId, cancellationToken).ConfigureAwait(false);
        if (account is not null && account.LimitsJson != limitsJson)
            throw new InvalidOperationException("An interaction cannot change its effective limits.");
        var existing = await ReadOperationAsync(connection, transaction, request.InteractionId, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSame(existing, request);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return InferenceBudgetReservationDecision.Existing;
        }

        if (account is not null)
        {
            if (account.Finalized) throw new InvalidOperationException("The inference budget account is finalized.");
        }
        var quote = request.Quote;
        var currency = quote.MaximumCost?.CurrencyCode;
        var revision = quote.MaximumCost?.PricingRevision;
        if (account is not null && (account.Currency != currency || account.Revision != revision))
            throw new InvalidOperationException("All operations in an interaction must use the same quoted currency and pricing revision.");
        if (account is null)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO accounts(interaction_id,limits_json,currency,pricing_revision) VALUES($i,$l,$c,$r)", cancellationToken,
                ("$i", request.InteractionId), ("$l", limitsJson), ("$c", currency), ("$r", revision)).ConfigureAwait(false);
        }
        var rows = await ReadOperationsAsync(connection, transaction, request.InteractionId, cancellationToken).ConfigureAwait(false);
        if (!Fits(request.EffectiveLimits, rows.Append(new Operation(request.OperationId, request.RequestDigest, quote.BindingRevision, quote.MaximumPromptTokens, quote.MaximumCompletionTokens, quote.MaximumTotalTokens, quote.MaximumCost?.AmountMicrounits, currency, revision, 0))))
        {
            if (account is null) await ExecuteAsync(connection, transaction, "DELETE FROM accounts WHERE interaction_id=$i", cancellationToken, ("$i", request.InteractionId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return InferenceBudgetReservationDecision.ExceedsCeiling;
        }
        await ExecuteAsync(connection, transaction, "INSERT INTO operations(interaction_id,operation_id,digest,binding,prompt_quote,completion_quote,total_quote,cost_quote,currency,pricing_revision,status) VALUES($i,$o,$d,$b,$p,$c,$t,$m,$u,$r,0)", cancellationToken,
            ("$i", request.InteractionId), ("$o", request.OperationId), ("$d", request.RequestDigest), ("$b", quote.BindingRevision), ("$p", quote.MaximumPromptTokens), ("$c", quote.MaximumCompletionTokens), ("$t", quote.MaximumTotalTokens), ("$m", quote.MaximumCost?.AmountMicrounits), ("$u", currency), ("$r", revision)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return InferenceBudgetReservationDecision.Reserved;
    }

    /// <inheritdoc />
    public async ValueTask SettleAsync(string interactionId, string operationId, InferenceTurnUsage actual, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actual);
        await MutateAsync(interactionId, operationId, async (connection, transaction, operation) =>
        {
            if (operation.Status == 2)
            {
                var repeatedCost = actual.Cost;
                if (operation.PromptActual != actual.PromptTokens || operation.CompletionActual != actual.CompletionTokens || operation.TotalActual != actual.TotalTokens || operation.CostActual != repeatedCost?.AmountMicrounits || operation.ActualCurrency != repeatedCost?.CurrencyCode || operation.ActualRevision != repeatedCost?.PricingRevision)
                    throw new InvalidOperationException("A settled operation cannot be replayed with different usage.");
                return;
            }
            if (operation.Status == 3) throw new InvalidOperationException("A proven-unused operation cannot be settled.");
            var cost = actual.Cost;
            if (cost is not null && (operation.Currency is null || operation.Revision is null || cost.CurrencyCode != operation.Currency || cost.PricingRevision != operation.Revision || cost.IsEstimated))
                throw new InvalidOperationException("Actual cost currency or pricing revision does not match its reservation.");
            CheckActual("prompt tokens", actual.PromptTokens, operation.PromptQuote);
            CheckActual("completion tokens", actual.CompletionTokens, operation.CompletionQuote);
            CheckActual("total tokens", actual.TotalTokens, operation.TotalQuote);
            CheckActual("cost microunits", cost?.AmountMicrounits, operation.CostQuote);
            await ExecuteAsync(connection, transaction, "UPDATE operations SET status=2,prompt_actual=$p,completion_actual=$c,total_actual=$t,cost_actual=$m,actual_currency=$u,actual_revision=$r WHERE interaction_id=$i AND operation_id=$o", cancellationToken,
                ("$p", actual.PromptTokens), ("$c", actual.CompletionTokens), ("$t", actual.TotalTokens), ("$m", cost?.AmountMicrounits), ("$u", cost?.CurrencyCode), ("$r", cost?.PricingRevision), ("$i", interactionId), ("$o", operationId)).ConfigureAwait(false);
            var account = await ReadAccountAsync(connection, transaction, interactionId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Budget account was not found.");
            var operations = await ReadOperationsAsync(connection, transaction, interactionId, cancellationToken).ConfigureAwait(false);
            if (!Fits(account.LimitsJson, operations)) throw new InvalidOperationException("Settled usage exceeds the interaction budget ceiling.");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask RetainUncertainAsync(string interactionId, string operationId, CancellationToken cancellationToken = default) =>
        MutateAsync(interactionId, operationId, async (connection, transaction, operation) =>
        {
            if (operation.Status is 2 or 3) return;
            await ExecuteAsync(connection, transaction, "UPDATE operations SET status=1 WHERE interaction_id=$i AND operation_id=$o", cancellationToken, ("$i", interactionId), ("$o", operationId)).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask ReleaseProvenUnusedAsync(string interactionId, string operationId, CancellationToken cancellationToken = default) =>
        MutateAsync(interactionId, operationId, async (connection, transaction, operation) =>
        {
            if (operation.Status == 3) return;
            if (operation.Status != 0) throw new InvalidOperationException("Only a pending reservation can be released as unused.");
            await ExecuteAsync(connection, transaction, "UPDATE operations SET status=3 WHERE interaction_id=$i AND operation_id=$o", cancellationToken, ("$i", interactionId), ("$o", operationId)).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask FinalizeAsync(string interactionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var account = await ReadAccountAsync(connection, transaction, interactionId, cancellationToken).ConfigureAwait(false);
        if (account is not null && !account.Finalized)
        {
            var operations = await ReadOperationsAsync(connection, transaction, interactionId, cancellationToken).ConfigureAwait(false);
            if (operations.Any(x => x.Status == 0)) throw new InvalidOperationException("Cannot finalize while a reservation is pending.");
            await ExecuteAsync(connection, transaction, "UPDATE accounts SET finalized=1 WHERE interaction_id=$i", cancellationToken, ("$i", interactionId)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MutateAsync(string interactionId, string operationId, Func<SqliteConnection, SqliteTransaction, Operation, Task> action, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId); ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var operation = await ReadOperationAsync(connection, transaction, interactionId, operationId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Budget operation was not found.");
        await action(connection, transaction, operation).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static void EnsureSame(Operation old, InferenceBudgetReservationRequest request)
    {
        var quote = request.Quote;
        if (old.Digest != request.RequestDigest || old.Binding != quote.BindingRevision || old.PromptQuote != quote.MaximumPromptTokens || old.CompletionQuote != quote.MaximumCompletionTokens || old.TotalQuote != quote.MaximumTotalTokens || old.CostQuote != quote.MaximumCost?.AmountMicrounits || old.Currency != quote.MaximumCost?.CurrencyCode || old.Revision != quote.MaximumCost?.PricingRevision)
            throw new InvalidOperationException("The operation ID is already bound to a different request or quote.");
    }

    private static void CheckActual(string name, long? actual, long? maximum)
    { if (actual is long value && maximum is long bound && value > bound) throw new InvalidOperationException($"Actual {name} exceed the reserved maximum."); }

    private static bool Fits(InferenceLimitSet limits, IEnumerable<Operation> operations) => Fits(limits.Limits.ToDictionary(x => x.Dimension, x => x.Maximum), operations);
    private static bool Fits(string limitsJson, IEnumerable<Operation> operations) => Fits(JsonSerializer.Deserialize<LimitDto[]>(limitsJson, JsonOptions)!.ToDictionary(x => (InferenceLimitDimension)x.Dimension, x => x.Maximum), operations);
    private static bool Fits(Dictionary<InferenceLimitDimension, long> limits, IEnumerable<Operation> operations)
    {
        try
        {
            foreach (var pair in limits)
            {
                if (pair.Key is not (InferenceLimitDimension.PromptTokens or InferenceLimitDimension.CompletionTokens or InferenceLimitDimension.TotalTokens or InferenceLimitDimension.CostMicrounits))
                    continue;
                long total = 0;
                foreach (var op in operations)
                {
                    var value = op.Status == 2 ? op.Actual(pair.Key) ?? op.Quote(pair.Key) : op.Status == 3 ? 0 : op.Quote(pair.Key);
                    if (op.Status != 3 && value is null) return false;
                    total = checked(total + value.GetValueOrDefault());
                }
                if (total > pair.Value) return false;
            }
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token) { var c = new SqliteConnection(_connectionString); await c.OpenAsync(token).ConfigureAwait(false); await using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;"; await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false); return c; }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction t, string sql, CancellationToken token, params (string Name, object? Value)[] args)
    { await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql; foreach (var a in args) cmd.Parameters.AddWithValue(a.Name, a.Value ?? DBNull.Value); await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    private static async Task<Account?> ReadAccountAsync(SqliteConnection c, SqliteTransaction t, string id, CancellationToken token)
    { await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT limits_json,finalized,currency,pricing_revision FROM accounts WHERE interaction_id=$i"; cmd.Parameters.AddWithValue("$i", id); await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); return await r.ReadAsync(token).ConfigureAwait(false) ? new(r.GetString(0), r.GetInt64(1) != 0, Text(r, 2), Text(r, 3)) : null; }
    private static async Task<Operation?> ReadOperationAsync(SqliteConnection c, SqliteTransaction t, string i, string o, CancellationToken token)
    { await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT operation_id,digest,binding,prompt_quote,completion_quote,total_quote,cost_quote,currency,pricing_revision,status,prompt_actual,completion_actual,total_actual,cost_actual,actual_currency,actual_revision FROM operations WHERE interaction_id=$i AND operation_id=$o"; cmd.Parameters.AddWithValue("$i", i); cmd.Parameters.AddWithValue("$o", o); await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); return await r.ReadAsync(token).ConfigureAwait(false) ? ReadOperation(r) : null; }
    private static async Task<List<Operation>> ReadOperationsAsync(SqliteConnection c, SqliteTransaction t, string i, CancellationToken token)
    { var list = new List<Operation>(); await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT operation_id,digest,binding,prompt_quote,completion_quote,total_quote,cost_quote,currency,pricing_revision,status,prompt_actual,completion_actual,total_actual,cost_actual,actual_currency,actual_revision FROM operations WHERE interaction_id=$i"; cmd.Parameters.AddWithValue("$i", i); await using var r = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false); while (await r.ReadAsync(token).ConfigureAwait(false)) list.Add(ReadOperation(r)); return list; }
    private static Operation ReadOperation(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), Long(r, 3), Long(r, 4), Long(r, 5), Long(r, 6), Text(r, 7), Text(r, 8), (int)r.GetInt64(9), Long(r, 10), Long(r, 11), Long(r, 12), Long(r, 13), Text(r, 14), Text(r, 15));
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i); private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private sealed record LimitDto(int Dimension, long Maximum);
    private sealed record Account(string LimitsJson, bool Finalized, string? Currency, string? Revision);
    private sealed record Operation(string Id, string Digest, string Binding, long? PromptQuote, long? CompletionQuote, long? TotalQuote, long? CostQuote, string? Currency, string? Revision, int Status, long? PromptActual = null, long? CompletionActual = null, long? TotalActual = null, long? CostActual = null, string? ActualCurrency = null, string? ActualRevision = null)
    {
        public long? Quote(InferenceLimitDimension d) => d switch { InferenceLimitDimension.PromptTokens => PromptQuote, InferenceLimitDimension.CompletionTokens => CompletionQuote, InferenceLimitDimension.TotalTokens => TotalQuote, InferenceLimitDimension.CostMicrounits => CostQuote, _ => null };
        public long? Actual(InferenceLimitDimension d) => d switch { InferenceLimitDimension.PromptTokens => PromptActual, InferenceLimitDimension.CompletionTokens => CompletionActual, InferenceLimitDimension.TotalTokens => TotalActual, InferenceLimitDimension.CostMicrounits => CostActual, _ => null };
    }
}
