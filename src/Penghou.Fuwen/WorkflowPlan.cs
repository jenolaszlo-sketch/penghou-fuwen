using System.Text.Json.Serialization;

namespace Penghou.Fuwen;

/// <summary>Canonicalization and fingerprint contracts supported by the Fuwen IR.</summary>
public static class FuwenContracts
{
    /// <summary>The historical v1 executable-plan contract.</summary>
    public const string IrVersion = "fuwen-ir/v1";
    /// <summary>The historical v1 executable-plan contract.</summary>
    public const string IrVersionV1 = IrVersion;
    /// <summary>The structured execution-schedule executable-plan contract.</summary>
    public const string IrVersionV2 = "fuwen-ir/v2";
    /// <summary>The typed-context-requirements executable-plan contract.</summary>
    public const string IrVersionV3 = "fuwen-ir/v3";
    /// <summary>The keyed fan-out executable-plan contract.</summary>
    public const string IrVersionV4 = "fuwen-ir/v4";
    /// <summary>The historical v1 compiler-semantics contract.</summary>
    public const string CompilerSemanticVersion = "compiler-semantics/1";
    /// <summary>The historical v1 compiler-semantics contract.</summary>
    public const string CompilerSemanticVersionV1 = CompilerSemanticVersion;
    /// <summary>The exact compiler-semantics contract required by IR v2.</summary>
    public const string CompilerSemanticVersionV2 = "compiler-semantics/2";
    /// <summary>The compiler-semantics contract for typed context requirements.</summary>
    public const string CompilerSemanticVersionV3 = "compiler-semantics/3";
    /// <summary>The compiler-semantics contract for keyed fan-out.</summary>
    public const string CompilerSemanticVersionV4 = "compiler-semantics/4";
    /// <summary>The canonical JSON contract used by all currently supported IR versions.</summary>
    public const string CanonicalJsonVersion = "penghou-canonical-json/v1";
    /// <summary>The historical v1 execution fingerprint envelope.</summary>
    public const string ExecutionFingerprintVersion = "fuwen-execution/v1";
    /// <summary>The historical v1 execution fingerprint envelope.</summary>
    public const string ExecutionFingerprintVersionV1 = ExecutionFingerprintVersion;
    /// <summary>The v2 execution fingerprint envelope for scheduled plans.</summary>
    public const string ExecutionFingerprintVersionV2 = "fuwen-execution/v2";
    /// <summary>The execution fingerprint envelope for typed context requirements.</summary>
    public const string ExecutionFingerprintVersionV3 = "fuwen-execution/v3";
    /// <summary>The execution fingerprint envelope for keyed fan-out.</summary>
    public const string ExecutionFingerprintVersionV4 = "fuwen-execution/v4";
    /// <summary>The first canonical authored-source fingerprint envelope.</summary>
    public const string SourceFingerprintVersion = "fuwen-source/v1";
    /// <summary>The maximum persisted canonical IR size accepted by the core verifier.</summary>
    public const int MaximumCanonicalPlanBytes = 4 * 1024 * 1024;
}

/// <summary>Base contract for explicitly named executable IR nodes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ContextNode), "context")]
[JsonDerivedType(typeof(InferenceNode), "inference")]
[JsonDerivedType(typeof(ActivityNode), "activity")]
[JsonDerivedType(typeof(ConditionalNode), "conditional")]
[JsonDerivedType(typeof(ReturnNode), "return")]
[JsonDerivedType(typeof(FanOutNode), "fan-out")]
public abstract record WorkflowNode(string Name, string StructuralPath);

/// <summary>Resolves an immutable context snapshot.</summary>
public sealed record ContextNode(
    string Name,
    string StructuralPath,
    DescriptorReference Provider,
    IReadOnlyList<ArgumentBinding> Arguments,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>A named, typed dependency on the output of a direct context node.</summary>
public sealed record ContextRequirement(
    string Name,
    NodeOutputBinding Source,
    FuwenType ExpectedType);

/// <summary>Executes structured or media inference through a resolved profile.</summary>
public sealed record InferenceNode(
    string Name,
    string StructuralPath,
    DescriptorReference Profile,
    DescriptorReference PromptTemplate,
    IReadOnlyList<ArgumentBinding> Arguments,
    IReadOnlyList<NodeOutputBinding> ContextSnapshots,
    FuwenType OutputType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ContextRequirement>? ContextRequirements = null) : WorkflowNode(Name, StructuralPath);

/// <summary>Executes one trusted catalogue activity.</summary>
public sealed record ActivityNode(
    string Name,
    string StructuralPath,
    DescriptorReference Activity,
    IReadOnlyList<ArgumentBinding> Arguments,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>Selects one named lexical branch using a deterministic condition.</summary>
public sealed record ConditionalNode(
    string Name,
    string StructuralPath,
    ConditionExpression Condition,
    IReadOnlyList<WorkflowNode> Then,
    IReadOnlyList<WorkflowNode> Else) : WorkflowNode(Name, StructuralPath);

/// <summary>Produces the typed workflow result.</summary>
public sealed record ReturnNode(
    string Name,
    string StructuralPath,
    Binding Value) : WorkflowNode(Name, StructuralPath);

/// <summary>The explicitly declared item binding for one keyed fan-out region.</summary>
public sealed record FanOutItemBinding(string Name, FuwenType Type);

/// <summary>References the current item while compiling a fan-out body.</summary>
public sealed record FanOutItemValueBinding(IReadOnlyList<string> Projection) : Binding;

/// <summary>
/// A bounded keyed fan-out region. Its source is evaluated once, keys are
/// validated before any body work starts, and yielded values are aggregated in
/// source order. Body references are closed over the item and earlier body
/// outputs only.
/// </summary>
public sealed record FanOutNode(
    string Name,
    string StructuralPath,
    Binding Source,
    FanOutItemBinding Item,
    Binding Key,
    IReadOnlyList<WorkflowNode> Body,
    Binding Yield,
    FuwenType ResultType,
    int MaximumItems,
    int MaximumConcurrency) : WorkflowNode(Name, StructuralPath);

/// <summary>The explicit completion schedule for an IR v2 workflow.</summary>
public sealed record WorkflowExecutionOrder(
    IReadOnlyList<WorkflowExecutionRegion> Regions);

/// <summary>A lexical workflow or structured-branch execution region.</summary>
public sealed record WorkflowExecutionRegion(
    string RegionPath,
    IReadOnlyList<WorkflowExecutionPhase> Phases);

/// <summary>A completion barrier containing unordered directly-owned node paths.</summary>
public sealed record WorkflowExecutionPhase(
    IReadOnlyList<string> NodePaths);

/// <summary>An immutable, completely resolved executable plan.</summary>
public sealed record WorkflowPlan(
    string IrVersion,
    string LanguageVersion,
    string CompilerSemanticVersion,
    string CanonicalJsonVersion,
    string FingerprintVersion,
    string Name,
    string Revision,
    FuwenType InputType,
    FuwenType OutputType,
    string RoutingPolicyRevision,
    IReadOnlyList<ResolvedSchemaDefinition> Schemas,
    IReadOnlyList<DescriptorReference> CatalogueBindings,
    CapabilityManifest CapabilityManifest,
    IReadOnlyList<WorkflowNode> Nodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkflowExecutionOrder? ExecutionOrder = null);
