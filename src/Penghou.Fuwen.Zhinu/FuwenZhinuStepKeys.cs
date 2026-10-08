using System.Security.Cryptography;
using System.Text;

namespace Penghou.Fuwen.Zhinu;

/// <summary>
/// The single source of truth for every durable step key the Fuwen to Zhinu
/// port can emit. Execution, the plan-scoped step map, and step-key matching
/// all build keys through these helpers so they cannot drift. Consumers never
/// reconstruct a key; they match persisted keys through
/// <see cref="FuwenZhinuStepMapper"/>.
/// </summary>
/// <remarks>
/// Invariant: <c>Penghou.Fuwen.Zhinu</c> intentionally owns the Fuwen-to-Zhinu
/// adapter key contract. Where Zhinu durable-loop key helpers are internal,
/// the exactness tests in <c>FuwenZhinuStepMapperTests</c> run real
/// <c>WorkflowEngine</c> executions and assert every persisted
/// <c>WorkflowStepRun.StepKey</c> matches the public map. Any future Zhinu
/// key-convention drift must fail those tests rather than silently altering the
/// public mapper's behavior.
/// </remarks>
internal static class FuwenZhinuStepKeys
{
    internal const string LoopSegment = "loop";
    internal const string BodySegment = "body";
    internal const string ItemSegment = "$item";
    internal const string ConditionSegment = "condition";
    internal const string CommitSegment = "commit";
    internal const string LimitsSegment = "limits";
    internal const string LimitSegment = "limit";
    internal const string BodyMarker = "/$body/";
    internal const string MergeSuffix = "/$merge";
    internal const string FallbackSuffix = "/$fallback";

    /// <summary>A declared node's own step key is its structural path.</summary>
    internal static string Node(string structuralPath) => structuralPath;

    /// <summary>The synthetic conditional-merge step key at the top level.</summary>
    internal static string ConditionalMerge(string structuralPath) => structuralPath + MergeSuffix;

    /// <summary>The synthetic inference-fallback step key.</summary>
    internal static string InferenceFallback(string structuralPath) => structuralPath + FallbackSuffix;

    /// <summary>The fan-out aggregate step key.</summary>
    internal static string FanOutAggregate(string structuralPath) => structuralPath;

    /// <summary>The durable fan-out item step key (covers the whole item body).</summary>
    internal static string FanOutItem(string structuralPath, RuntimeIdentityKey key) =>
        RuntimeNodeIdentity.CreateFanOutItem(structuralPath, key);

    /// <summary>The repeat-body step name passed to the loop iteration (port-owned suffix).</summary>
    internal static string RepeatBodyStepName(string nodePath, string repeatPath)
    {
        var marker = nodePath.IndexOf(BodyMarker, StringComparison.Ordinal);
        var suffix = marker >= 0 ? nodePath[(marker + BodyMarker.Length)..] : nodePath;
        if (suffix.StartsWith(repeatPath + "/", StringComparison.Ordinal))
        {
            suffix = suffix[(repeatPath.Length + 1)..];
        }

        return suffix.Replace("/", "-", StringComparison.Ordinal).Replace("$", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The repeat-nested conditional-merge body step name.</summary>
    internal static string RepeatMergeStepName(string conditionalPath, string repeatPath) =>
        RepeatBodyStepName(conditionalPath, repeatPath) + "-merge";

    // Loop envelope (Zhinu-owned format, encoded here so the port can match it).

    internal static string RootLoopPath(string loopName) => $"$loop/{loopName}";

    internal static string NestedLoopPath(string parentLoopPath, int parentIteration, string loopName) =>
        $"{parentLoopPath}/{parentIteration}/{LoopSegment}/{loopName}";

    internal static string LoopBody(string loopPath, int iteration, string stepName) =>
        $"{loopPath}/{iteration}/{BodySegment}/{stepName}";

    internal static string LoopCondition(string loopPath, int iteration) =>
        $"{loopPath}/{iteration}/{ConditionSegment}";

    internal static string LoopCommit(string loopPath, int iteration) =>
        $"{loopPath}/{iteration}/{CommitSegment}";

    internal static string LoopLimits(string loopPath) => $"{loopPath}/{LimitsSegment}";

    internal static string LoopLimit(string loopPath) => $"{loopPath}/{LimitSegment}";

    // Inference protocol loop.

    internal static string ProtocolLoopName(string structuralPath) =>
        $"infer-protocol-{SanitizeStepSegment(structuralPath)}-{ShortHash(structuralPath)}";

    internal static string ProtocolModelTurnStepName(int ordinal) => $"infer-model-turn-{ordinal:0000}";

    internal static string ProtocolReadToolStepName(int ordinal, string callId) =>
        $"infer-read-tool-{ordinal:0000}-{SanitizeStepSegment(callId)}";

    internal static string SanitizeStepSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '-');
        }

        var sanitized = builder.ToString().Trim('-');
        return sanitized.Length == 0 ? "call" : (sanitized.Length > 64 ? sanitized[..64] : sanitized);
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
}
