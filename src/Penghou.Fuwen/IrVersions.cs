namespace Penghou.Fuwen;

/// <summary>
/// Ordinal and feature-capability checks for versioned Fuwen IR contracts.
/// Centralizes IR feature gating so new versions only extend this file
/// instead of scattered string comparisons (see review R19).
/// </summary>
public static class IrVersions
{
    /// <summary>Ordinal for <c>fuwen-ir/v1</c>.</summary>
    public const int V1 = 1;
    /// <summary>Ordinal for <c>fuwen-ir/v2</c> (execution schedule).</summary>
    public const int V2 = 2;
    /// <summary>Ordinal for <c>fuwen-ir/v3</c> (typed context requirements).</summary>
    public const int V3 = 3;
    /// <summary>Ordinal for <c>fuwen-ir/v4</c> (keyed fan-out).</summary>
    public const int V4 = 4;
    /// <summary>Ordinal for <c>fuwen-ir/v5</c> (value-producing conditionals).</summary>
    public const int V5 = 5;
    /// <summary>Ordinal for <c>fuwen-ir/v6</c> (bounded repeat).</summary>
    public const int V6 = 6;
    /// <summary>Ordinal for <c>fuwen-ir/v7</c> (interaction gates).</summary>
    public const int V7 = 7;
    /// <summary>Ordinal for <c>fuwen-ir/v8</c> (workflow-owned prompts).</summary>
    public const int V8 = 8;

    /// <summary>Maps an IR version string to its ordinal; 0 when unknown.</summary>
    public static int Ordinal(string? irVersion) => irVersion switch
    {
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV1, StringComparison.Ordinal) => V1,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV2, StringComparison.Ordinal) => V2,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV3, StringComparison.Ordinal) => V3,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV4, StringComparison.Ordinal) => V4,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV5, StringComparison.Ordinal) => V5,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV6, StringComparison.Ordinal) => V6,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV7, StringComparison.Ordinal) => V7,
        _ when string.Equals(irVersion, FuwenContracts.IrVersionV8, StringComparison.Ordinal) => V8,
        _ => 0,
    };

    /// <summary>Whether the version is a known IR contract.</summary>
    public static bool IsKnown(string? irVersion) => Ordinal(irVersion) != 0;

    /// <summary>Whether the version is at least the given ordinal (unknown versions return false).</summary>
    public static bool AtLeast(string? irVersion, int minimum) => Ordinal(irVersion) >= minimum && minimum > 0;

    /// <summary>Whether the plan carries an explicit execution order (IR v2+).</summary>
    public static bool SupportsExecutionOrder(string? irVersion) => AtLeast(irVersion, V2);

    /// <summary>Whether inference nodes use typed context requirements (IR v3+).</summary>
    public static bool SupportsTypedContextRequirements(string? irVersion) => AtLeast(irVersion, V3);

    /// <summary>Whether keyed fan-out regions are supported (IR v4+).</summary>
    public static bool SupportsFanOut(string? irVersion) => AtLeast(irVersion, V4);

    /// <summary>Whether value-producing conditional merges are supported (IR v5+).</summary>
    public static bool SupportsConditionalMerge(string? irVersion) => AtLeast(irVersion, V5);

    /// <summary>Whether bounded repeat regions are supported (IR v6+).</summary>
    public static bool SupportsRepeat(string? irVersion) => AtLeast(irVersion, V6);

    /// <summary>Whether checkpoint and wait interaction gates are supported (IR v7+).</summary>
    public static bool SupportsInteractionGates(string? irVersion) => AtLeast(irVersion, V7);

    /// <summary>Whether workflow-owned prompt declarations are supported (IR v8+).</summary>
    public static bool SupportsWorkflowPrompts(string? irVersion) => AtLeast(irVersion, V8);
}
