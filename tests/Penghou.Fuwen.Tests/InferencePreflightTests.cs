using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class InferencePreflightTests
{
    [Fact]
    public void Exact_supported_requirement_is_executable_with_effective_limits()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "answer");
        var tool = Descriptor(DescriptorKind.Tool, "lookup");
        var requirement = new InferenceExecutionRequirement(
            profile, prompt, null, [tool], hasContextInputs: false, modality: InferenceModality.StructuredText,
            toolRequirements: [new InferenceToolRequirement(tool, InferenceToolEffect.ReadOnly)],
            limits: new InferenceLimitSet([new(InferenceLimitDimension.ModelCalls, 2)]),
            requiresStructuredOutput: true);
        var manifest = Manifest(profile, prompt, tool,
            supportedLimits: [new(InferenceLimitDimension.ModelCalls, 5)]);

        var report = InferencePreflight.Evaluate(requirement, manifest);

        report.IsExecutable.Should().BeTrue();
        report.Diagnostics.Should().BeEmpty();
        report.EffectiveLimits.GetMaximum(InferenceLimitDimension.ModelCalls).Should().Be(2);
        report.MatchedFeatures.Should().ContainInOrder("prompt-form", "modality", "structured-output", "profile", "tool:lookup");
    }

    [Fact]
    public void Missing_binding_unsupported_effect_and_limit_fail_closed()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "answer");
        var tool = Descriptor(DescriptorKind.Tool, "publish");
        var requirement = new InferenceExecutionRequirement(
            profile, prompt, null, [tool], hasContextInputs: false, modality: InferenceModality.StructuredText,
            toolRequirements: [new InferenceToolRequirement(tool, InferenceToolEffect.ExternalWrite)],
            limits: new InferenceLimitSet([new(InferenceLimitDimension.CostMicrounits, 10)]));
        var manifest = Manifest(Descriptor(DescriptorKind.InferenceProfile, "other"), prompt, null,
            supportedLimits: [new(InferenceLimitDimension.ModelCalls, 5)]);

        var report = manifest.Preflight(requirement);

        report.IsExecutable.Should().BeFalse();
        report.Diagnostics.Select(d => d.Code).Should().ContainInOrder(
            InferencePreflightDiagnosticCode.MissingProfileBinding,
            InferencePreflightDiagnosticCode.MissingToolBinding,
            InferencePreflightDiagnosticCode.UnsupportedLimit);
        report.Failure.Should().NotBeNull();
        report.EffectiveLimits.Limits.Should().BeEmpty();

        var effectReport = Manifest(Descriptor(DescriptorKind.InferenceProfile, "other"), prompt, tool,
            supportedLimits: [new(InferenceLimitDimension.ModelCalls, 5)]).Preflight(requirement);
        effectReport.IsExecutable.Should().BeFalse();
        effectReport.Diagnostics.Select(d => d.Code).Should().ContainInOrder(
            InferencePreflightDiagnosticCode.MissingProfileBinding,
            InferencePreflightDiagnosticCode.UnsupportedToolEffect,
            InferencePreflightDiagnosticCode.UnsupportedLimit);
    }

    [Fact]
    public void Requirement_and_manifest_collections_are_snapshotted()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "answer");
        var tool = Descriptor(DescriptorKind.Tool, "lookup");
        var tools = new List<DescriptorReference> { tool };
        var requirement = new InferenceExecutionRequirement(
            profile, prompt, null, tools, hasContextInputs: false,
            modality: InferenceModality.StructuredText);
        var manifest = new InferenceFeatureManifest(
            [InferencePromptForm.RegisteredTemplate], [InferenceModality.StructuredText],
            false, null, [InferenceToolEffect.ReadOnly], [], InferenceRecoveryQuality.Unsupported, InferenceUsageQuality.Unknown, InferencePricingQuality.Unknown,
            profiles: [profile], promptTemplates: [prompt], tools: tools);
        tools.Clear();

        var report = manifest.Preflight(requirement);

        report.IsExecutable.Should().BeTrue();
        report.Requirement.Tools.Should().ContainSingle().Which.Should().Be(tool);
        report.Manifest.Tools.Should().ContainSingle().Which.Should().Be(tool);
        report.MissingBindings.Should().BeEmpty();
    }

    [Fact]
    public void Human_and_json_rendering_are_deterministic()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "answer");
        var requirement = new InferenceExecutionRequirement(profile, prompt, null, tools: null,
            hasContextInputs: false, modality: InferenceModality.StructuredText,
            limits: new InferenceLimitSet([new(InferenceLimitDimension.ModelCalls, 2)]));
        var report = Manifest(profile, prompt, null, supportedLimits: [new(InferenceLimitDimension.ModelCalls, 5)]).Preflight(requirement);

        var human = InferencePreflightReportRenderer.RenderHuman(report);
        human.Should().Be(InferencePreflightReportRenderer.ToHumanString(report));
        human.Should().Contain("Inference preflight: executable").And.Contain("- modelCalls: 2");
        var json = InferencePreflightReportRenderer.RenderJson(report);
        json.Should().Be(InferencePreflightReportRenderer.ToJson(report));
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("executable").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("effectiveLimits")[0].GetProperty("maximum").GetInt64().Should().Be(2);
    }

    [Fact]
    public void Workflow_prompt_binding_is_exact_and_write_support_cannot_be_advertised()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "chat");
        var requirement = new InferenceExecutionRequirement(profile, null, "prompt-digest");
        var manifest = new InferenceFeatureManifest(
            [InferencePromptForm.WorkflowOwned], [InferenceModality.StructuredText],
            false, null, [], [], InferenceRecoveryQuality.Unsupported, InferenceUsageQuality.Unknown,
            InferencePricingQuality.Unknown, profiles: [profile], workflowPromptDigests: ["other-digest"]);

        manifest.Preflight(requirement).Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should().ContainSingle().Which.Should().Be(InferencePreflightDiagnosticCode.MissingWorkflowPromptBinding);
        ((Action)(() => new InferenceFeatureManifest(
            [InferencePromptForm.RegisteredTemplate], [InferenceModality.StructuredText],
            false, null, [InferenceToolEffect.ExternalWrite], [], InferenceRecoveryQuality.Unsupported,
            InferenceUsageQuality.Unknown, InferencePricingQuality.Unknown)))
            .Should().Throw<ArgumentException>().WithMessage("*read-only*");
    }

    [Fact]
    public void Legacy_requirement_leaves_modality_unspecified()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "media");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "generate");
        var requirement = new InferenceExecutionRequirement(profile, prompt, null);
        var manifest = new InferenceFeatureManifest(
            [InferencePromptForm.RegisteredTemplate], [InferenceModality.Image],
            false, null, [], [], InferenceRecoveryQuality.Unsupported, InferenceUsageQuality.Unknown,
            InferencePricingQuality.Unknown, profiles: [profile], promptTemplates: [prompt]);

        requirement.Modality.Should().BeNull();
        manifest.Preflight(requirement).IsExecutable.Should().BeTrue();
        InferencePreflightReportRenderer.RenderJson(manifest.Preflight(requirement))
            .Should().Contain("\"modality\":\"unspecified\"");
    }

    private static InferenceFeatureManifest Manifest(DescriptorReference profile, DescriptorReference prompt, DescriptorReference? tool,
        IReadOnlyList<InferenceLimit>? supportedLimits = null) => new(
        [InferencePromptForm.RegisteredTemplate], [InferenceModality.StructuredText],
        false, null, tool is null ? [] : [InferenceToolEffect.ReadOnly], supportedLimits ?? [],
        InferenceRecoveryQuality.Unsupported, InferenceUsageQuality.Unknown, InferencePricingQuality.Unknown,
        profiles: [profile], promptTemplates: [prompt], tools: tool is null ? [] : [tool]);

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
}
