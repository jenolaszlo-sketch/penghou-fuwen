using System.Globalization;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>Form of prompt supplied to an inference adapter.</summary>
public enum InferencePromptForm
{
    /// <summary>A prompt resolved from an exact registered template descriptor.</summary>
    RegisteredTemplate,
    /// <summary>A workflow-owned prompt rendered for this invocation.</summary>
    WorkflowOwned,
}

/// <summary>Effect class requested by an inference tool call.</summary>
public enum InferenceToolEffect
{
    /// <summary>The tool only reads or derives information.</summary>
    ReadOnly,
    /// <summary>The tool performs an externally visible but repeat-safe update.</summary>
    IdempotentWrite,
    /// <summary>The tool performs an externally visible update.</summary>
    ExternalWrite,
    /// <summary>The tool can delete, publish, transact, or otherwise cause a destructive effect.</summary>
    Destructive,
}

/// <summary>Aggregate bound dimension understood by the inference protocol.</summary>
public enum InferenceLimitDimension
{
    /// <summary>Maximum protocol turns, including model turns.</summary>
    Turns,
    /// <summary>Maximum provider model calls.</summary>
    ModelCalls,
    /// <summary>Maximum internal tool calls.</summary>
    ToolCalls,
    /// <summary>Maximum prompt tokens across model calls.</summary>
    PromptTokens,
    /// <summary>Maximum completion tokens across model calls.</summary>
    CompletionTokens,
    /// <summary>Maximum total tokens across model calls.</summary>
    TotalTokens,
    /// <summary>Maximum elapsed protocol duration in milliseconds.</summary>
    DurationMilliseconds,
    /// <summary>Maximum cost in provider-neutral currency microunits.</summary>
    CostMicrounits,
    /// <summary>Maximum bytes in tool arguments.</summary>
    ToolArgumentBytes,
    /// <summary>Maximum bytes in tool results.</summary>
    ToolResultBytes,
    /// <summary>Maximum retained conversation bytes.</summary>
    RetainedConversationBytes,
    /// <summary>Maximum retained evidence bytes.</summary>
    RetainedEvidenceBytes,
}

/// <summary>Quality of durable recovery support offered by an adapter.</summary>
public enum InferenceRecoveryQuality
{
    /// <summary>No durable continuation or reconciliation contract is offered.</summary>
    Unsupported,
    /// <summary>Completed operations can be resumed from durable state.</summary>
    Resumable,
    /// <summary>Ambiguous operations can additionally be reconciled without guessing.</summary>
    Reconcilable,
}

/// <summary>Quality of usage evidence returned by an adapter.</summary>
public enum InferenceUsageQuality
{
    /// <summary>Usage is unavailable or cannot be trusted.</summary>
    Unknown,
    /// <summary>Usage is available but estimated.</summary>
    Estimated,
    /// <summary>Usage is exact for the admitted operation.</summary>
    Exact,
}

/// <summary>Quality of pricing evidence returned by an adapter.</summary>
public enum InferencePricingQuality
{
    /// <summary>Pricing is unavailable or cannot be trusted.</summary>
    Unknown,
    /// <summary>Pricing is available but estimated.</summary>
    Estimated,
    /// <summary>Pricing is exact for the admitted operation.</summary>
    Exact,
}

/// <summary>A bounded aggregate inference limit.</summary>
public sealed class InferenceLimit
{
    /// <summary>Creates a limit with a strictly positive bounded maximum.</summary>
    public InferenceLimit(InferenceLimitDimension dimension, long maximum)
    {
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));
        if (maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Inference limits must be positive.");
        if (maximum > 1_000_000_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Inference limits exceed the supported bound.");
        Dimension = dimension;
        Maximum = maximum;
    }

    /// <summary>The bounded dimension.</summary>
    public InferenceLimitDimension Dimension { get; }
    /// <summary>The maximum admitted value for <see cref="Dimension"/>.</summary>
    public long Maximum { get; }
}

/// <summary>An immutable, unique collection of aggregate inference limits.</summary>
public sealed class InferenceLimitSet
{
    /// <summary>Maximum number of dimensions in one set.</summary>
    public const int MaximumDimensions = 16;

