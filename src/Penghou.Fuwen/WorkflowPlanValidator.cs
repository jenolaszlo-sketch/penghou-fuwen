using System.Text.Json;
using System.Xml;

namespace Penghou.Fuwen;

/// <summary>Rejects malformed or unsupported executable-plan contracts.</summary>
public static class WorkflowPlanValidator
{
    /// <summary>Validates compatibility and the identity-critical plan invariants.</summary>
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
        var nodes = new Dictionary<string, NodeLocation>(StringComparer.Ordinal);
        ValidateNodes(
            plan.Name,
            plan.Nodes,
            plan.Name,
            new HashSet<string>(StringComparer.Ordinal),
            nodes);
        ValidateCatalogueClosure(plan);

        if (string.Equals(plan.IrVersion, FuwenContracts.IrVersionV2, StringComparison.Ordinal))
            ValidateExecutionOrder(plan, nodes);
    }

    /// <summary>
    /// Validates only the versioned serialization and identity contracts that
    /// this library can safely interpret. It does not semantically admit a plan.
    /// </summary>
    internal static void ValidateCompatibility(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var isV1 = string.Equals(plan.IrVersion, FuwenContracts.IrVersionV1, StringComparison.Ordinal);
        var isV2 = string.Equals(plan.IrVersion, FuwenContracts.IrVersionV2, StringComparison.Ordinal);
        if (!isV1 && !isV2)
            throw new NotSupportedException(
                $"Unsupported {nameof(plan.IrVersion)} '{plan.IrVersion}'. Expected '{FuwenContracts.IrVersionV1}' or '{FuwenContracts.IrVersionV2}'.");

        RequireVersion(plan.CanonicalJsonVersion, FuwenContracts.CanonicalJsonVersion, nameof(plan.CanonicalJsonVersion));
        var expectedFingerprint = isV1
            ? FuwenContracts.ExecutionFingerprintVersionV1
            : FuwenContracts.ExecutionFingerprintVersionV2;
        RequireVersion(plan.FingerprintVersion, expectedFingerprint, nameof(plan.FingerprintVersion));
        var expectedCompilerSemantics = isV1
            ? FuwenContracts.CompilerSemanticVersionV1
            : FuwenContracts.CompilerSemanticVersionV2;
        RequireVersion(plan.CompilerSemanticVersion, expectedCompilerSemantics, nameof(plan.CompilerSemanticVersion));
        if (isV1 && plan.ExecutionOrder is not null)
            throw new ArgumentException(
                "IR v1 does not contain an execution order; historical v1 plans are never silently upgraded.",
                nameof(plan.ExecutionOrder));
        if (isV2 && plan.ExecutionOrder is null)
            throw new ArgumentException(
                "IR v2 requires an explicit execution order.",
                nameof(plan.ExecutionOrder));
    }

    private static void ValidateNodes(
        string parentPath,
        IEnumerable<WorkflowNode> nodes,
        string regionPath,
        ISet<string> paths,
        IDictionary<string, NodeLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            StructuralNodeIdentity.ValidateSegment(node.Name);
            RequireText(node.StructuralPath, nameof(node.StructuralPath));
            var expectedPath = $"{parentPath}/{node.Name}";
            if (!string.Equals(node.StructuralPath, expectedPath, StringComparison.Ordinal))
                throw new ArgumentException($"Node path '{node.StructuralPath}' must equal '{expectedPath}'.", nameof(nodes));
            if (!paths.Add(node.StructuralPath))
                throw new ArgumentException($"Duplicate structural node path '{node.StructuralPath}'.", nameof(nodes));
            locations.Add(node.StructuralPath, new NodeLocation(node, regionPath));

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
                    ValidateConditionShape(conditional.Condition);
                    ValidateNodes(
                        $"{conditional.StructuralPath}/$then",
                        conditional.Then,
                        $"{conditional.StructuralPath}/$then",
                        paths,
                        locations);
                    ValidateNodes(
                        $"{conditional.StructuralPath}/$else",
                        conditional.Else,
                        $"{conditional.StructuralPath}/$else",
                        paths,
                        locations);
                    break;
                case ReturnNode:
                    break;
                default:
                    throw new NotSupportedException($"Unsupported workflow node type '{node.GetType().Name}'.");
            }
        }
    }

    private static void ValidateConditionShape(ConditionExpression condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(condition.Left);
        if (!Enum.IsDefined(condition.Operator))
            throw new ArgumentOutOfRangeException(
                nameof(condition),
                condition.Operator,
                "Condition operator is not supported by this IR version.");

        var unary = condition.Operator is ConditionOperator.Not or ConditionOperator.Exists;
        if (unary && condition.Right is not null)
            throw new ArgumentException(
                $"Unary condition operator '{condition.Operator}' cannot have a right operand.",
                nameof(condition));
        if (!unary && condition.Right is null)
            throw new ArgumentException(
                $"Binary condition operator '{condition.Operator}' requires a right operand.",
                nameof(condition));
    }

    private static void ValidateExecutionOrder(
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations)
    {
        var order = plan.ExecutionOrder!;
        ArgumentNullException.ThrowIfNull(order.Regions);
        ValidateExecutionOrderBounds(order);

        var expected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        CollectExpectedRegions(plan.Name, plan.Nodes, expected);

        var seenRegions = new HashSet<string>(StringComparer.Ordinal);
        var phasesByRegion = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var region in order.Regions)
        {
            ArgumentNullException.ThrowIfNull(region);
            RequireText(region.RegionPath, nameof(region.RegionPath));
            if (!expected.TryGetValue(region.RegionPath, out var expectedPaths))
                throw new ArgumentException(
                    $"Execution region '{region.RegionPath}' is unknown or wrongly scoped.",
                    nameof(plan.ExecutionOrder));
            if (!seenRegions.Add(region.RegionPath))
                throw new ArgumentException(
                    $"Duplicate execution region '{region.RegionPath}'.",
                    nameof(plan.ExecutionOrder));

            ArgumentNullException.ThrowIfNull(region.Phases);
            var scheduled = new HashSet<string>(StringComparer.Ordinal);
            var phaseByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var phaseIndex = 0; phaseIndex < region.Phases.Count; phaseIndex++)
            {
                var phase = region.Phases[phaseIndex];
                ArgumentNullException.ThrowIfNull(phase);
                ArgumentNullException.ThrowIfNull(phase.NodePaths);
                if (phase.NodePaths.Count == 0)
                    throw new ArgumentException(
                        $"Execution region '{region.RegionPath}' contains an empty phase.",
                        nameof(plan.ExecutionOrder));

                foreach (var nodePath in phase.NodePaths)
                {
                    RequireText(nodePath, nameof(nodePath));
                    if (!locations.TryGetValue(nodePath, out var location))
                        throw new ArgumentException(
                            $"Execution phase in region '{region.RegionPath}' contains unknown node path '{nodePath}'.",
                            nameof(plan.ExecutionOrder));
                    if (!string.Equals(location.RegionPath, region.RegionPath, StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"Node path '{nodePath}' is wrongly scoped to execution region '{region.RegionPath}'.",
                            nameof(plan.ExecutionOrder));
                    if (!scheduled.Add(nodePath))
                        throw new ArgumentException(
                            $"Node path '{nodePath}' appears more than once in execution region '{region.RegionPath}'.",
                            nameof(plan.ExecutionOrder));
                    phaseByPath.Add(nodePath, phaseIndex);
                }
            }

            var missing = expectedPaths.FirstOrDefault(path => !scheduled.Contains(path));
            if (missing is not null)
                throw new ArgumentException(
                    $"Execution order is missing node path '{missing}' from region '{region.RegionPath}'.",
                    nameof(plan.ExecutionOrder));
            phasesByRegion.Add(region.RegionPath, phaseByPath);
        }

        var missingRegion = expected.Keys.FirstOrDefault(path => !seenRegions.Contains(path));
        if (missingRegion is not null)
            throw new ArgumentException(
                $"Execution order is missing region '{missingRegion}'.",
                nameof(plan.ExecutionOrder));

        ValidateRootReturn(plan, locations, phasesByRegion);
        ValidateBindings(plan, locations, phasesByRegion);
    }

    private static void ValidateExecutionOrderBounds(WorkflowExecutionOrder order)
    {
        if (order.Regions.Count > WorkflowPlanSnapshotLimits.MaximumExecutionRegions)
            throw new ArgumentException(
                "Execution order region count exceeds the bounded limit.",
                nameof(order));

        long phaseCount = 0;
        long entryCount = 0;
        foreach (var region in order.Regions)
        {
            ArgumentNullException.ThrowIfNull(region);
            ArgumentNullException.ThrowIfNull(region.Phases);
            if (region.Phases.Count > WorkflowPlanSnapshotLimits.MaximumExecutionPhases)
                throw new ArgumentException(
                    "Execution order phase count exceeds the bounded limit.",
                    nameof(order));
            phaseCount += region.Phases.Count;
            foreach (var phase in region.Phases)
            {
                ArgumentNullException.ThrowIfNull(phase);
                ArgumentNullException.ThrowIfNull(phase.NodePaths);
                if (phase.NodePaths.Count > WorkflowPlanSnapshotLimits.MaximumExecutionEntries)
                    throw new ArgumentException(
                        "Execution order entry count exceeds the bounded limit.",
                        nameof(order));
                entryCount += phase.NodePaths.Count;
            }
        }

        if (phaseCount > WorkflowPlanSnapshotLimits.MaximumExecutionPhases)
            throw new ArgumentException(
                "Aggregate execution order phase count exceeds the bounded limit.",
                nameof(order));
        if (entryCount > WorkflowPlanSnapshotLimits.MaximumExecutionEntries)
            throw new ArgumentException(
                "Aggregate execution order entry count exceeds the bounded limit.",
                nameof(order));
    }

    private static void CollectExpectedRegions(
        string regionPath,
        IEnumerable<WorkflowNode> nodes,
        IDictionary<string, HashSet<string>> expected)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!expected.TryGetValue(regionPath, out var paths))
        {
            paths = new HashSet<string>(StringComparer.Ordinal);
            expected.Add(regionPath, paths);
        }

        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            paths.Add(node.StructuralPath);
            if (node is not ConditionalNode conditional)
                continue;

            CollectExpectedRegions($"{conditional.StructuralPath}/$then", conditional.Then, expected);
            CollectExpectedRegions($"{conditional.StructuralPath}/$else", conditional.Else, expected);
        }
    }

    private static void ValidateRootReturn(
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> phasesByRegion)
    {
        var rootReturns = locations.Values
            .Where(location => string.Equals(location.RegionPath, plan.Name, StringComparison.Ordinal))
            .Where(location => location.Node is ReturnNode)
            .ToArray();
        if (rootReturns.Length != 1)
            throw new ArgumentException(
                $"IR v2 requires exactly one root return node; found {rootReturns.Length}.",
                nameof(plan.Nodes));

        var branchReturn = locations.Values
            .FirstOrDefault(location => !string.Equals(location.RegionPath, plan.Name, StringComparison.Ordinal) && location.Node is ReturnNode);
        if (branchReturn is not null)
            throw new ArgumentException(
                $"Branch-local return node '{branchReturn.Node.StructuralPath}' is not supported by IR v2.",
                nameof(plan.Nodes));

        if (!phasesByRegion.TryGetValue(plan.Name, out var rootPhases) || rootPhases.Count == 0)
            throw new ArgumentException("IR v2 requires a non-empty root execution order.", nameof(plan.ExecutionOrder));

        var rootReturnPath = rootReturns[0].Node.StructuralPath;
        var finalPhase = rootPhases
            .Where(pair => pair.Value == rootPhases.Values.Max())
            .Select(pair => pair.Key)
            .ToArray();
        if (finalPhase.Length != 1 || !string.Equals(finalPhase[0], rootReturnPath, StringComparison.Ordinal))
            throw new ArgumentException(
                "The root return node must be the sole member of the final root execution phase.",
                nameof(plan.ExecutionOrder));
    }

    private static void ValidateBindings(
        WorkflowPlan plan,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> phasesByRegion)
    {
        foreach (var location in locations.Values)
        {
            switch (location.Node)
            {
                case ContextNode context:
                    ValidateArgumentsForExecution(context.Arguments, location, locations, phasesByRegion);
                    break;
                case InferenceNode inference:
                    ValidateArgumentsForExecution(inference.Arguments, location, locations, phasesByRegion);
                    foreach (var snapshot in inference.ContextSnapshots)
                        ValidateNodeOutputBinding(snapshot, location, locations, phasesByRegion);
                    break;
                case ActivityNode activity:
                    ValidateArgumentsForExecution(activity.Arguments, location, locations, phasesByRegion);
                    break;
                case ConditionalNode conditional:
                    ValidateBindingForExecution(conditional.Condition.Left, location, locations, phasesByRegion);
                    if (conditional.Condition.Right is not null)
                        ValidateBindingForExecution(conditional.Condition.Right, location, locations, phasesByRegion);
                    break;
                case ReturnNode @return:
                    ValidateBindingForExecution(@return.Value, location, locations, phasesByRegion);
                    ValidateReturnValue(plan, @return, locations);
                    break;
            }
        }
    }

    private static void ValidateArgumentsForExecution(
        IReadOnlyList<ArgumentBinding> arguments,
        NodeLocation consumer,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> phasesByRegion)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            ValidateBindingForExecution(argument.Value, consumer, locations, phasesByRegion);
        }
    }

    private static void ValidateBindingForExecution(
        Binding binding,
        NodeLocation consumer,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> phasesByRegion)
    {
        ArgumentNullException.ThrowIfNull(binding);
        switch (binding)
        {
            case InputBinding input:
                ValidateProjection(input.Projection);
                break;
            case NodeOutputBinding output:
                ValidateNodeOutputBinding(output, consumer, locations, phasesByRegion);
                break;
            case LiteralBinding:
                break;
            case ListBinding list:
                ArgumentNullException.ThrowIfNull(list.Items);
                foreach (var item in list.Items)
                    ValidateBindingForExecution(item, consumer, locations, phasesByRegion);
                break;
            case ObjectBinding @object:
                ArgumentNullException.ThrowIfNull(@object.Properties);
                foreach (var property in @object.Properties)
                    ValidateBindingForExecution(property.Value, consumer, locations, phasesByRegion);
                break;
            default:
                throw new NotSupportedException($"Unsupported binding type '{binding.GetType().Name}'.");
        }
    }

    private static void ValidateNodeOutputBinding(
        NodeOutputBinding output,
        NodeLocation consumer,
        IReadOnlyDictionary<string, NodeLocation> locations,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> phasesByRegion)
    {
        ArgumentNullException.ThrowIfNull(output);
        RequireText(output.NodePath, nameof(output.NodePath));
        ValidateProjection(output.Projection);
        if (!locations.TryGetValue(output.NodePath, out var source))
            throw new ArgumentException(
                $"Node output binding references unknown node path '{output.NodePath}'.",
                nameof(output));
        if (source.Node is ReturnNode or ConditionalNode)
            throw new ArgumentException(
                $"Node '{output.NodePath}' cannot be used as an output source.",
                nameof(output));
        if (!string.Equals(source.RegionPath, consumer.RegionPath, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Node output binding from '{output.NodePath}' crosses execution regions '{source.RegionPath}' and '{consumer.RegionPath}'.",
                nameof(output));

        var sourcePhase = phasesByRegion[source.RegionPath][source.Node.StructuralPath];
        var consumerPhase = phasesByRegion[consumer.RegionPath][consumer.Node.StructuralPath];
        if (sourcePhase >= consumerPhase)
            throw new ArgumentException(
                $"Node output binding from '{output.NodePath}' must refer to an earlier execution phase than '{consumer.Node.StructuralPath}'.",
                nameof(output));
    }

    private static void ValidateProjection(IReadOnlyList<string> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        foreach (var segment in projection)
            StructuralNodeIdentity.ValidateSegment(segment);
    }

    private static void ValidateReturnValue(
        WorkflowPlan plan,
        ReturnNode returnNode,
        IReadOnlyDictionary<string, NodeLocation> locations)
    {
        if (returnNode.Value is LiteralBinding literal)
        {
            if (!LiteralMatchesType(literal.Value, plan.OutputType))
                throw new ArgumentException(
                    $"Return node '{returnNode.StructuralPath}' has a literal value incompatible with the declared output type.",
                    nameof(returnNode.Value));
            return;
        }

        if (returnNode.Value is not InputBinding and not NodeOutputBinding)
            throw new ArgumentException(
                $"IR v2 does not support return binding shape '{returnNode.Value.GetType().Name}'; use an input or node-output binding.",
                nameof(returnNode.Value));

        var actualType = returnNode.Value switch
        {
            InputBinding input => ResolveProjectionType(plan.InputType, input.Projection, plan.Schemas),
            NodeOutputBinding output when locations.TryGetValue(output.NodePath, out var source) =>
                ResolveProjectionType(GetNodeOutputType(source.Node), output.Projection, plan.Schemas),
            _ => null,
        };
        if (actualType is null)
            throw new ArgumentException(
                $"IR v2 cannot resolve the return binding shape for '{returnNode.StructuralPath}'.",
                nameof(returnNode.Value));
        if (!TypesEquivalent(actualType, plan.OutputType))
            throw new ArgumentException(
                $"Return node '{returnNode.StructuralPath}' produces '{DescribeType(actualType)}', but the declared output type is '{DescribeType(plan.OutputType)}'.",
                nameof(returnNode.Value));
    }

    private static FuwenType? GetNodeOutputType(WorkflowNode node) => node switch
    {
        ContextNode context => context.OutputType,
        InferenceNode inference => inference.OutputType,
        ActivityNode activity => activity.OutputType,
        _ => null,
    };

    private static FuwenType? ResolveProjectionType(
        FuwenType? type,
        IReadOnlyList<string> projection,
        IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        if (type is null)
            return null;
        ValidateProjection(projection);
        foreach (var segment in projection)
        {
            if (type is not NamedTypeReference named)
                return null;
            var definition = schemas.FirstOrDefault(schema => schema.Descriptor == named.Schema);
            if (definition is not ObjectSchemaDefinition @object)
                return null;
            var field = @object.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.Ordinal));
            if (field is null)
                return null;
            type = field.Type;
        }
        return type;
    }

    private static bool LiteralMatchesType(JsonElement value, FuwenType type)
    {
        if (value.ValueKind is JsonValueKind.Undefined)
            return false;
        if (type is OptionalType optional)
            return value.ValueKind is JsonValueKind.Null || LiteralMatchesType(value, optional.ValueType);
        if (type is PrimitiveType primitive)
        {
            return primitive.Primitive switch
            {
                FuwenPrimitiveKind.String => value.ValueKind is JsonValueKind.String,
                FuwenPrimitiveKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                FuwenPrimitiveKind.Integer => value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out _),
                FuwenPrimitiveKind.Number => value.ValueKind is JsonValueKind.Number,
                FuwenPrimitiveKind.Duration => value.ValueKind is JsonValueKind.String && IsDuration(value),
                FuwenPrimitiveKind.Json => true,
                _ => false,
            };
        }
        return false;
    }

    private static bool IsDuration(JsonElement value)
    {
        try
        {
            _ = XmlConvert.ToTimeSpan(value.GetString()!);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TypesEquivalent(FuwenType left, FuwenType right)
    {
        return (left, right) switch
        {
            (PrimitiveType first, PrimitiveType second) => first.Primitive == second.Primitive,
            (NamedTypeReference first, NamedTypeReference second) => first.Schema == second.Schema,
            (OptionalType first, OptionalType second) => TypesEquivalent(first.ValueType, second.ValueType),
            (ListType first, ListType second) => first.MaxItems == second.MaxItems && TypesEquivalent(first.ItemType, second.ItemType),
            (ArtifactType first, ArtifactType second) => first.ArtifactDescriptor == second.ArtifactDescriptor,
            _ => false,
        };
    }

    private static string DescribeType(FuwenType type) => type switch
    {
        PrimitiveType primitive => primitive.Primitive.ToString(),
        NamedTypeReference named => $"{named.Schema.Kind}:{named.Schema.Name}@{named.Schema.Version}",
        OptionalType optional => $"optional<{DescribeType(optional.ValueType)}>",
        ListType list => $"list<{DescribeType(list.ItemType)}>",
        ArtifactType artifact => $"artifact<{artifact.ArtifactDescriptor.Name}@{artifact.ArtifactDescriptor.Version}>",
        _ => type.GetType().Name,
    };

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
        var identities = new HashSet<(DescriptorKind Kind, string Name, string Version)>();
        foreach (var descriptor in descriptors)
        {
            ValidateDescriptor(descriptor);
            var identity = (descriptor.Kind, descriptor.Name, descriptor.Version);
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
        var identities = new HashSet<(string Name, string? ScopeClass)>();
        foreach (var requirement in manifest.Requirements)
        {
            ArgumentNullException.ThrowIfNull(requirement);
            RequireText(requirement.Name, nameof(requirement.Name));
            if (!identities.Add((requirement.Name, requirement.ScopeClass)))
                throw new ArgumentException(
                    $"Duplicate capability requirement '{requirement.Name}|{requirement.ScopeClass}'.",
                    nameof(manifest));
        }
    }

    private static void ValidateType(FuwenType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type)
        {
            case PrimitiveType primitive when Enum.IsDefined(primitive.Primitive):
                break;
            case PrimitiveType:
                throw new ArgumentOutOfRangeException(nameof(type), "Primitive type is not supported.");
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

    private sealed record NodeLocation(WorkflowNode Node, string RegionPath);
}
