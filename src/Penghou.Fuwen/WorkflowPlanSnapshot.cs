using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>Creates a detached, deeply copied snapshot of a caller-owned plan.</summary>
internal static class WorkflowPlanSnapshot
{
    internal static WorkflowPlan Create(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var state = new SnapshotState();
        state.Enter(plan);
        try
        {
            ArgumentNullException.ThrowIfNull(plan.InputType);
            ArgumentNullException.ThrowIfNull(plan.OutputType);
            ArgumentNullException.ThrowIfNull(plan.CapabilityManifest);

            return new WorkflowPlan(
                plan.IrVersion,
                plan.LanguageVersion,
                plan.CompilerSemanticVersion,
                plan.CanonicalJsonVersion,
                plan.FingerprintVersion,
                plan.Name,
                plan.Revision,
                CloneType(plan.InputType, state),
                CloneType(plan.OutputType, state),
                plan.RoutingPolicyRevision,
                SnapshotList(plan.Schemas, "schemas", CloneSchema, state),
                SnapshotList(plan.CatalogueBindings, "catalogue bindings", CloneDescriptor, state),
                CloneCapabilities(plan.CapabilityManifest, state),
                SnapshotList(plan.Nodes, "workflow nodes", CloneNode, state),
                plan.ExecutionOrder is null ? null : CloneExecutionOrder(plan.ExecutionOrder, state));
        }
        finally
        {
            state.Exit(plan);
        }
    }

    private static CapabilityManifest CloneCapabilities(CapabilityManifest manifest, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        state.Enter(manifest);
        try
        {
            return new CapabilityManifest(
                SnapshotList(manifest.Requirements, "capability requirements", CloneCapability, state));
        }
        finally
        {
            state.Exit(manifest);
        }
    }

    private static WorkflowExecutionOrder CloneExecutionOrder(
        WorkflowExecutionOrder order,
        SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(order);
        state.Enter(order);
        try
        {
            state.CheckExecutionOrderBudget(order);
            return new WorkflowExecutionOrder(
                SnapshotList(order.Regions, "execution regions", CloneExecutionRegion, state));
        }
        finally
        {
            state.Exit(order);
        }
    }

    private static WorkflowExecutionRegion CloneExecutionRegion(
        WorkflowExecutionRegion region,
        SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(region);
        state.CountExecutionRegion();
        state.Enter(region);
        try
        {
            return new WorkflowExecutionRegion(
                region.RegionPath,
                SnapshotList(region.Phases, "execution phases", CloneExecutionPhase, state));
        }
        finally
        {
            state.Exit(region);
        }
    }

    private static WorkflowExecutionPhase CloneExecutionPhase(
        WorkflowExecutionPhase phase,
        SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(phase);
        state.CountExecutionPhase();
        state.Enter(phase);
        try
        {
            return new WorkflowExecutionPhase(
                SnapshotList(phase.NodePaths, "execution phase node paths", CloneExecutionNodePath, state));
        }
        finally
        {
            state.Exit(phase);
        }
    }

    private static string CloneExecutionNodePath(string value, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(value);
        state.CountExecutionEntry();
        return value;
    }

