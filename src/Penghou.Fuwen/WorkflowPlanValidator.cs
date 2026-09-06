namespace Penghou.Fuwen;

/// <summary>Rejects malformed or unsupported executable-plan contracts.</summary>
public static class WorkflowPlanValidator
{
    /// <summary>Validates compatibility and the identity-critical v1 invariants.</summary>
    public static void Validate(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateCompatibility(plan);
        StructuralNodeIdentity.ValidateSegment(plan.Name);
        RequireText(plan.LanguageVersion, nameof(plan.LanguageVersion));
        RequireText(plan.CompilerSemanticVersion, nameof(plan.CompilerSemanticVersion));
        RequireText(plan.Revision, nameof(plan.Revision));
        RequireText(plan.RoutingPolicyRevision, nameof(plan.RoutingPolicyRevision));

        ValidateType(plan.InputType);
        ValidateType(plan.OutputType);
        ValidateSchemas(plan.Schemas);
        ValidateDescriptors(plan.CatalogueBindings);
        ValidateCapabilities(plan.CapabilityManifest);
        ValidateNodes(plan.Name, plan.Nodes, new HashSet<string>(StringComparer.Ordinal));
        ValidateCatalogueClosure(plan);
    }

    /// <summary>
    /// Validates only the versioned serialization and identity contracts that
    /// this library can safely interpret. It does not semantically admit a plan.
    /// </summary>
    internal static void ValidateCompatibility(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireVersion(plan.IrVersion, FuwenContracts.IrVersion, nameof(plan.IrVersion));
        RequireVersion(plan.CanonicalJsonVersion, FuwenContracts.CanonicalJsonVersion, nameof(plan.CanonicalJsonVersion));
        RequireVersion(plan.FingerprintVersion, FuwenContracts.ExecutionFingerprintVersion, nameof(plan.FingerprintVersion));
    }

    private static void ValidateNodes(string parentPath, IEnumerable<WorkflowNode> nodes, ISet<string> paths)
    {
        foreach (var node in nodes)
        {
            StructuralNodeIdentity.ValidateSegment(node.Name);
            RequireText(node.StructuralPath, nameof(node.StructuralPath));
            var expectedPath = $"{parentPath}/{node.Name}";
            if (!string.Equals(node.StructuralPath, expectedPath, StringComparison.Ordinal))
                throw new ArgumentException($"Node path '{node.StructuralPath}' must equal '{expectedPath}'.", nameof(nodes));
            if (!paths.Add(node.StructuralPath))
                throw new ArgumentException($"Duplicate structural node path '{node.StructuralPath}'.", nameof(nodes));

            switch (node)
            {
                case ContextNode context:
                    RequireKind(context.Provider, DescriptorKind.ContextProvider);
                    ValidateArguments(context.Arguments);
                    ValidateType(context.OutputType);
                    break;
                case InferenceNode inference:
                    RequireKind(inference.Profile, DescriptorKind.InferenceProfile);
                    RequireKind(inference.PromptTemplate, DescriptorKind.PromptTemplate);
                    ValidateArguments(inference.Arguments);
                    ValidateType(inference.OutputType);
                    break;
                case ActivityNode activity:
                    RequireKind(activity.Activity, DescriptorKind.Activity);
                    ValidateArguments(activity.Arguments);
                    ValidateType(activity.OutputType);
                    break;
                case ConditionalNode conditional:
                    ValidateNodes($"{conditional.StructuralPath}/$then", conditional.Then, paths);
                    ValidateNodes($"{conditional.StructuralPath}/$else", conditional.Else, paths);
                    break;
                case ReturnNode:
                    break;
                default:
                    throw new NotSupportedException($"Unsupported workflow node type '{node.GetType().Name}'.");
            }
        }
    }

    private static void ValidateCatalogueClosure(WorkflowPlan plan)
    {
        var bindings = plan.CatalogueBindings.ToHashSet();
        var referenced = new HashSet<DescriptorReference>();
        CollectTypeDescriptors(plan.InputType, referenced);
        CollectTypeDescriptors(plan.OutputType, referenced);
        CollectNodeDescriptors(plan.Nodes, referenced);
        foreach (var schema in plan.Schemas)
        {
            referenced.Add(schema.Descriptor);
            if (schema is ObjectSchemaDefinition @object)
                foreach (var field in @object.Fields)
                    CollectTypeDescriptors(field.Type, referenced);
        }
        var missing = referenced.FirstOrDefault(reference => !bindings.Contains(reference));
        if (missing is not null)
            throw new ArgumentException(
                $"Resolved descriptor '{missing.Kind}:{missing.Name}@{missing.Version}' is absent or differs from the catalogue binding.",
                nameof(plan));

        var definitions = plan.Schemas.Select(schema => schema.Descriptor).ToHashSet();
        var undefined = referenced.FirstOrDefault(reference => reference.Kind == DescriptorKind.Schema && !definitions.Contains(reference));
        if (undefined is not null)
            throw new ArgumentException(
                $"Schema '{undefined.Name}@{undefined.Version}' is referenced but has no resolved definition.",
                nameof(plan));
    }

