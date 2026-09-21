using System.Text.Json;

namespace Penghou.Fuwen.Zhinu;

/// <summary>Owns bounded fan-out scheduling and persisted item outcome validation.</summary>
internal static class FuwenFanOutCoordinator
{
    internal static async Task<TResult[]> ExecuteBoundedAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maximumConcurrency,
        Func<TItem, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(execute);
        if (maximumConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

        using var gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await execute(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    internal static RuntimeIdentityKey ToRuntimeKey(RuntimeValue value)
    {
        var json = RuntimeValueJson.ToJsonElement(value);
        return json.ValueKind switch
        {
            JsonValueKind.String => new StringRuntimeKey(json.GetString()!),
            JsonValueKind.Number when json.TryGetInt64(out var integer) => new IntegerRuntimeKey(integer),
            JsonValueKind.Null => throw new FuwenZhinuExecutionException("Fan-out keys cannot be null."),
            _ => throw new FuwenZhinuExecutionException("Fan-out keys must be strings or signed integers."),
        };
    }

    internal static FanOutItemOutcome ReadOutcome(JsonElement value, RuntimeIdentityKey expectedKey, string runtimePath)
    {
        var outcome = CanonicalJson.Deserialize<FanOutItemOutcome>(CanonicalJson.Canonicalize(value))
            ?? throw new FuwenZhinuExecutionException($"Persisted fan-out item '{runtimePath}' is malformed.");
        if (!string.Equals(outcome.RuntimePath, runtimePath, StringComparison.Ordinal) || !Equals(outcome.Key, expectedKey) ||
            (outcome.Output is null) == (outcome.Failure is null))
            throw new FuwenZhinuExecutionException($"Persisted fan-out item '{runtimePath}' has invalid identity or outcome evidence.");
        return outcome;
    }

    internal static IReadOnlyList<FanOutItemOutcome> ReadOutcomes(
        JsonElement value,
        IReadOnlyList<(RuntimeIdentityKey Key, RuntimeValue Item, string RuntimePath)> expected)
    {
        var outcomes = CanonicalJson.Deserialize<FanOutItemOutcome[]>(CanonicalJson.Canonicalize(value))
            ?? throw new FuwenZhinuExecutionException("Persisted fan-out aggregate is malformed.");
        if (outcomes.Length != expected.Count)
            throw new FuwenZhinuExecutionException("Persisted fan-out aggregate item count does not match its source collection.");
        for (var index = 0; index < outcomes.Length; index++)
        {
            var outcome = outcomes[index];
            if (outcome is null || !string.Equals(outcome.RuntimePath, expected[index].RuntimePath, StringComparison.Ordinal) ||
                !Equals(outcome.Key, expected[index].Key) || (outcome.Output is null) == (outcome.Failure is null))
                throw new FuwenZhinuExecutionException("Persisted fan-out aggregate item identity or outcome is invalid.");
        }
        RuntimeNodeIdentity.ValidateUniqueKeys(outcomes.Select(static outcome => outcome.Key));
        return outcomes;
    }
}

internal sealed record FanOutItemOutcome(
    RuntimeIdentityKey Key,
    string RuntimePath,
    JsonElement? Output,
    ExecutionFailure? Failure);
