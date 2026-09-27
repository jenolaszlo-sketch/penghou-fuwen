namespace Penghou.Fuwen;

/// <summary>
/// A conservative upper bound for every billable provider attempt that one
/// normalized turn may cause, including retries, fallbacks, and fees.
/// </summary>
public sealed class InferenceTurnBudgetQuote
{
    /// <summary>Creates an immutable quote tied to one selected runtime binding and pricing revision.</summary>
    public InferenceTurnBudgetQuote(
        string bindingRevision,
        long? maximumPromptTokens,
        long? maximumCompletionTokens,
        long? maximumTotalTokens,
        InferenceCostEvidence? maximumCost = null)
    {
        BindingRevision = RuntimeValueSnapshot.Text(bindingRevision, nameof(bindingRevision), 256);
        if (maximumPromptTokens is < 0 || maximumCompletionTokens is < 0 || maximumTotalTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalTokens));
        if (maximumPromptTokens is long prompt && maximumTotalTokens is long total && prompt > total)
            throw new ArgumentOutOfRangeException(nameof(maximumPromptTokens));
        if (maximumCompletionTokens is long completion && maximumTotalTokens is long totalLimit && completion > totalLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumCompletionTokens));
        if (maximumCost is { IsEstimated: true })
            throw new ArgumentException("A hard maximum charge cannot be estimated.", nameof(maximumCost));
        if (maximumCost is not null && string.IsNullOrWhiteSpace(maximumCost.PricingRevision))
            throw new ArgumentException("A hard maximum charge requires a pinned pricing revision.", nameof(maximumCost));
        MaximumPromptTokens = maximumPromptTokens;
        MaximumCompletionTokens = maximumCompletionTokens;
        MaximumTotalTokens = maximumTotalTokens;
        MaximumCost = maximumCost is null ? null : new InferenceCostEvidence(
            maximumCost.CurrencyCode, maximumCost.AmountMicrounits, false, maximumCost.PricingRevision);
    }

    /// <summary>Exact selected binding and quote-policy revision.</summary>
    public string BindingRevision { get; }
    /// <summary>Maximum billable prompt tokens across every attempt.</summary>
    public long? MaximumPromptTokens { get; }
    /// <summary>Maximum billable completion tokens across every attempt.</summary>
    public long? MaximumCompletionTokens { get; }
    /// <summary>Maximum billable total tokens across every attempt.</summary>
    public long? MaximumTotalTokens { get; }
    /// <summary>Maximum charge including every fee and retry, with pinned currency and pricing revision.</summary>
    public InferenceCostEvidence? MaximumCost { get; }
}

/// <summary>
/// Optional hard-budget capability of the selected turn executor. A quote must
/// perform no paid work and must bound every provider operation caused by the
/// matching ExecuteTurnAsync request. A returned usage estimate is insufficient.
/// </summary>
public interface IInferenceTurnBudgetAuthority
{
    /// <summary>Quotes a conservative, enforceable maximum before submission.</summary>
    ValueTask<InferenceTurnBudgetQuote> QuoteMaximumAsync(
        InferenceTurnRequest request, CancellationToken cancellationToken = default);
}
