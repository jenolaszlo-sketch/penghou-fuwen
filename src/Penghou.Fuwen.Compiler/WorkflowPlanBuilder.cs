using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>
/// Builds the small, programmatic subset of Fuwen IR used by hosts and tests.
/// The builder has no parser or execution semantics; compilation remains the
/// authoritative binding, type, catalogue, and capability-checking boundary.
/// </summary>
public sealed class WorkflowPlanBuilder
{
    private readonly string name;
    private readonly string revision;
    private readonly FuwenType inputType;
    private readonly FuwenType outputType;
    private readonly string routingPolicyRevision;
    private readonly string languageVersion;
    private readonly List<ResolvedSchemaDefinition> schemas = [];
    private readonly List<DescriptorReference> catalogueBindings = [];
    private readonly List<CapabilityRequirement> capabilities = [];
    private readonly List<WorkflowNode> nodes = [];
    private WorkflowExecutionOrder? executionOrder;

    /// <summary>Creates a v2 builder with the current canonical contracts.</summary>
    public WorkflowPlanBuilder(
        string name,
        string revision,
        FuwenType inputType,
        FuwenType outputType,
        string routingPolicyRevision,
        string languageVersion = "fuwen-language/v1")
    {
        this.name = name ?? throw new ArgumentNullException(nameof(name));
        this.revision = revision ?? throw new ArgumentNullException(nameof(revision));
        this.inputType = inputType ?? throw new ArgumentNullException(nameof(inputType));
        this.outputType = outputType ?? throw new ArgumentNullException(nameof(outputType));
        this.routingPolicyRevision = routingPolicyRevision ?? throw new ArgumentNullException(nameof(routingPolicyRevision));
        this.languageVersion = languageVersion ?? throw new ArgumentNullException(nameof(languageVersion));
    }

    /// <summary>Adds a resolved schema definition to the authored plan.</summary>
    public WorkflowPlanBuilder AddSchema(ResolvedSchemaDefinition schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schemas.Add(schema);
        return this;
    }

    /// <summary>Adds an exact descriptor pin. Referenced pins are also inferred at build time.</summary>
    public WorkflowPlanBuilder AddCatalogueBinding(DescriptorReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        catalogueBindings.Add(descriptor);
        return this;
    }

    /// <summary>Adds an authored capability declaration for diagnostic purposes.</summary>
    /// <remarks>The compiler derives the authoritative manifest from trusted descriptors.</remarks>
    public WorkflowPlanBuilder RequireCapability(CapabilityRequirement capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        capabilities.Add(capability);
        return this;
    }

    /// <summary>Adds one supported node. Loops, waits, and side-effect execution are not builder constructs.</summary>
    public WorkflowPlanBuilder AddNode(WorkflowNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        nodes.Add(node);
        return this;
    }

    /// <summary>Adds one bounded keyed fan-out region to the programmatic plan.</summary>
    public WorkflowPlanBuilder AddFanOut(FanOutNode node) => AddNode(node);

    /// <summary>Sets the explicit v2 completion schedule.</summary>
    public WorkflowPlanBuilder SetExecutionOrder(WorkflowExecutionOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        executionOrder = order;
        return this;
    }

    /// <summary>Returns a detached plan snapshot. Semantic validation occurs in <see cref="WorkflowCompiler"/>.</summary>
    public WorkflowPlan Build()
        => BuildVersioned(FuwenContracts.IrVersionV2, FuwenContracts.CompilerSemanticVersionV2, FuwenContracts.ExecutionFingerprintVersionV2);

    /// <summary>Builds a pre-release v3 plan using typed context requirements.</summary>
    public WorkflowPlan BuildV3()
        => BuildVersioned(FuwenContracts.IrVersionV3, FuwenContracts.CompilerSemanticVersionV3, FuwenContracts.ExecutionFingerprintVersionV3);

    /// <summary>Builds a pre-release v4 plan containing bounded keyed fan-out regions.</summary>
    public WorkflowPlan BuildV4()
        => BuildVersioned(FuwenContracts.IrVersionV4, FuwenContracts.CompilerSemanticVersionV4, FuwenContracts.ExecutionFingerprintVersionV4);

