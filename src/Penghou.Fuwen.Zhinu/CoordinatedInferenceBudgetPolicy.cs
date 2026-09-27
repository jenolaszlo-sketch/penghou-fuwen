namespace Penghou.Fuwen.Zhinu;

/// <summary>Shared admission and execution gate for ceilings this runtime cannot reserve.</summary>
internal static class CoordinatedInferenceBudgetPolicy
{
    internal static bool RequiresUnsupportedHardCeiling(InferenceNode node, InferenceLimitSet? hostCeilings)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Protocol is null)
            return false;
        return hostCeilings?.GetMaximum(InferenceLimitDimension.PromptTokens) is not null ||
            hostCeilings?.GetMaximum(InferenceLimitDimension.TotalTokens) is not null ||
            hostCeilings?.GetMaximum(InferenceLimitDimension.CostMicrounits) is not null ||
            (node.Protocol.BudgetEnforcement == InferenceBudgetEnforcement.Strict &&
             (node.Protocol.Limits.MaxPromptTokens is not null ||
              node.Protocol.Limits.MaxTotalTokens is not null ||
              node.Protocol.Limits.Cost is not null));
    }
}