    private static void ValidateSchemas(IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        var descriptors = new HashSet<DescriptorReference>();
        foreach (var schema in schemas)
        {
            RequireKind(schema.Descriptor, DescriptorKind.Schema);
            if (!descriptors.Add(schema.Descriptor))
                throw new ArgumentException($"Duplicate resolved schema '{schema.Descriptor.Name}@{schema.Descriptor.Version}'.", nameof(schemas));

            switch (schema)
            {
                case ObjectSchemaDefinition @object:
                    var duplicateField = @object.Fields
                        .GroupBy(field => field.Name, StringComparer.Ordinal)
                        .FirstOrDefault(group => group.Count() > 1);
                    if (duplicateField is not null)
                        throw new ArgumentException($"Duplicate field '{duplicateField.Key}' in schema '{@object.Descriptor.Name}'.", nameof(schemas));
                    foreach (var field in @object.Fields)
                    {
                        StructuralNodeIdentity.ValidateSegment(field.Name);
                        ValidateType(field.Type);
                    }
                    break;
                case EnumSchemaDefinition @enum:
                    if (@enum.Members.Count == 0)
                        throw new ArgumentException($"Enum schema '{@enum.Descriptor.Name}' must declare at least one member.", nameof(schemas));
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in @enum.Members)
                    {
                        StructuralNodeIdentity.ValidateSegment(member.Name);
                        RequireText(member.Value, nameof(member.Value));
                        if (!names.Add(member.Name) || !values.Add(member.Value))
                            throw new ArgumentException($"Enum schema '{@enum.Descriptor.Name}' has duplicate names or values.", nameof(schemas));
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported schema definition type '{schema.GetType().Name}'.");
            }
        }
    }

    private static void CollectNodeDescriptors(IEnumerable<WorkflowNode> nodes, ISet<DescriptorReference> descriptors)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ContextNode context:
                    descriptors.Add(context.Provider);
                    CollectTypeDescriptors(context.OutputType, descriptors);
                    break;
                case InferenceNode inference:
                    descriptors.Add(inference.Profile);
                    descriptors.Add(inference.PromptTemplate);
                    CollectTypeDescriptors(inference.OutputType, descriptors);
                    break;
                case ActivityNode activity:
                    descriptors.Add(activity.Activity);
                    CollectTypeDescriptors(activity.OutputType, descriptors);
                    break;
                case ConditionalNode conditional:
                    CollectNodeDescriptors(conditional.Then, descriptors);
                    CollectNodeDescriptors(conditional.Else, descriptors);
                    break;
            }
        }
    }

    private static void CollectTypeDescriptors(FuwenType type, ISet<DescriptorReference> descriptors)
    {
        switch (type)
        {
            case NamedTypeReference named:
                descriptors.Add(named.Schema);
                break;
            case OptionalType optional:
                CollectTypeDescriptors(optional.ValueType, descriptors);
                break;
            case ListType list:
                CollectTypeDescriptors(list.ItemType, descriptors);
                break;
            case ArtifactType artifact:
                descriptors.Add(artifact.ArtifactDescriptor);
                break;
        }
    }

    private static void ValidateArguments(IReadOnlyList<ArgumentBinding> arguments)
    {
        var duplicate = arguments.GroupBy(argument => argument.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate argument '{duplicate.Key}'.", nameof(arguments));
        foreach (var argument in arguments)
            RequireText(argument.Name, nameof(argument.Name));
    }

    private static void ValidateDescriptors(IEnumerable<DescriptorReference> descriptors)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            ValidateDescriptor(descriptor);
            var identity = $"{descriptor.Kind:D}|{descriptor.Name}|{descriptor.Version}";
            if (!identities.Add(identity))
                throw new ArgumentException($"Duplicate descriptor binding '{descriptor.Kind}:{descriptor.Name}@{descriptor.Version}'.", nameof(descriptors));
        }
    }

    private static void ValidateDescriptor(DescriptorReference descriptor)
    {
        RequireText(descriptor.Name, nameof(descriptor.Name));
        RequireText(descriptor.Version, nameof(descriptor.Version));
        RequireText(descriptor.ContentDigest.Algorithm, nameof(descriptor.ContentDigest.Algorithm));
        RequireText(descriptor.ContentDigest.Contract, nameof(descriptor.ContentDigest.Contract));
        RequireText(descriptor.ContentDigest.Value, nameof(descriptor.ContentDigest.Value));
    }

    private static void ValidateCapabilities(CapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var duplicate = manifest.Requirements
            .GroupBy(requirement => $"{requirement.Name}|{requirement.ScopeClass}", StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate capability requirement '{duplicate.Key}'.", nameof(manifest));
        foreach (var requirement in manifest.Requirements)
            RequireText(requirement.Name, nameof(requirement.Name));
    }

    private static void ValidateType(FuwenType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type)
        {
            case PrimitiveType:
                break;
            case NamedTypeReference named:
                RequireKind(named.Schema, DescriptorKind.Schema);
                break;
            case OptionalType optional:
                ValidateType(optional.ValueType);
                break;
            case ListType list when list.MaxItems <= 0:
                throw new ArgumentOutOfRangeException(nameof(type), "List maximum must be positive.");
            case ListType list:
                ValidateType(list.ItemType);
                break;
            case ArtifactType artifact:
                RequireKind(artifact.ArtifactDescriptor, DescriptorKind.Artifact);
                break;
            default:
                throw new NotSupportedException($"Unsupported Fuwen type '{type.GetType().Name}'.");
        }
    }

    private static void RequireKind(DescriptorReference descriptor, DescriptorKind expected)
    {
        ValidateDescriptor(descriptor);
        if (descriptor.Kind != expected)
            throw new ArgumentException($"Descriptor '{descriptor.Name}' must be of kind '{expected}', not '{descriptor.Kind}'.", nameof(descriptor));
    }

    private static void RequireVersion(string actual, string expected, string parameterName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported {parameterName} '{actual}'. Expected '{expected}'.");
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
    }
}