    /// <summary>Creates a detached set of limits.</summary>
    public InferenceLimitSet(IReadOnlyList<InferenceLimit>? limits = null)
    {
        limits ??= [];
        if (limits.Count > MaximumDimensions)
            throw new ArgumentOutOfRangeException(nameof(limits), $"An inference limit set supports at most {MaximumDimensions} dimensions.");
        var copy = limits.Select(static limit => limit ?? throw new ArgumentException("Limits cannot contain null values.", nameof(limits))).ToArray();
        if (copy.Select(static limit => limit.Dimension).Distinct().Count() != copy.Length)
            throw new ArgumentException("Inference limit dimensions must be unique.", nameof(limits));
        Limits = Array.AsReadOnly(copy);
    }

    /// <summary>The detached limits in deterministic caller order.</summary>
    public IReadOnlyList<InferenceLimit> Limits { get; }

    /// <summary>Returns a maximum, or null when the dimension is not bounded.</summary>
    public long? GetMaximum(InferenceLimitDimension dimension) =>
        !Enum.IsDefined(dimension)
            ? throw new ArgumentOutOfRangeException(nameof(dimension))
            : Limits.FirstOrDefault(limit => limit.Dimension == dimension)?.Maximum;
}

/// <summary>One exact tool binding and its admitted effect class.</summary>
public sealed class InferenceToolRequirement
{
    /// <summary>Creates one detached tool requirement.</summary>
    public InferenceToolRequirement(DescriptorReference descriptor, InferenceToolEffect effect = InferenceToolEffect.ReadOnly)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));
        if (descriptor.Kind != DescriptorKind.Tool)
            throw new ArgumentException("Inference tool requirements must reference tool descriptors.", nameof(descriptor));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        Descriptor = RuntimeValueSnapshot.CloneDescriptor(descriptor);
        Effect = effect;
    }

    /// <summary>The exact admitted tool descriptor.</summary>
    public DescriptorReference Descriptor { get; }
    /// <summary>The effect class the host must authorize.</summary>
    public InferenceToolEffect Effect { get; }
}

/// <summary>Provider-neutral immutable capabilities exposed by one inference adapter.</summary>
public sealed class InferenceFeatureManifest
{
    /// <summary>Maximum number of advertised feature values.</summary>
    public const int MaximumVersions = 32;
    /// <summary>Maximum number of exact descriptor bindings.</summary>
    public const int MaximumBindings = 256;

