using System.Security.Cryptography;

namespace Penghou.Fuwen;

/// <summary>Canonicalizes resolved plans and derives their execution identity.</summary>
public static class WorkflowPlanIdentity
{
    private const string ExecutionFingerprintPrefix = "sha256:fuwen-execution/v1:";

    /// <summary>Produces canonical resolved IR bytes after normalizing unordered collections.</summary>
    public static byte[] GetCanonicalBytes(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return GetCanonicalBytesFrozen(WorkflowPlanSnapshot.Create(plan));
    }

    /// <summary>Computes the self-describing SHA-256 execution fingerprint.</summary>
    public static string ComputeExecutionFingerprint(WorkflowPlan plan)
    {
        var hash = SHA256.HashData(GetCanonicalBytes(plan));
        return $"sha256:{FuwenContracts.ExecutionFingerprintVersion}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    internal static byte[] GetCanonicalBytesFrozen(WorkflowPlan plan)
    {
        WorkflowPlanValidator.Validate(plan);
        return CanonicalJson.Serialize(Normalize(plan));
    }

    internal static byte[] GetCanonicalBytesForVerification(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WorkflowPlanValidator.ValidateCompatibility(plan);
        return CanonicalJson.Serialize(Normalize(plan));
    }

    internal static string ComputeExecutionFingerprint(ReadOnlySpan<byte> canonicalBytes)
    {
        var hash = SHA256.HashData(canonicalBytes);
        return $"sha256:{FuwenContracts.ExecutionFingerprintVersion}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>Rejects malformed or non-canonical execution-fingerprint strings.</summary>
    public static void ValidateExecutionFingerprint(string executionFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionFingerprint);
        if (executionFingerprint.Length != ExecutionFingerprintPrefix.Length + 64 ||
            !executionFingerprint.StartsWith(ExecutionFingerprintPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Execution fingerprint must use canonical sha256:fuwen-execution/v1 lowercase hexadecimal form.", nameof(executionFingerprint));
        foreach (var character in executionFingerprint.AsSpan(ExecutionFingerprintPrefix.Length))
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                throw new ArgumentException("Execution fingerprint must use canonical sha256:fuwen-execution/v1 lowercase hexadecimal form.", nameof(executionFingerprint));
    }

    private static WorkflowPlan Normalize(WorkflowPlan plan) => plan with
    {
        Schemas = plan.Schemas
            .Select(NormalizeSchema)
            .OrderBy(schema => DescriptorSortKey(schema.Descriptor), StringComparer.Ordinal)
            .ToArray(),
        CatalogueBindings = plan.CatalogueBindings.OrderBy(DescriptorSortKey, StringComparer.Ordinal).ToArray(),
        CapabilityManifest = plan.CapabilityManifest with
        {
            Requirements = plan.CapabilityManifest.Requirements
                .OrderBy(requirement => requirement.Name, StringComparer.Ordinal)
                .ThenBy(requirement => requirement.ScopeClass, StringComparer.Ordinal)
                .ToArray(),
        },
        Nodes = NormalizeNodes(plan.Nodes),
    };

    private static ResolvedSchemaDefinition NormalizeSchema(ResolvedSchemaDefinition schema) => schema switch
    {
        ObjectSchemaDefinition @object => @object with
        {
            Fields = @object.Fields.OrderBy(field => field.Name, StringComparer.Ordinal).ToArray(),
        },
        EnumSchemaDefinition @enum => @enum with
        {
            Members = @enum.Members
                .OrderBy(member => member.Value, StringComparer.Ordinal)
                .ThenBy(member => member.Name, StringComparer.Ordinal)
                .ToArray(),
        },
        _ => throw new NotSupportedException($"Unsupported schema definition type '{schema.GetType().Name}'."),
    };

    private static WorkflowNode[] NormalizeNodes(IEnumerable<WorkflowNode> nodes) => nodes
        .Select(NormalizeNode)
        .OrderBy(node => node.StructuralPath, StringComparer.Ordinal)
        .ToArray();

    private static WorkflowNode NormalizeNode(WorkflowNode node) => node switch
    {
        ContextNode context => context with { Arguments = NormalizeArguments(context.Arguments) },
        InferenceNode inference => inference with { Arguments = NormalizeArguments(inference.Arguments) },
        ActivityNode activity => activity with { Arguments = NormalizeArguments(activity.Arguments) },
        ConditionalNode conditional => conditional with
        {
            Then = NormalizeNodes(conditional.Then),
            Else = NormalizeNodes(conditional.Else),
        },
        ReturnNode @return => @return,
        _ => throw new NotSupportedException($"Unsupported workflow node type '{node.GetType().Name}'."),
    };

    private static ArgumentBinding[] NormalizeArguments(IEnumerable<ArgumentBinding> arguments) => arguments
        .OrderBy(argument => argument.Name, StringComparer.Ordinal)
        .ToArray();

    private static string DescriptorSortKey(DescriptorReference descriptor) =>
        $"{descriptor.Kind:D}|{descriptor.Name}|{descriptor.Version}|{descriptor.ContentDigest.Algorithm}|{descriptor.ContentDigest.Contract}|{descriptor.ContentDigest.Value}";
}