    private WorkflowPlan BuildVersioned(string irVersion, string compilerSemanticVersion, string fingerprintVersion)
    {
        var plan = new WorkflowPlan(
            irVersion,
            languageVersion,
            compilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            fingerprintVersion,
            name,
            revision,
            inputType,
            outputType,
            routingPolicyRevision,
            schemas.ToArray(),
            MergeDescriptors(catalogueBindings, schemas, inputType, outputType, nodes),
            new CapabilityManifest(capabilities.ToArray()),
            nodes.ToArray(),
            executionOrder ?? throw new InvalidOperationException(
                $"{irVersion} requires an explicit execution order; use SetExecutionOrder before building the plan."));

        // The core snapshot is intentionally the only place that freezes the
        // recursive plan graph. This keeps builder and future parser paths
        // identical and prevents caller mutation after Build returns.
        return WorkflowPlanSnapshot.Create(plan);
    }

    private static IReadOnlyList<DescriptorReference> MergeDescriptors(
        IEnumerable<DescriptorReference> explicitBindings,
        IEnumerable<ResolvedSchemaDefinition> schemas,
        FuwenType inputType,
        FuwenType outputType,
        IEnumerable<WorkflowNode> nodes)
    {
        var bindings = new List<DescriptorReference>();
        foreach (var descriptor in explicitBindings)
            AddDescriptor(bindings, descriptor);
        foreach (var schema in schemas)
            AddDescriptor(bindings, schema.Descriptor);
        CollectType(inputType, bindings);
        CollectType(outputType, bindings);
        CollectNodes(nodes, bindings);
        return bindings.ToArray();
    }

    private static void CollectNodes(IEnumerable<WorkflowNode> values, List<DescriptorReference> bindings)
    {
        foreach (var node in values)
        {
            switch (node)
            {
                case ContextNode context:
                    AddDescriptor(bindings, context.Provider);
                    CollectType(context.OutputType, bindings);
                    break;
                case InferenceNode inference:
                    AddDescriptor(bindings, inference.Profile);
                    AddDescriptor(bindings, inference.PromptTemplate);
                    CollectType(inference.OutputType, bindings);
                    if (inference.ContextRequirements is not null)
                        foreach (var requirement in inference.ContextRequirements)
                            CollectType(requirement.ExpectedType, bindings);
                    break;
                case ActivityNode activity:
                    AddDescriptor(bindings, activity.Activity);
                    CollectType(activity.OutputType, bindings);
                    break;
                case ConditionalNode conditional:
                    CollectNodes(conditional.Then, bindings);
                    CollectNodes(conditional.Else, bindings);
                    break;
                case FanOutNode fanOut:
                    CollectBindingDescriptors(fanOut.Source, bindings);
                    CollectBindingDescriptors(fanOut.Key, bindings);
                    CollectBindingDescriptors(fanOut.Yield, bindings);
                    CollectType(fanOut.Item.Type, bindings);
                    CollectType(fanOut.ResultType, bindings);
                    CollectNodes(fanOut.Body, bindings);
                    break;
            }
        }
    }

    private static void CollectBindingDescriptors(Binding binding, List<DescriptorReference> bindings)
    {
        switch (binding)
        {
            case ListBinding list:
                foreach (var item in list.Items) CollectBindingDescriptors(item, bindings);
                break;
            case ObjectBinding @object:
                foreach (var item in @object.Properties.Values) CollectBindingDescriptors(item, bindings);
                break;
        }
    }

    private static void CollectType(FuwenType type, List<DescriptorReference> bindings)
    {
        switch (type)
        {
            case NamedTypeReference named:
                AddDescriptor(bindings, named.Schema);
                break;
            case OptionalType optional:
                CollectType(optional.ValueType, bindings);
                break;
            case ListType list:
                CollectType(list.ItemType, bindings);
                break;
            case ArtifactType artifact:
                AddDescriptor(bindings, artifact.ArtifactDescriptor);
                break;
        }
    }

    private static void AddDescriptor(List<DescriptorReference> bindings, DescriptorReference descriptor)
    {
        var duplicate = bindings.FirstOrDefault(existing =>
            existing.Kind == descriptor.Kind &&
            string.Equals(existing.Name, descriptor.Name, StringComparison.Ordinal) &&
            string.Equals(existing.Version, descriptor.Version, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            if (!duplicate.Equals(descriptor))
                throw new ArgumentException(
                    $"Descriptor '{descriptor.Kind}:{descriptor.Name}@{descriptor.Version}' is pinned to conflicting digests.",
                    nameof(descriptor));
            return;
        }

        bindings.Add(descriptor);
    }

}