    /// <summary>Creates a bounded adapter feature manifest.</summary>
    public InferenceFeatureManifest(
        IReadOnlyList<InferencePromptForm> supportedPromptForms,
        IReadOnlyList<InferenceModality> supportedModalities,
        bool supportsContextDelivery,
        int? maximumContextPayloadUtf8Bytes,
        IReadOnlyList<InferenceToolEffect> supportedToolEffects,
        IReadOnlyList<InferenceLimit> supportedLimits,
        InferenceRecoveryQuality recoveryQuality,
        InferenceUsageQuality usageQuality,
        InferencePricingQuality pricingQuality,
        bool supportsStructuredOutput = true,
        bool supportsSyntheticStructuredOutput = false,
        IReadOnlyList<DescriptorReference>? profiles = null,
        IReadOnlyList<DescriptorReference>? promptTemplates = null,
        IReadOnlyList<DescriptorReference>? tools = null,
        IReadOnlyList<string>? workflowPromptDigests = null)
    {
        SupportedPromptForms = EnumList(supportedPromptForms, nameof(supportedPromptForms));
        SupportedModalities = EnumList(supportedModalities, nameof(supportedModalities));
        if (maximumContextPayloadUtf8Bytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumContextPayloadUtf8Bytes));
        if (supportsContextDelivery && maximumContextPayloadUtf8Bytes is null)
            throw new ArgumentException("Context delivery support requires a maximum payload.", nameof(maximumContextPayloadUtf8Bytes));
        SupportsContextDelivery = supportsContextDelivery;
        MaximumContextPayloadUtf8Bytes = maximumContextPayloadUtf8Bytes;
        SupportedToolEffects = EnumList(supportedToolEffects, nameof(supportedToolEffects));
        if (SupportedToolEffects.Any(static effect => effect != InferenceToolEffect.ReadOnly))
            throw new ArgumentException("The initial inference protocol supports read-only tools only.", nameof(supportedToolEffects));
        ArgumentNullException.ThrowIfNull(supportedLimits, nameof(supportedLimits));
        SupportedLimits = new InferenceLimitSet(supportedLimits);
        if (!Enum.IsDefined(recoveryQuality))
            throw new ArgumentOutOfRangeException(nameof(recoveryQuality));
        if (!Enum.IsDefined(usageQuality))
            throw new ArgumentOutOfRangeException(nameof(usageQuality));
        if (!Enum.IsDefined(pricingQuality))
            throw new ArgumentOutOfRangeException(nameof(pricingQuality));
        RecoveryQuality = recoveryQuality;
        UsageQuality = usageQuality;
        PricingQuality = pricingQuality;
        SupportsStructuredOutput = supportsStructuredOutput;
        SupportsSyntheticStructuredOutput = supportsSyntheticStructuredOutput;
        Profiles = BindingList(profiles, DescriptorKind.InferenceProfile, nameof(profiles));
        PromptTemplates = BindingList(promptTemplates, DescriptorKind.PromptTemplate, nameof(promptTemplates));
        Tools = BindingList(tools, DescriptorKind.Tool, nameof(tools));
        WorkflowPromptDigests = TextList(workflowPromptDigests ?? [], nameof(workflowPromptDigests), MaximumBindings);
    }

    /// <summary>Prompt forms understood by the adapter.</summary>
    public IReadOnlyList<InferencePromptForm> SupportedPromptForms { get; }
    /// <summary>Output modalities understood by the adapter.</summary>
    public IReadOnlyList<InferenceModality> SupportedModalities { get; }
    /// <summary>Whether typed context inputs can be delivered.</summary>
    public bool SupportsContextDelivery { get; }
    /// <summary>Maximum context payload in UTF-8 bytes, when context is supported.</summary>
    public int? MaximumContextPayloadUtf8Bytes { get; }
    /// <summary>Tool effect classes understood by the adapter.</summary>
    public IReadOnlyList<InferenceToolEffect> SupportedToolEffects { get; }
    /// <summary>Host maxima for each supported aggregate limit.</summary>
    public InferenceLimitSet SupportedLimits { get; }
    /// <summary>Durable recovery quality.</summary>
    public InferenceRecoveryQuality RecoveryQuality { get; }
    /// <summary>Usage evidence quality.</summary>
    public InferenceUsageQuality UsageQuality { get; }
    /// <summary>Pricing evidence quality.</summary>
    public InferencePricingQuality PricingQuality { get; }
    /// <summary>Whether declared structured output is supported.</summary>
    public bool SupportsStructuredOutput { get; }
    /// <summary>Whether structured output may be synthesized when the provider lacks native support.</summary>
    public bool SupportsSyntheticStructuredOutput { get; }
    /// <summary>Exact profiles available to this adapter.</summary>
    public IReadOnlyList<DescriptorReference> Profiles { get; }
    /// <summary>Exact prompt templates available to this adapter.</summary>
    public IReadOnlyList<DescriptorReference> PromptTemplates { get; }
    /// <summary>Exact tools available to this adapter.</summary>
    public IReadOnlyList<DescriptorReference> Tools { get; }
    /// <summary>Exact workflow-owned prompt semantic digests available to this adapter.</summary>
    public IReadOnlyList<string> WorkflowPromptDigests { get; }

    /// <summary>Preflights one exact requirement against this manifest.</summary>
    public InferencePreflightReport Preflight(InferenceExecutionRequirement requirement) =>
        InferencePreflight.Evaluate(requirement, this);

    private static IReadOnlyList<string> TextList(
        IReadOnlyList<string> values,
        string parameterName,
        int maximumCount = MaximumVersions)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximumCount)
            throw new ArgumentOutOfRangeException(parameterName);
        var copy = values.Select(value => RuntimeValueSnapshot.Text(value, parameterName, InferenceExecutionEvidence.MaximumIdentityUtf8Bytes)).ToArray();
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Manifest versions must be unique.", parameterName);
        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<T> EnumList<T>(IReadOnlyList<T> values, string parameterName) where T : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumVersions)
            throw new ArgumentOutOfRangeException(parameterName);
        var copy = values.ToArray();
        if (copy.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentOutOfRangeException(parameterName, "Manifest features must contain only defined enum values.");
        if (copy.Distinct().Count() != copy.Length)
            throw new ArgumentException("Manifest features must be unique.", parameterName);
        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<DescriptorReference> BindingList(IReadOnlyList<DescriptorReference>? values, DescriptorKind expected, string parameterName)
    {
        values ??= [];
        if (values.Count > MaximumBindings)
            throw new ArgumentOutOfRangeException(parameterName);
        var copy = values.Select(value => ExecutionPortValidation.Descriptor(value, expected, parameterName)).ToArray();
        if (copy.Distinct().Count() != copy.Length)
            throw new ArgumentException("Manifest bindings must be unique.", parameterName);
        return Array.AsReadOnly(copy);
    }
}

