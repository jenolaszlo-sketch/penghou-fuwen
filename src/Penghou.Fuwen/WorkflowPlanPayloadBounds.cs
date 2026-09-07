using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>
/// Checks caller-owned payload sizes before the snapshot starts allocating
/// detached arrays, dictionaries, or cloned JSON documents.
/// </summary>
internal sealed class WorkflowPlanPayloadBounds
{
    private readonly HashSet<object> active = new(ReferenceEqualityComparer.Instance);
    private int depth;
    private int collections;
    private long textBytes;
    private long jsonBytes;

    internal void Validate(WorkflowPlan plan)
    {
        Enter(plan);
        try
        {
            Text(plan.IrVersion, nameof(plan.IrVersion));
            Text(plan.LanguageVersion, nameof(plan.LanguageVersion));
            Text(plan.CompilerSemanticVersion, nameof(plan.CompilerSemanticVersion));
            Text(plan.CanonicalJsonVersion, nameof(plan.CanonicalJsonVersion));
            Text(plan.FingerprintVersion, nameof(plan.FingerprintVersion));
            Text(plan.Name, nameof(plan.Name));
            Text(plan.Revision, nameof(plan.Revision));
            Text(plan.RoutingPolicyRevision, nameof(plan.RoutingPolicyRevision));
            Type(plan.InputType);
            Type(plan.OutputType);
            List(plan.Schemas, Schema, "schemas");
            List(plan.CatalogueBindings, Descriptor, "catalogue bindings");
            Capabilities(plan.CapabilityManifest);
            List(plan.Nodes, Node, "workflow nodes");
            if (plan.ExecutionOrder is not null)
                ExecutionOrder(plan.ExecutionOrder);
        }
        finally
        {
            Exit(plan);
        }
    }

    private void Capabilities(CapabilityManifest manifest)
    {
        if (manifest is null)
            return;
        Enter(manifest);
        try
        {
            List(manifest.Requirements, requirement =>
            {
                if (requirement is null)
                    return;
                Enter(requirement);
                try
                {
                    Text(requirement.Name, "capability name");
                    Text(requirement.ScopeClass, "capability scope class");
                }
                finally
                {
                    Exit(requirement);
                }
            }, "capability requirements");
        }
        finally
        {
            Exit(manifest);
        }
    }

    private void ExecutionOrder(WorkflowExecutionOrder order)
    {
        Enter(order);
        try
        {
            List(order.Regions, region =>
            {
                if (region is null)
                    return;
                Enter(region);
                try
                {
                    Text(region.RegionPath, "execution region path");
                    List(region.Phases, phase =>
                    {
                        if (phase is null)
                            return;
                        Enter(phase);
                        try
                        {
                            List(phase.NodePaths, path => Text(path, "execution node path"), "execution phase node paths");
                        }
                        finally
                        {
                            Exit(phase);
                        }
                    }, "execution phases");
                }
                finally
                {
                    Exit(region);
                }
            }, "execution regions");
        }
        finally
        {
            Exit(order);
        }
    }

    private void Schema(ResolvedSchemaDefinition schema)
    {
        if (schema is null)
            return;
        Enter(schema);
        try
        {
            Descriptor(schema.Descriptor);
            switch (schema)
            {
                case ObjectSchemaDefinition value:
                    List(value.Fields, field =>
                    {
                        if (field is null)
                            return;
                        Enter(field);
                        try
                        {
                            Text(field.Name, "schema field name");
                            Type(field.Type);
                        }
                        finally
                        {
                            Exit(field);
                        }
                    }, "schema fields");
                    break;
                case EnumSchemaDefinition value:
                    List(value.Members, member =>
                    {
                        if (member is null)
                            return;
                        Enter(member);
                        try
                        {
                            Text(member.Name, "enum member name");
                            Text(member.Value, "enum member value");
                        }
                        finally
                        {
                            Exit(member);
                        }
                    }, "enum members");
                    break;
            }
        }
        finally
        {
            Exit(schema);
        }
    }

