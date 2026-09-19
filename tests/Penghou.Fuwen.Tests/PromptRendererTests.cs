using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class PromptRendererTests
{
    private static PromptDefinition Greet() => new(
        "greet",
        [
            new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String)),
            new PromptParameter("count", new PrimitiveType(FuwenPrimitiveKind.Integer)),
            new PromptParameter("title", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
        ],
        [
            new PromptMessage(PromptMessageRole.System, "You greet users."),
            new PromptMessage(PromptMessageRole.User, "Greet {{ name }} {{ count }} times{{ title }}."),
        ]);

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement.Clone());
    }

    [Fact]
    public void Render_substitutes_strings_raw_and_other_values_as_canonical_json()
    {
        var rendered = PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Alice\""),
                ["count"] = Json("3"),
            });

        rendered.Select(message => message.Role).Should().Equal(PromptMessageRole.System, PromptMessageRole.User);
        rendered[0].Text.Should().Be("You greet users.");
        rendered[1].Text.Should().Be("Greet Alice 3 times.");
    }

    [Fact]
    public void Render_is_single_pass_over_replacement_text()
    {
        var rendered = PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"{{ count }} Alice\""),
                ["count"] = Json("1"),
            });

        rendered[1].Text.Should().Be("Greet {{ count }} Alice 1 times.");
    }

    [Fact]
    public void Render_rejects_missing_required_values_and_accepts_extra_ones()
    {
        var act = () => PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Alice\""),
                ["unrelated"] = Json("true"),
            });

        act.Should().Throw<ArgumentException>().WithMessage("*count*");
    }

    [Fact]
    public void Rendered_digest_is_stable_and_sensitive()
    {
        var first = PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Alice\""),
                ["count"] = Json("3"),
            });
        var same = PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Alice\""),
                ["count"] = Json("3"),
            });
        var changed = PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Bob\""),
                ["count"] = Json("3"),
            });

        PromptRenderer.GetRenderedDigest(first).Should().Be(PromptRenderer.GetRenderedDigest(same));
        PromptRenderer.GetRenderedDigest(first).Should().NotBe(PromptRenderer.GetRenderedDigest(changed));
        PromptRenderer.GetRenderedDigest(first).Should().MatchRegex(@"^sha256:rendered-prompt/v1:[0-9a-f]{64}$");
    }

    [Fact]
    public void RenderValue_writes_strings_raw_and_canonical_json_otherwise()
    {
        PromptRenderer.RenderValue(Json("\"raw\"")).Should().Be("raw");
        PromptRenderer.RenderValue(Json("true")).Should().Be("true");
        PromptRenderer.RenderValue(Json("42")).Should().Be("42");
        PromptRenderer.RenderValue(Json("{\"b\":1,\"a\":2}")).Should().Be("{\"a\":2,\"b\":1}");
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    private static ExecutionInvocation Invocation() =>
        new("sha256:fuwen-execution/v8:" + new string('a', 64), "workflow/infer", "run/infer", "1", "sha256:req:" + new string('b', 64));

    private static PromptDefinition Definition() => new(
        "greet",
        [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
        [new PromptMessage(PromptMessageRole.User, "Hi {{ name }}.")]);

    private static IReadOnlyList<RenderedPromptMessage> Rendered() =>
        [new RenderedPromptMessage(PromptMessageRole.User, "Hi Alice.")];

    [Fact]
    public void Prompt_requests_require_exactly_one_prompt_source_with_rendered_messages()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "profile");
        var template = Descriptor(DescriptorKind.PromptTemplate, "template");
        var output = new PrimitiveType(FuwenPrimitiveKind.String);

        var prompt = new InferenceExecutionRequest(
            Invocation(), profile, null, [], [], output, Definition(), Rendered());
        prompt.PromptTemplate.Should().BeNull();
        prompt.Prompt.Should().NotBeNull();
        prompt.RenderedPrompt.Should().ContainSingle();

        ((Action)(() => new InferenceExecutionRequest(
            Invocation(), profile, template, [], [], output, Definition(), Rendered())))
            .Should().Throw<ArgumentException>();
        ((Action)(() => new InferenceExecutionRequest(
            Invocation(), profile, null, [], [], output, Definition(), null)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => new InferenceExecutionRequest(
            Invocation(), profile, null, [], [], output)))
            .Should().Throw<ArgumentNullException>();

        var legacy = new InferenceExecutionRequest(
            Invocation(), profile, template, [], [], output);
        legacy.Prompt.Should().BeNull();
        legacy.RenderedPrompt.Should().BeNull();
        legacy.PromptTemplate.Should().NotBeNull();
    }

    [Fact]
    public void Prompt_evidence_requires_exactly_one_source_with_both_digests()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "profile");
        var template = Descriptor(DescriptorKind.PromptTemplate, "template");

        var evidence = new InferenceExecutionEvidence(
            profile, null, [],
            promptDigest: "sha256:prompt-definition/v1:" + new string('a', 64),
            renderedPromptDigest: "sha256:rendered-prompt/v1:" + new string('b', 64));
        evidence.PromptDigest.Should().NotBeNull();
        evidence.RenderedPromptDigest.Should().NotBeNull();
        evidence.PromptTemplate.Should().BeNull();

        ((Action)(() => new InferenceExecutionEvidence(profile, template, [], promptDigest: "x")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => new InferenceExecutionEvidence(
            profile, null, [],
            promptDigest: "sha256:prompt-definition/v1:" + new string('a', 64))))
            .Should().Throw<ArgumentException>();
    }
}
