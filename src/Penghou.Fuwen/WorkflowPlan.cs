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
    /// <summary>The historical v1 compiler-semantics contract.</summary>
    public const string CompilerSemanticVersion = "compiler-semantics/1";
    /// <summary>The historical v1 compiler-semantics contract.</summary>
    public const string CompilerSemanticVersionV1 = CompilerSemanticVersion;
    /// <summary>The exact compiler-semantics contract required by IR v2.</summary>
    public const string CompilerSemanticVersionV2 = "compiler-semantics/2";
    /// <summary>The canonical JSON contract used by all currently supported IR versions.</summary>
    public const string CanonicalJsonVersion = "penghou-canonical-json/v1";
    /// <summary>The historical v1 execution fingerprint envelope.</summary>
    public const string ExecutionFingerprintVersion = "fuwen-execution/v1";
    /// <summary>The historical v1 execution fingerprint envelope.</summary>
    public const string ExecutionFingerprintVersionV1 = ExecutionFingerprintVersion;
    /// <summary>The v2 execution fingerprint envelope for scheduled plans.</summary>
    public const string ExecutionFingerprintVersionV2 = "fuwen-execution/v2";
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
public abstract record WorkflowNode(string Name, string StructuralPath);

/// <summary>Resolves an immutable context snapshot.</summary>
public sealed record ContextNode(
    string Name,
    string StructuralPath,
    DescriptorReference Provider,
    IReadOnlyList<ArgumentBinding> Arguments,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

/// <summary>Executes structured or media inference through a resolved profile.</summary>
public sealed record InferenceNode(
    string Name,
    string StructuralPath,
    DescriptorReference Profile,
    DescriptorReference PromptTemplate,
    IReadOnlyList<ArgumentBinding> Arguments,
    IReadOnlyList<NodeOutputBinding> ContextSnapshots,
    FuwenType OutputType) : WorkflowNode(Name, StructuralPath);

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