    private void Node(WorkflowNode node)
    {
        if (node is null)
            return;
        Enter(node);
        try
        {
            Text(node.Name, "workflow node name");
            Text(node.StructuralPath, "workflow node path");
            switch (node)
            {
                case ContextNode value:
                    Descriptor(value.Provider);
                    Arguments(value.Arguments);
                    Type(value.OutputType);
                    break;
                case InferenceNode value:
                    Descriptor(value.Profile);
                    Descriptor(value.PromptTemplate);
                    Arguments(value.Arguments);
                    List(value.ContextSnapshots, NodeOutput, "context snapshots");
                    Type(value.OutputType);
                    break;
                case ActivityNode value:
                    Descriptor(value.Activity);
                    Arguments(value.Arguments);
                    Type(value.OutputType);
                    break;
                case ConditionalNode value:
                    Condition(value.Condition);
                    List(value.Then, Node, "then branch nodes");
                    List(value.Else, Node, "else branch nodes");
                    break;
                case ReturnNode value:
                    Binding(value.Value);
                    break;
            }
        }
        finally
        {
            Exit(node);
        }
    }

    private void Arguments(IReadOnlyList<ArgumentBinding> arguments)
    {
        List(arguments, argument =>
        {
            if (argument is null)
                return;
            Enter(argument);
            try
            {
                Text(argument.Name, "argument name");
                Binding(argument.Value);
            }
            finally
            {
                Exit(argument);
            }
        });
    }

    private void Condition(ConditionExpression condition)
    {
        if (condition is null)
            return;
        Enter(condition);
        try
        {
            Binding(condition.Left);
            if (condition.Right is not null)
                Binding(condition.Right);
        }
        finally
        {
            Exit(condition);
        }
    }

    private void NodeOutput(NodeOutputBinding binding)
    {
        if (binding is null)
            return;
        Enter(binding);
        try
        {
            NodeOutputBody(binding);
        }
        finally
        {
            Exit(binding);
        }
    }

    private void NodeOutputBody(NodeOutputBinding binding)
    {
        Text(binding.NodePath, "node output path");
        List(binding.Projection, path => Text(path, "binding projection"));
    }

    private void Binding(Penghou.Fuwen.Binding binding)
    {
        if (binding is null)
            return;
        Enter(binding);
        try
        {
            switch (binding)
            {
                case InputBinding input:
                    List(input.Projection, path => Text(path, "binding projection"));
                    break;
                case NodeOutputBinding output:
                    NodeOutputBody(output);
                    break;
                case LiteralBinding literal:
                    Json(literal.Value);
                    break;
                case ListBinding list:
                    List(list.Items, Binding);
                    break;
                case ObjectBinding @object:
                    if (@object.Properties is null)
                        break;
                    CheckCollection(@object.Properties.Count, "object literal properties");
                    Enter(@object.Properties);
                    try
                    {
                        foreach (var pair in @object.Properties)
                        {
                            Text(pair.Key, "object literal property name");
                            Binding(pair.Value);
                        }
                    }
                    finally
                    {
                        Exit(@object.Properties);
                    }
                    break;
            }
        }
        finally
        {
            Exit(binding);
        }
    }

    private void Type(FuwenType type)
    {
        if (type is null)
            return;
        Enter(type);
        try
        {
            switch (type)
            {
                case NamedTypeReference named:
                    Descriptor(named.Schema);
                    break;
                case OptionalType optional:
                    Type(optional.ValueType);
                    break;
                case ListType list:
                    Type(list.ItemType);
                    break;
                case ArtifactType artifact:
                    Descriptor(artifact.ArtifactDescriptor);
                    break;
            }
        }
        finally
        {
            Exit(type);
        }
    }

    private void Descriptor(DescriptorReference descriptor)
    {
        if (descriptor is null)
            return;
        Enter(descriptor);
        try
        {
            Text(descriptor.Name, "descriptor name");
            Text(descriptor.Version, "descriptor version");
            Digest(descriptor.ContentDigest);
        }
        finally
        {
            Exit(descriptor);
        }
    }

    private void Digest(ContentDigest digest)
    {
        if (digest is null)
            return;
        Enter(digest);
        try
        {
            Text(digest.Algorithm, "digest algorithm");
            Text(digest.Contract, "digest contract");
            Text(digest.Value, "digest value");
        }
        finally
        {
            Exit(digest);
        }
    }