/// <summary>Stable diagnostic code emitted by inference preflight.</summary>
public enum InferencePreflightDiagnosticCode
{
    /// <summary>The adapter does not support the requested prompt form.</summary>
    UnsupportedPromptForm,
    /// <summary>The adapter does not support the requested modality.</summary>
    UnsupportedModality,
    /// <summary>The adapter cannot produce the required structured result.</summary>
    StructuredOutputUnavailable,
    /// <summary>The adapter cannot deliver context.</summary>
    ContextDeliveryUnavailable,
    /// <summary>The required context bound exceeds the adapter maximum.</summary>
    ContextPayloadTooLarge,
    /// <summary>The exact profile is not bound.</summary>
    MissingProfileBinding,
    /// <summary>The exact registered prompt template is not bound.</summary>
    MissingPromptTemplateBinding,
    /// <summary>An exact required tool is not bound.</summary>
    MissingToolBinding,
    /// <summary>A required tool effect is unsupported.</summary>
    UnsupportedToolEffect,
    /// <summary>A required aggregate limit dimension is unsupported.</summary>
    UnsupportedLimit,
    /// <summary>A requested limit exceeds the adapter maximum.</summary>
    LimitExceedsHostMaximum,
    /// <summary>The adapter cannot meet the required recovery quality.</summary>
    RecoveryUnsupported,
    /// <summary>The adapter cannot provide the required usage evidence.</summary>
    UsageEvidenceUnavailable,
    /// <summary>The adapter cannot provide the required pricing evidence.</summary>
    PricingEvidenceUnavailable,
    /// <summary>The exact workflow-owned prompt digest is not bound.</summary>
    MissingWorkflowPromptBinding,
}

/// <summary>A bounded, stable explanation emitted by inference preflight.</summary>
public sealed class InferencePreflightDiagnostic
{
    /// <summary>Creates one diagnostic.</summary>
    public InferencePreflightDiagnostic(InferencePreflightDiagnosticCode code, string message, bool isError = true, string? subject = null)
    {
        if (!Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code));
        Code = code;
        Message = RuntimeValueSnapshot.Text(message, nameof(message), InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes);
        Subject = RuntimeValueSnapshot.OptionalText(subject, nameof(subject), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        IsError = isError;
    }

    /// <summary>Stable machine-readable code.</summary>
    public InferencePreflightDiagnosticCode Code { get; }
    /// <summary>Bounded human-readable explanation.</summary>
    public string Message { get; }
    /// <summary>Whether the diagnostic makes the requirement non-executable.</summary>
    public bool IsError { get; }
    /// <summary>Stable descriptor or dimension identity associated with the diagnostic.</summary>
    public string? Subject { get; }
}

