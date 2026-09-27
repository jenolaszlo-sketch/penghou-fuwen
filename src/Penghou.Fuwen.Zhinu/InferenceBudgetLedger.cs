namespace Penghou.Fuwen.Zhinu;

/// <summary>Result of an atomic durable reservation by logical operation identity.</summary>
public enum InferenceBudgetReservationDecision
{
    /// <summary>The operation was reserved for the first time and may be submitted once.</summary>
    Reserved,
    /// <summary>The operation already has a reservation or settlement; never submit it again.</summary>
    Existing,
    /// <summary>The conservative maximum cannot fit the account ceiling.</summary>
    ExceedsCeiling,
}

/// <summary>Immutable data that a host ledger must atomically bind to an operation reservation.</summary>
public sealed class InferenceBudgetReservationRequest
{
    /// <summary>Creates a detached logical reservation request.</summary>
    public InferenceBudgetReservationRequest(
        string interactionId, string operationId, string requestDigest,
        InferenceLimitSet effectiveLimits, InferenceTurnBudgetQuote quote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDigest);
        InteractionId = interactionId;
        OperationId = operationId;
        RequestDigest = requestDigest;
        EffectiveLimits = effectiveLimits ?? throw new ArgumentNullException(nameof(effectiveLimits));
        Quote = quote ?? throw new ArgumentNullException(nameof(quote));
    }

    /// <summary>Logical inference account identity.</summary>
    public string InteractionId { get; }
    /// <summary>Stable logical provider operation identity.</summary>
    public string OperationId { get; }
    /// <summary>Canonical request SHA-256 digest used to reject conflicting operation reuse.</summary>
    public string RequestDigest { get; }
    /// <summary>Source/host minimums enforced by the ledger.</summary>
    public InferenceLimitSet EffectiveLimits { get; }
    /// <summary>Selected-executor maximum for the entire logical operation.</summary>
    public InferenceTurnBudgetQuote Quote { get; }
}

/// <summary>
/// Host-owned durable, atomic leaf-account ledger. Reservations are keyed by
/// logical operation, survive process loss, and fence duplicate requests.
/// Conflicting reuse of an operation ID must throw rather than return Existing.
/// A pending/uncertain reservation continues consuming its full quote until
/// settlement or proof of non-submission. Implementations must compare the
/// pinned currency, pricing revision, and checked aggregate account totals.
/// </summary>
public interface IInferenceBudgetLedger
{
    /// <summary>Atomically reserve the entire maximum charge before any paid work.</summary>
    ValueTask<InferenceBudgetReservationDecision> ReserveAsync(
        InferenceBudgetReservationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Idempotently settle known usage once; retain the maximum for unknown dimensions.</summary>
    ValueTask SettleAsync(
        string interactionId, string operationId, InferenceTurnUsage actual,
        CancellationToken cancellationToken = default);

    /// <summary>Persist an ambiguous commitment without releasing its maximum reservation.</summary>
    ValueTask RetainUncertainAsync(
        string interactionId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Release only when non-submission is durably proven by the owning runtime.</summary>
    ValueTask ReleaseProvenUnusedAsync(
        string interactionId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Close the account after all reservations are settled or retained uncertain.</summary>
    ValueTask FinalizeAsync(string interactionId, CancellationToken cancellationToken = default);
}