    private void List<T>(
        IReadOnlyList<T> values,
        Action<T> visit,
        string name = "snapshot collection")
    {
        if (values is null)
            return;
        CheckCollection(values.Count, name);
        Enter(values);
        try
        {
            for (var index = 0; index < values.Count; index++)
                visit(values[index]);
        }
        finally
        {
            Exit(values);
        }
    }

    private void CheckCollection(int count, string name)
    {
        if (count < 0 || count > WorkflowPlanSnapshotLimits.MaximumCollectionCount)
            throw new WorkflowPlanSnapshotException($"{name} collection count exceeds the bounded limit");
        if (++collections > WorkflowPlanSnapshotLimits.MaximumTotalCollections)
            throw new WorkflowPlanSnapshotException("total collection count exceeds the bounded limit");
    }

    private void Text(string? value, string name)
    {
        if (value is null)
            return;
        if (value.Length > WorkflowPlanSnapshotLimits.MaximumTextUtf8Bytes)
            throw new WorkflowPlanPayloadSizeException($"{name} exceeds the bounded text size");
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > WorkflowPlanSnapshotLimits.MaximumTextUtf8Bytes)
            throw new WorkflowPlanPayloadSizeException($"{name} exceeds the bounded text size");
        textBytes = checked(textBytes + bytes);
        if (textBytes > WorkflowPlanSnapshotLimits.MaximumTotalTextUtf8Bytes)
            throw new WorkflowPlanPayloadSizeException("aggregate text size exceeds the bounded limit");
    }

    private void Json(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined)
            return;
        var jsonNodes = 0;
        CheckJsonStructure(value, 1, ref jsonNodes);
        using var stream = new CountingStream(
            WorkflowPlanSnapshotLimits.MaximumJsonLiteralBytes,
            AddJsonBytes);
        using var writer = new Utf8JsonWriter(stream);
        value.WriteTo(writer);
        writer.Flush();
        if (stream.Count > WorkflowPlanSnapshotLimits.MaximumJsonLiteralBytes)
            throw new WorkflowPlanPayloadSizeException("JSON literal content exceeds the bounded size");
        if (jsonBytes > WorkflowPlanSnapshotLimits.MaximumTotalJsonLiteralBytes)
            throw new WorkflowPlanPayloadSizeException("aggregate JSON literal content exceeds the bounded limit");
    }

    private static void CheckJsonStructure(JsonElement value, int depth, ref int nodes)
    {
        if (depth > WorkflowPlanSnapshotLimits.MaximumNestingDepth)
            throw new WorkflowPlanSnapshotException("maximum JSON literal nesting depth exceeded");
        if (++nodes > WorkflowPlanSnapshotLimits.MaximumJsonLiteralNodes)
            throw new WorkflowPlanSnapshotException("JSON literal content exceeds the bounded size");
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    CheckJsonStructure(item, depth + 1, ref nodes);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    CheckJsonStructure(property.Value, depth + 1, ref nodes);
                break;
        }
    }

    private void AddJsonBytes(int bytes)
    {
        if (bytes < 0 || jsonBytes > WorkflowPlanSnapshotLimits.MaximumTotalJsonLiteralBytes - bytes)
            throw new WorkflowPlanPayloadSizeException("aggregate JSON literal content exceeds the bounded limit");
        jsonBytes += bytes;
    }

    private void Enter(object value)
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

    private void Exit(object value)
    {
        active.Remove(value);
        depth--;
    }

    private sealed class CountingStream : Stream
    {
        private readonly long limit;
        private readonly Action<int> countBytes;
        internal long Count { get; private set; }

        internal CountingStream(long limit, Action<int> countBytes)
        {
            this.limit = limit;
            this.countBytes = countBytes;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Add(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);

        private void Add(int bytes)
        {
            if (bytes < 0 || Count > limit - bytes)
                throw new WorkflowPlanPayloadSizeException("JSON literal content exceeds the bounded size");
            Count += bytes;
            countBytes(bytes);
        }
    }
}

internal sealed class WorkflowPlanPayloadSizeException : WorkflowPlanSnapshotException
{
    internal WorkflowPlanPayloadSizeException(string reason) : base(reason) { }
}