/// <summary>Result of comparing an exact requirement with an adapter manifest.</summary>
public sealed class InferencePreflightReport
{
    internal InferencePreflightReport(
        InferenceExecutionRequirement requirement,
        InferenceFeatureManifest manifest,
        IReadOnlyList<InferencePreflightDiagnostic> diagnostics,
        InferenceLimitSet effectiveLimits,
        IReadOnlyList<string> matchedFeatures,
        IReadOnlyList<string> missingBindings)
    {
        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(matchedFeatures);
        ArgumentNullException.ThrowIfNull(missingBindings);
        Diagnostics = Array.AsReadOnly(diagnostics.Select(static diagnostic => diagnostic ?? throw new ArgumentException("Diagnostics cannot contain null values.", nameof(diagnostics))).ToArray());
        EffectiveLimits = effectiveLimits ?? throw new ArgumentNullException(nameof(effectiveLimits));
        MatchedFeatures = Array.AsReadOnly(matchedFeatures.Select(value => RuntimeValueSnapshot.Text(value, nameof(matchedFeatures), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes)).ToArray());
        MissingBindings = Array.AsReadOnly(missingBindings.Select(value => RuntimeValueSnapshot.Text(value, nameof(missingBindings), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes)).ToArray());
    }

    /// <summary>The exact detached requirement that was checked.</summary>
    public InferenceExecutionRequirement Requirement { get; }
    /// <summary>The immutable adapter manifest used for the check.</summary>
    public InferenceFeatureManifest Manifest { get; }
    /// <summary>Stable diagnostics in deterministic evaluation order.</summary>
    public IReadOnlyList<InferencePreflightDiagnostic> Diagnostics { get; }
    /// <summary>Limits effective after applying the requirement's narrower bounds.</summary>
    public InferenceLimitSet EffectiveLimits { get; }
    /// <summary>Features that matched the requirement.</summary>
    public IReadOnlyList<string> MatchedFeatures { get; }
    /// <summary>Exact bindings unavailable from the manifest.</summary>
    public IReadOnlyList<string> MissingBindings { get; }
    /// <summary>Whether registration and execution may proceed.</summary>
    public bool IsExecutable => Diagnostics.All(static diagnostic => !diagnostic.IsError);
    /// <summary>Converts the first blocking diagnostic to the legacy failure surface.</summary>
    public ExecutionFailure? Failure => IsExecutable ? null : new ExecutionFailure(
        ExecutionFailureKind.Admission,
        ExecutionFailureCode.NotAdmitted,
        Diagnostics.First(static diagnostic => diagnostic.IsError).Message);
}

