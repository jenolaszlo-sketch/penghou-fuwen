using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Fuwen;
using Penghou.Nuwa;

namespace Penghou.Fuwen.Baize.Tests;

/// <summary>
/// R29 execution wiring through Baize: workflow-owned prompt bindings match
/// by semantic digest, rendered messages reach the model verbatim, and
/// evidence carries both digests. Routed and generation executors fail closed.
/// </summary>
public sealed class BaizePromptExecutionTests
{
    private static PromptDefinition Greet() => new(
        "greet",
        [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
        [
            new PromptMessage(PromptMessageRole.System, "You greet users."),
            new PromptMessage(PromptMessageRole.User, "Greet {{ name }}."),
        ]);

    private static IReadOnlyList<RenderedPromptMessage> Rendered() =>
        PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = RuntimeValue.FromJson(JsonDocument.Parse("\"Alice\"").RootElement),
            });

    private static DescriptorReference Profile() =>
        new(DescriptorKind.InferenceProfile, "profile", "1",
            new ContentDigest("sha256", "test", new string('a', 64)));

    private static ExecutionInvocation Invocation() =>
        new("sha256:fuwen-execution/v8:" + new string('a', 64), "workflow/infer", "run/infer", "1", "sha256:req:" + new string('b', 64));

    private static InferenceExecutionRequest Request(PromptDefinition prompt, IReadOnlyList<RenderedPromptMessage> rendered) =>
        new(Invocation(), Profile(), null, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), prompt, rendered);

    [Fact]
    public async Task Prompt_binding_sends_rendered_messages_and_records_prompt_evidence()
    {
        var definition = Greet();
        var rendered = Rendered();
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var sink = new RecordingSink();
        var executor = new BaizeInferenceExecutor(
            [
                new BaizeInferenceBinding(
                    Profile(),
                    null,
                    [new BaizeEndpointBinding("primary", "provider", "model", client)],
                    prompt: definition),
            ],
            provenanceSink: sink);

        var result = await executor.ExecuteAsync(
            Request(definition, rendered), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        ((JsonRuntimeValue)result.Output!).Value.GetString().Should().Be("hi");
        var texts = client.LastRequest!.Messages
            .SelectMany(message => message.Parts)
            .OfType<LlmTextContent>()
            .Select(part => part.Text)
            .ToArray();
        texts.Should().Equal("You greet users.", "Greet Alice.");
        result.Evidence.Should().NotBeNull();
        result.Evidence!.PromptTemplate.Should().BeNull();
        result.Evidence.PromptDigest.Should().Be(definition.GetSemanticDigest());
        result.Evidence.RenderedPromptDigest.Should().Be(PromptRenderer.GetRenderedDigest(rendered));
        sink.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Prompt_binding_mismatch_fails_closed()
    {
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(),
                null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                prompt: Greet()),
        ]);
        var other = new PromptDefinition(
            "other",
            [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
            [new PromptMessage(PromptMessageRole.User, "Other {{ name }}.")]);
        var otherRendered = PromptRenderer.Render(
            other,
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = RuntimeValue.FromJson(JsonDocument.Parse("\"Alice\"").RootElement),
            });

        var result = await executor.ExecuteAsync(
            Request(other, otherRendered), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Routed_executor_rejects_workflow_owned_prompts()
    {
        var inner = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(),
                null,
                [new BaizeEndpointBinding("primary", "provider", "model", new FakeClient(new LlmResponse("\"hi\"")))],
                prompt: Greet()),
        ]);
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(
                Profile(),
                new DescriptorReference(
                    DescriptorKind.PromptTemplate, "template", "1",
                    new ContentDigest("sha256", "test", new string('b', 64))),
                inner),
        ]);

        var result = await router.ExecuteAsync(
            Request(Greet(), Rendered()), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
        result.Failure.Message.Should().Contain("workflow-owned prompts");
    }

    private sealed class FakeClient : ILlmClient, ILlmCompletionClient
    {
        private readonly Queue<object> responses;
        public FakeClient(string content) => responses = new Queue<object>([new LlmResponse(content)]);
        public FakeClient(params object[] responses) => this.responses = new Queue<object>(responses);
        public int Calls { get; private set; }
        public LlmRequest? LastRequest { get; private set; }
        public LlmEndpointCapabilities Capabilities { get; } = new()
        {
            NativeStructuredOutput = true,
            StructuredOutputViaTool = true,
            NativeToolCalling = true,
            ToolsWithStructuredOutput = true,
            StrictToolArguments = true,
        };
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            var next = responses.Dequeue();
            return next is Exception exception ? Task.FromException<LlmResponse>(exception) : Task.FromResult((LlmResponse)next);
        }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamEvent("");
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingSink : IBaizeInferenceProvenanceSink
    {
        public ConcurrentBag<InferenceExecutionEvidence> Items { get; } = [];
        public ValueTask RecordAsync(InferenceExecutionEvidence evidence, CancellationToken cancellationToken = default)
        {
            Items.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }
}
