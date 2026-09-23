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
        ArgumentNullException.ThrowIfNull(plan);
        var canonicalBytes = GetCanonicalBytes(plan);
        return ComputeExecutionFingerprint(canonicalBytes, plan.FingerprintVersion);
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
        return ComputeExecutionFingerprint(canonicalBytes, FuwenContracts.ExecutionFingerprintVersion);
    }

    internal static string ComputeExecutionFingerprint(
        ReadOnlySpan<byte> canonicalBytes,
        string fingerprintVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprintVersion);
        if (!string.Equals(fingerprintVersion, FuwenContracts.ExecutionFingerprintVersion, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported fingerprint version '{fingerprintVersion}'.");
        var hash = SHA256.HashData(canonicalBytes);
        return $"sha256:{fingerprintVersion}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>Rejects malformed or non-canonical execution-fingerprint strings.</summary>
    public static void ValidateExecutionFingerprint(string executionFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionFingerprint);
        if (!executionFingerprint.StartsWith(ExecutionFingerprintPrefix, StringComparison.Ordinal) ||
            executionFingerprint.Length != ExecutionFingerprintPrefix.Length + 64)
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
        ExecutionOrder = plan.ExecutionOrder is null ? null : NormalizeExecutionOrder(plan.ExecutionOrder),
        Prompts = plan.Prompts is null
            ? null
            : plan.Prompts
                .Select(prompt => new PromptDefinition(
                    prompt.Name,
                    prompt.Parameters.ToArray(),
                    prompt.Messages.Select(message => new PromptMessage(message.Role, message.Template)).ToArray(),
                    prompt.RegisteredSource))
                .OrderBy(prompt => prompt.Name, StringComparer.Ordinal)
                .ToArray(),
    };

    private static WorkflowExecutionOrder NormalizeExecutionOrder(WorkflowExecutionOrder order) => new(
        order.Regions
            .Select(region => new WorkflowExecutionRegion(
                region.RegionPath,
                region.Phases
                    .Select(phase => new WorkflowExecutionPhase(
                        phase.NodePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray()))
                    .ToArray()))
            .OrderBy(region => region.RegionPath, StringComparer.Ordinal)
            .ToArray());

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
        InferenceNode inference => inference with
        {
            Arguments = NormalizeArguments(inference.Arguments),
            ContextRequirements = inference.ContextRequirements is null
                ? null
                : inference.ContextRequirements
                    .OrderBy(requirement => requirement.Name, StringComparer.Ordinal)
                    .ThenBy(requirement => requirement.Source.NodePath, StringComparer.Ordinal)
                    .ToArray(),
            PromptBindings = inference.PromptBindings is null
                ? null
                : inference.PromptBindings
                    .Select(binding => new PromptBinding(
                        binding.ParameterName,
                        NormalizeBinding(binding.Value)))
                    .OrderBy(binding => binding.ParameterName, StringComparer.Ordinal)
                    .ToArray(),
            Tools = inference.Tools is null
                ? null
                : inference.Tools
                    .OrderBy(DescriptorSortKey, StringComparer.Ordinal)
                    .ToArray(),
        },
        ActivityNode activity => activity with { Arguments = NormalizeArguments(activity.Arguments) },
        ConditionalNode conditional => conditional with
        {
            Then = NormalizeNodes(conditional.Then),
            Else = NormalizeNodes(conditional.Else),
            Merge = conditional.Merge is null
                ? null
                : new ConditionalMerge(
                    NormalizeBinding(conditional.Merge.ThenValue),
                    NormalizeBinding(conditional.Merge.ElseValue),
                    NormalizeFuwenType(conditional.Merge.ResultType)),
        },
        ReturnNode @return => @return,
        FanOutNode fanOut => fanOut with
        {
            Body = NormalizeNodes(fanOut.Body),
        },
        RepeatNode repeat => repeat with
        {
            Body = NormalizeNodes(repeat.Body),
            InitialState = NormalizeBinding(repeat.InitialState),
            ContinueWith = NormalizeBinding(repeat.ContinueWith),
            BreakWhen = NormalizeCondition(repeat.BreakWhen),
        },
        CheckpointNode checkpoint => checkpoint with
        {
            Value = NormalizeBinding(checkpoint.Value),
        },
        WaitNode wait => wait,
        _ => throw new NotSupportedException($"Unsupported workflow node type '{node.GetType().Name}'."),
    };

    private static ConditionExpression NormalizeCondition(ConditionExpression condition) => new(
        condition.Operator,
        NormalizeBinding(condition.Left),
        condition.Right is null ? null : NormalizeBinding(condition.Right));

    private static ArgumentBinding[] NormalizeArguments(IEnumerable<ArgumentBinding> arguments) => arguments
        .OrderBy(argument => argument.Name, StringComparer.Ordinal)
        .ToArray();

    private static Binding NormalizeBinding(Binding binding) => binding switch
    {
        InputBinding value => new InputBinding(value.Projection.ToArray()),
        NodeOutputBinding value => new NodeOutputBinding(value.NodePath, value.Projection.ToArray()),
        FanOutItemValueBinding value => new FanOutItemValueBinding(value.Projection.ToArray()),
        LiteralBinding value => new LiteralBinding(value.Value.Clone()),
        ListBinding value => new ListBinding(value.Items.Select(NormalizeBinding).ToArray()),
        ObjectBinding value => new ObjectBinding(value.Properties.ToDictionary(pair => pair.Key, pair => NormalizeBinding(pair.Value), StringComparer.Ordinal)),
        _ => binding,
    };

    private static FuwenType NormalizeFuwenType(FuwenType type) => type switch
    {
        PrimitiveType _ => type,
        NamedTypeReference _ => type,
        OptionalType value => new OptionalType(NormalizeFuwenType(value.ValueType)),
        ListType value => new ListType(NormalizeFuwenType(value.ItemType), value.MaxItems),
        ArtifactType _ => type,
        _ => type,
    };

    private static string DescriptorSortKey(DescriptorReference descriptor) =>
        $"{descriptor.Kind:D}|{descriptor.Name}|{descriptor.Version}|{descriptor.ContentDigest.Algorithm}|{descriptor.ContentDigest.Contract}|{descriptor.ContentDigest.Value}";
}