/// <summary>Deterministically renders provider-neutral inference preflight reports.</summary>
public static class InferencePreflightReportRenderer
{
    /// <summary>Renders a compact, stable human-readable report.</summary>
    public static string RenderHuman(InferencePreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append("Inference preflight: ").Append(report.IsExecutable ? "executable" : "blocked").AppendLine();
        builder.Append("Prompt form: ").AppendLine(EnumText(report.Requirement.PromptForm));
        builder.Append("Modality: ").AppendLine(report.Requirement.Modality is null
            ? "unspecified"
            : EnumText(report.Requirement.Modality.Value));
        builder.Append("Profile: ").AppendLine(DescriptorText(report.Requirement.Profile));
        builder.Append("Prompt: ").AppendLine(report.Requirement.PromptTemplate is null
            ? $"workflow-owned ({report.Requirement.PromptDigest})"
            : DescriptorText(report.Requirement.PromptTemplate));
        builder.AppendLine("Matched features:");
        foreach (var feature in report.MatchedFeatures)
            builder.Append("- ").AppendLine(feature);
        builder.AppendLine("Missing bindings:");
        foreach (var binding in report.MissingBindings)
            builder.Append("- ").AppendLine(binding);
        builder.AppendLine("Effective limits:");
        foreach (var limit in report.EffectiveLimits.Limits)
            builder.Append("- ").Append(EnumText(limit.Dimension)).Append(": ")
                .AppendLine(limit.Maximum.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("Diagnostics:");
        foreach (var diagnostic in report.Diagnostics)
        {
            builder.Append(diagnostic.IsError ? "ERROR " : "INFO ")
                .Append(EnumText(diagnostic.Code)).Append(": ")
                .Append(diagnostic.Message);
            if (diagnostic.Subject is not null)
                builder.Append(" [").Append(diagnostic.Subject).Append(']');
            builder.AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>Renders a compact, stable JSON report.</summary>
    public static string RenderJson(InferencePreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("executable", report.IsExecutable);
        writer.WriteString("promptForm", EnumText(report.Requirement.PromptForm));
        writer.WriteString("modality", report.Requirement.Modality is null
            ? "unspecified"
            : EnumText(report.Requirement.Modality.Value));
        writer.WriteString("profile", DescriptorText(report.Requirement.Profile));
        if (report.Requirement.PromptTemplate is null)
        {
            writer.WriteString("promptFormDetail", "workflow-owned");
            writer.WriteString("promptDigest", report.Requirement.PromptDigest);
        }
        else
            writer.WriteString("promptTemplate", DescriptorText(report.Requirement.PromptTemplate));
        writer.WriteStartArray("matchedFeatures");
        foreach (var feature in report.MatchedFeatures) writer.WriteStringValue(feature);
        writer.WriteEndArray();
        writer.WriteStartArray("missingBindings");
        foreach (var binding in report.MissingBindings) writer.WriteStringValue(binding);
        writer.WriteEndArray();
        writer.WriteStartArray("effectiveLimits");
        foreach (var limit in report.EffectiveLimits.Limits)
        {
            writer.WriteStartObject();
            writer.WriteString("dimension", EnumText(limit.Dimension));
            writer.WriteNumber("maximum", limit.Maximum);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in report.Diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", EnumText(diagnostic.Code));
            writer.WriteBoolean("isError", diagnostic.IsError);
            if (diagnostic.Subject is not null) writer.WriteString("subject", diagnostic.Subject);
            writer.WriteString("message", diagnostic.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Renders the report as deterministic human-readable text.</summary>
    public static string ToHumanString(InferencePreflightReport report) => RenderHuman(report);

    /// <summary>Renders the report as deterministic JSON.</summary>
    public static string ToJson(InferencePreflightReport report) => RenderJson(report);

    private static string DescriptorText(DescriptorReference descriptor) =>
        $"{descriptor.Name}@{descriptor.Version}";

    private static string EnumText<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }

}

/// <summary>Evaluates provider-neutral inference requirements without provider work.</summary>
public static class InferencePreflight
{
    /// <summary>Compares a requirement with an adapter feature manifest.</summary>
    public static InferencePreflightReport Evaluate(InferenceExecutionRequirement requirement, InferenceFeatureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(manifest);
        var diagnostics = new List<InferencePreflightDiagnostic>();
        var matched = new List<string>();
        var missing = new List<string>();

        if (!manifest.SupportedPromptForms.Contains(requirement.PromptForm))
            diagnostics.Add(new(InferencePreflightDiagnosticCode.UnsupportedPromptForm, "The adapter does not support the required prompt form.", subject: requirement.PromptForm.ToString()));
        else matched.Add("prompt-form");
        if (requirement.Modality is not null)
        {
            if (!manifest.SupportedModalities.Contains(requirement.Modality.Value))
                diagnostics.Add(new(InferencePreflightDiagnosticCode.UnsupportedModality, "The adapter does not support the required inference modality.", subject: requirement.Modality.Value.ToString()));
            else matched.Add("modality");
        }
        if (requirement.RequiresStructuredOutput && !manifest.SupportsStructuredOutput && !manifest.SupportsSyntheticStructuredOutput)
            diagnostics.Add(new(InferencePreflightDiagnosticCode.StructuredOutputUnavailable, "The adapter cannot produce the required structured output."));
        else if (requirement.RequiresStructuredOutput)
            matched.Add("structured-output");
        if (!manifest.Profiles.Contains(requirement.Profile))
        {
            diagnostics.Add(new(InferencePreflightDiagnosticCode.MissingProfileBinding, "The exact inference profile is not bound by the adapter manifest.", subject: requirement.Profile.Name));
            missing.Add(requirement.Profile.Name);
        }
        else matched.Add("profile");
        if (requirement.PromptTemplate is not null && !manifest.PromptTemplates.Contains(requirement.PromptTemplate))
        {
            diagnostics.Add(new(InferencePreflightDiagnosticCode.MissingPromptTemplateBinding, "The exact prompt template is not bound by the adapter manifest.", subject: requirement.PromptTemplate.Name));
            missing.Add(requirement.PromptTemplate.Name);
        }
        if (requirement.PromptDigest is not null &&
            !manifest.WorkflowPromptDigests.Contains(requirement.PromptDigest, StringComparer.Ordinal))
        {
            diagnostics.Add(new(InferencePreflightDiagnosticCode.MissingWorkflowPromptBinding, "The exact workflow-owned prompt is not bound by the adapter manifest.", subject: requirement.PromptDigest));
            missing.Add(requirement.PromptDigest);
        }
        foreach (var tool in requirement.ToolRequirements)
        {
            if (!manifest.Tools.Contains(tool.Descriptor))
            {
                diagnostics.Add(new(InferencePreflightDiagnosticCode.MissingToolBinding, "The exact tool descriptor is not bound by the adapter manifest.", subject: tool.Descriptor.Name));
                missing.Add(tool.Descriptor.Name);
            }
            else if (!manifest.SupportedToolEffects.Contains(tool.Effect))
                diagnostics.Add(new(InferencePreflightDiagnosticCode.UnsupportedToolEffect, "The adapter does not support the required tool effect.", subject: tool.Effect.ToString()));
            else
                matched.Add($"tool:{tool.Descriptor.Name}");
        }
        if (requirement.MinimumRecoveryQuality > manifest.RecoveryQuality)
            diagnostics.Add(new(InferencePreflightDiagnosticCode.RecoveryUnsupported, "The adapter does not provide the required durable recovery quality.", subject: requirement.MinimumRecoveryQuality.ToString()));
        else if (requirement.MinimumRecoveryQuality != InferenceRecoveryQuality.Unsupported)
            matched.Add("recovery");
        if (requirement.RequiresExactUsageEvidence && manifest.UsageQuality != InferenceUsageQuality.Exact)
            diagnostics.Add(new(InferencePreflightDiagnosticCode.UsageEvidenceUnavailable, "The adapter does not provide exact usage evidence."));
        else if (requirement.RequiresExactUsageEvidence)
            matched.Add("usage-evidence");
        if (requirement.RequiresExactPricingEvidence && manifest.PricingQuality != InferencePricingQuality.Exact)
            diagnostics.Add(new(InferencePreflightDiagnosticCode.PricingEvidenceUnavailable, "The adapter does not provide exact pricing evidence."));
        else if (requirement.RequiresExactPricingEvidence)
            matched.Add("pricing-evidence");
        if (requirement.HasContextInputs)
        {
            if (!manifest.SupportsContextDelivery)
                diagnostics.Add(new(InferencePreflightDiagnosticCode.ContextDeliveryUnavailable, "The adapter cannot deliver the required context inputs."));
            else if (requirement.MaximumContextPayloadUtf8Bytes is not null &&
                     (manifest.MaximumContextPayloadUtf8Bytes is null || requirement.MaximumContextPayloadUtf8Bytes > manifest.MaximumContextPayloadUtf8Bytes))
                diagnostics.Add(new(InferencePreflightDiagnosticCode.ContextPayloadTooLarge, "The required context payload exceeds the adapter maximum.", subject: requirement.MaximumContextPayloadUtf8Bytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            else
                matched.Add("context-delivery");
        }
        var effective = new List<InferenceLimit>();
        foreach (var requested in requirement.Limits.Limits)
        {
            var maximum = manifest.SupportedLimits.GetMaximum(requested.Dimension);
            if (maximum is null)
                diagnostics.Add(new(InferencePreflightDiagnosticCode.UnsupportedLimit, "The adapter does not expose the required aggregate limit dimension.", subject: requested.Dimension.ToString()));
            else if (requested.Maximum > maximum.Value)
                diagnostics.Add(new(InferencePreflightDiagnosticCode.LimitExceedsHostMaximum, "The requested aggregate limit exceeds the adapter maximum.", subject: requested.Dimension.ToString()));
            else
                effective.Add(new(requested.Dimension, requested.Maximum));
        }
        return new InferencePreflightReport(requirement, manifest, diagnostics, new InferenceLimitSet(effective), matched, missing);
    }
}
