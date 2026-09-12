using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Identifies one deterministic execution attempt and its idempotency key.</summary>
public sealed class ExecutionInvocation
{
    /// <summary>The canonical operation-key contract.</summary>
    public const string OperationKeyVersion = "fuwen-operation/v1";
    /// <summary>The maximum UTF-8 length of an execution path.</summary>
    public const int MaximumPathUtf8Bytes = StructuralNodeIdentity.MaximumPathUtf8Bytes;
    /// <summary>The maximum UTF-8 length of a step revision.</summary>
    public const int MaximumStepRevisionUtf8Bytes = 512;

    /// <summary>Creates an invocation and derives its stable operation key.</summary>
    [JsonConstructor]
    public ExecutionInvocation(
        string executionFingerprint,
        string structuralPath,
        string runtimePath,
        string stepRevision,
        string effectiveRequestFingerprint)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        ExecutionFingerprint = executionFingerprint;
        StructuralPath = ValidatePath(structuralPath, nameof(structuralPath));
        RuntimePath = ValidatePath(runtimePath, nameof(runtimePath));
        StepRevision = RuntimeValueSnapshot.Text(stepRevision, nameof(stepRevision), MaximumStepRevisionUtf8Bytes);
        EffectiveRequestFingerprint = ValidateFingerprint(effectiveRequestFingerprint, nameof(effectiveRequestFingerprint));
        OperationKey = ComputeOperationKey(
            ExecutionFingerprint,
            StructuralPath,
            RuntimePath,
            StepRevision,
            EffectiveRequestFingerprint);
    }

    /// <summary>Creates an invocation when the host represents the step revision numerically.</summary>
    public ExecutionInvocation(
        string executionFingerprint,
        string structuralPath,
        string runtimePath,
        long stepRevision,
        string effectiveRequestFingerprint)
        : this(executionFingerprint, structuralPath, runtimePath,
            stepRevision < 0 ? throw new ArgumentOutOfRangeException(nameof(stepRevision)) : stepRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            effectiveRequestFingerprint)
    {
    }

    /// <summary>The verified workflow execution fingerprint.</summary>
    public string ExecutionFingerprint { get; }
    /// <summary>The lexical node path from the immutable workflow plan.</summary>
    public string StructuralPath { get; }
    /// <summary>The runtime node path, including any host-owned instance identity.</summary>
    public string RuntimePath { get; }
    /// <summary>The durable step revision selected by the host.</summary>
    public string StepRevision { get; }
    /// <summary>The canonical fingerprint of the effective request/input.</summary>
    public string EffectiveRequestFingerprint { get; }
    /// <summary>The operation key used to distinguish retries from restarts or changed input.</summary>
    public string OperationKey { get; }

    /// <summary>Computes the canonical operation key for the supplied invocation identity.</summary>
    public static string ComputeOperationKey(
        string executionFingerprint,
        string structuralPath,
        string runtimePath,
        string stepRevision,
        string effectiveRequestFingerprint)
    {
        WorkflowPlanIdentity.ValidateExecutionFingerprint(executionFingerprint);
        var canonical = CanonicalJson.Serialize(new
        {
            executionFingerprint,
            structuralPath = ValidatePath(structuralPath, nameof(structuralPath)),
            runtimePath = ValidatePath(runtimePath, nameof(runtimePath)),
            stepRevision = RuntimeValueSnapshot.Text(stepRevision, nameof(stepRevision), MaximumStepRevisionUtf8Bytes),
            effectiveRequestFingerprint = ValidateFingerprint(effectiveRequestFingerprint, nameof(effectiveRequestFingerprint)),
        });
        return $"sha256:{OperationKeyVersion}:{Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}";
    }

    private static string ValidatePath(string path, string parameterName)
    {
        var value = RuntimeValueSnapshot.Text(path, parameterName, MaximumPathUtf8Bytes);
        if (value.Contains('\0'))
            throw new ArgumentException("Execution paths cannot contain null characters.", parameterName);
        return value;
    }

    private static string ValidateFingerprint(string fingerprint, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint, parameterName);
        var separator = fingerprint.StartsWith("sha256:", StringComparison.Ordinal)
            ? fingerprint.IndexOf(':', "sha256:".Length)
            : -1;
        if (!fingerprint.StartsWith("sha256:", StringComparison.Ordinal) || separator < 0 || fingerprint.Length != separator + 65)
            throw new ArgumentException("The fingerprint must use canonical sha256:<contract>:<lowercase-hex> form.", parameterName);
        RuntimeValueSnapshot.Text(fingerprint["sha256:".Length..separator], parameterName, 512);
        foreach (var character in fingerprint.AsSpan(separator + 1))
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                throw new ArgumentException("The fingerprint must use canonical sha256:<contract>:<lowercase-hex> form.", parameterName);
        return fingerprint;
    }
}