    private static ResolvedSchemaDefinition CloneSchema(ResolvedSchemaDefinition schema, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        state.Enter(schema);
        try
        {
            return schema switch
            {
                ObjectSchemaDefinition value => new ObjectSchemaDefinition(
                    CloneDescriptor(value.Descriptor, state),
                    SnapshotList(value.Fields, "schema fields", CloneField, state)),
                EnumSchemaDefinition value => new EnumSchemaDefinition(
                    CloneDescriptor(value.Descriptor, state),
                    SnapshotList(value.Members, "enum members", CloneMember, state)),
                _ => throw new NotSupportedException($"Unsupported schema definition type '{schema.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(schema);
        }
    }

    private static SchemaField CloneField(SchemaField field, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(field);
        state.CountSchemaField();
        state.Enter(field);
        try
        {
            ArgumentNullException.ThrowIfNull(field.Type);
            return new SchemaField(field.Name, CloneType(field.Type, state));
        }
        finally
        {
            state.Exit(field);
        }
    }

    private static EnumMember CloneMember(EnumMember member, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(member);
        state.Enter(member);
        try
        {
            return new EnumMember(member.Name, member.Value);
        }
        finally
        {
            state.Exit(member);
        }
    }

    private static WorkflowNode CloneNode(WorkflowNode node, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(node);
        state.CountNode();
        state.Enter(node);
        try
        {
            return node switch
            {
                ContextNode value => new ContextNode(
                    value.Name,
                    value.StructuralPath,
                    CloneDescriptor(value.Provider, state),
                    SnapshotList(value.Arguments, "context arguments", CloneArgument, state),
                    CloneType(value.OutputType, state)),
                InferenceNode value => new InferenceNode(
                    value.Name,
                    value.StructuralPath,
                    CloneDescriptor(value.Profile, state),
                    CloneDescriptor(value.PromptTemplate, state),
                    SnapshotList(value.Arguments, "inference arguments", CloneArgument, state),
                    SnapshotList(value.ContextSnapshots, "context snapshots", CloneNodeOutput, state),
                    CloneType(value.OutputType, state)),
                ActivityNode value => new ActivityNode(
                    value.Name,
                    value.StructuralPath,
                    CloneDescriptor(value.Activity, state),
                    SnapshotList(value.Arguments, "activity arguments", CloneArgument, state),
                    CloneType(value.OutputType, state)),
                ConditionalNode value => new ConditionalNode(
                    value.Name,
                    value.StructuralPath,
                    CloneCondition(value.Condition, state),
                    SnapshotList(value.Then, "then branch nodes", CloneNode, state),
                    SnapshotList(value.Else, "else branch nodes", CloneNode, state)),
                ReturnNode value => new ReturnNode(value.Name, value.StructuralPath, CloneBinding(value.Value, state)),
                _ => throw new NotSupportedException($"Unsupported workflow node type '{node.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(node);
        }
    }

    private static ConditionExpression CloneCondition(ConditionExpression condition, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(condition);
        state.Enter(condition);
        try
        {
            ArgumentNullException.ThrowIfNull(condition.Left);
            return new(
                condition.Operator,
                CloneBinding(condition.Left, state),
                condition.Right is null ? null : CloneBinding(condition.Right, state));
        }
        finally
        {
            state.Exit(condition);
        }
    }

    private static ArgumentBinding CloneArgument(ArgumentBinding argument, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(argument);
        state.Enter(argument);
        try
        {
            ArgumentNullException.ThrowIfNull(argument.Value);
            return new(argument.Name, CloneBinding(argument.Value, state));
        }
        finally
        {
            state.Exit(argument);
        }
    }

    private static NodeOutputBinding CloneNodeOutput(NodeOutputBinding binding, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(binding);
        state.Enter(binding);
        try
        {
            return new(binding.NodePath, SnapshotProjection(binding.Projection, state));
        }
        finally
        {
            state.Exit(binding);
        }
    }

    private static Binding CloneBinding(Binding binding, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(binding);
        state.CountBinding();
        state.Enter(binding);
        try
        {
            return binding switch
            {
                InputBinding value => new InputBinding(SnapshotProjection(value.Projection, state)),
                NodeOutputBinding value => new NodeOutputBinding(value.NodePath, SnapshotProjection(value.Projection, state)),
                LiteralBinding value when value.Value.ValueKind is not JsonValueKind.Undefined => new LiteralBinding(value.Value.Clone()),
                ListBinding value => new ListBinding(SnapshotList(value.Items, "list literal items", CloneBinding, state)),
                ObjectBinding value => new ObjectBinding(SnapshotProperties(value.Properties, state)),
                _ => throw new NotSupportedException($"Unsupported binding type '{binding.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(binding);
        }
    }

    private static FuwenType CloneType(FuwenType type, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(type);
        state.Enter(type);
        try
        {
            return type switch
            {
                PrimitiveType value when Enum.IsDefined(value.Primitive) => new PrimitiveType(value.Primitive),
                PrimitiveType => throw new ArgumentOutOfRangeException(nameof(type), "Primitive type is not supported."),
                NamedTypeReference value => new NamedTypeReference(CloneDescriptor(value.Schema, state)),
                OptionalType value => new OptionalType(CloneType(value.ValueType, state)),
                ListType value => new ListType(CloneType(value.ItemType, state), value.MaxItems),
                ArtifactType value => new ArtifactType(CloneDescriptor(value.ArtifactDescriptor, state)),
                _ => throw new NotSupportedException($"Unsupported Fuwen type '{type.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(type);
        }
    }

    private static DescriptorReference CloneDescriptor(DescriptorReference descriptor, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        state.Enter(descriptor);
        try
        {
            ArgumentNullException.ThrowIfNull(descriptor.ContentDigest);
            return new(
                descriptor.Kind,
                descriptor.Name,
                descriptor.Version,
                CloneDigest(descriptor.ContentDigest, state));
        }
        finally
        {
            state.Exit(descriptor);
        }
    }

    private static ContentDigest CloneDigest(ContentDigest digest, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(digest);
        state.Enter(digest);
        try
        {
            return new(digest.Algorithm, digest.Contract, digest.Value);
        }
        finally
        {
            state.Exit(digest);
        }
    }

    private static CapabilityRequirement CloneCapability(CapabilityRequirement requirement, SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        state.Enter(requirement);
        try
        {
            return new(requirement.Name, requirement.ScopeClass);
        }
        finally
        {
            state.Exit(requirement);
        }
    }

    private static string[] SnapshotProjection(IReadOnlyList<string> projection, SnapshotState state)
    {
        return SnapshotList(projection, "binding projection", static (value, _) => value, state);
    }

    private static Dictionary<string, Binding> SnapshotProperties(
        IReadOnlyDictionary<string, Binding> properties,
        SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(properties);
        state.CheckCollection(properties.Count, "object literal properties");
        state.Enter(properties);
        try
        {
            var result = new Dictionary<string, Binding>(properties.Count, StringComparer.Ordinal);
            foreach (var pair in properties)
            {
                if (!result.TryAdd(pair.Key, CloneBinding(pair.Value, state)))
                    throw new WorkflowPlanSnapshotException("duplicate object literal property");
            }

            return result;
        }
        finally
        {
            state.Exit(properties);
        }
    }

    private static T[] SnapshotList<T>(
        IReadOnlyList<T> values,
        string name,
        Func<T, SnapshotState, T> clone,
        SnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(values);
        state.CheckCollection(values.Count, name);
        state.Enter(values);
        try
        {
            var result = new T[values.Count];
            for (var index = 0; index < result.Length; index++)
                result[index] = clone(values[index], state);
            return result;
        }
        finally
        {
            state.Exit(values);
        }
    }

    private sealed class SnapshotState
    {
        private readonly HashSet<object> active = new(ReferenceEqualityComparer.Instance);
        private int depth;
        private int collections;
        private int nodes;
        private int bindings;
        private int schemaFields;
        private int executionRegions;
        private int executionPhases;
        private int executionEntries;

        internal void Enter(object value)
        {
            if (++depth > WorkflowPlanSnapshotLimits.MaximumNestingDepth)
            {
                depth--;
                throw new WorkflowPlanSnapshotException("maximum nesting depth exceeded");
            }

            if (!active.Add(value))
            {
                depth--;
                throw new WorkflowPlanSnapshotException("reference cycle detected");
            }
        }

        internal void Exit(object value)
        {
            active.Remove(value);
            depth--;
        }

        internal void CheckCollection(int count, string name)
        {
            if (count < 0 || count > WorkflowPlanSnapshotLimits.MaximumCollectionCount)
                throw new WorkflowPlanSnapshotException($"{name} collection count exceeds the bounded limit");
            if (++collections > WorkflowPlanSnapshotLimits.MaximumTotalCollections)
                throw new WorkflowPlanSnapshotException("total collection count exceeds the bounded limit");
        }

        internal void CountNode()
        {
            if (++nodes > WorkflowPlanSnapshotLimits.MaximumTotalNodes)
                throw new WorkflowPlanSnapshotException("total workflow node count exceeds the bounded limit");
        }

        internal void CountBinding()
        {
            if (++bindings > WorkflowPlanSnapshotLimits.MaximumTotalBindings)
                throw new WorkflowPlanSnapshotException("total binding count exceeds the bounded limit");
        }

        internal void CountSchemaField()
        {
            if (++schemaFields > WorkflowPlanSnapshotLimits.MaximumTotalSchemaFields)
                throw new WorkflowPlanSnapshotException("total schema field count exceeds the bounded limit");
        }

        internal void CountExecutionRegion()
        {
            if (++executionRegions > WorkflowPlanSnapshotLimits.MaximumExecutionRegions)
                throw new WorkflowPlanSnapshotException("execution region count exceeds the bounded limit");
        }

        internal void CountExecutionPhase()
        {
            if (++executionPhases > WorkflowPlanSnapshotLimits.MaximumExecutionPhases)
                throw new WorkflowPlanSnapshotException("execution phase count exceeds the bounded limit");
        }

        internal void CountExecutionEntry()
        {
            if (++executionEntries > WorkflowPlanSnapshotLimits.MaximumExecutionEntries)
                throw new WorkflowPlanSnapshotException("execution entry count exceeds the bounded limit");
        }

        internal void CheckExecutionOrderBudget(WorkflowExecutionOrder order)
        {
            ArgumentNullException.ThrowIfNull(order.Regions);
            if (order.Regions.Count > WorkflowPlanSnapshotLimits.MaximumExecutionRegions)
                throw new WorkflowPlanSnapshotException("execution region count exceeds the bounded limit");

            long phaseCount = 0;
            long entryCount = 0;
            foreach (var region in order.Regions)
            {
                ArgumentNullException.ThrowIfNull(region);
                ArgumentNullException.ThrowIfNull(region.Phases);
                phaseCount += region.Phases.Count;
                if (phaseCount > WorkflowPlanSnapshotLimits.MaximumExecutionPhases)
                    throw new WorkflowPlanSnapshotException("execution phase count exceeds the bounded limit");
                foreach (var phase in region.Phases)
                {
                    ArgumentNullException.ThrowIfNull(phase);
                    ArgumentNullException.ThrowIfNull(phase.NodePaths);
                    entryCount += phase.NodePaths.Count;
                    if (entryCount > WorkflowPlanSnapshotLimits.MaximumExecutionEntries)
                        throw new WorkflowPlanSnapshotException("execution entry count exceeds the bounded limit");
                }
            }
        }
    }
}

internal static class WorkflowPlanSnapshotLimits
{
    internal const int MaximumCollectionCount = 4_096;
    internal const int MaximumTotalCollections = 16_384;
    internal const int MaximumTotalNodes = 2_048;
    internal const int MaximumTotalBindings = 16_384;
    internal const int MaximumTotalSchemaFields = 16_384;
    internal const int MaximumExecutionRegions = 1_024;
    internal const int MaximumExecutionPhases = 4_096;
    internal const int MaximumExecutionEntries = 16_384;
    internal const int MaximumExecutionNodePaths = MaximumExecutionEntries;
    internal const int MaximumNestingDepth = 128;
}

internal sealed class WorkflowPlanSnapshotException : InvalidOperationException
{
    internal WorkflowPlanSnapshotException(string reason)
        : base($"Workflow plan snapshot rejected: {reason}.") { }
}
