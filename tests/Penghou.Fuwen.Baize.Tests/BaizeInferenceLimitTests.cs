using FluentAssertions;
using Penghou.Baize;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Baize.Tests;

/// <summary>
/// Plan-declared inference limits through Baize: maxTokens caps the host
/// policy ceiling, and timeouts fail fast without retry.
/// </summary>
public sealed class BaizeInferenceLimitTests
{
    private static PromptDefinition Greet() => new(
        "greet",
        [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
        [new PromptMessage(PromptMessageRole.User, "Greet {{ name }}.")]);

    private static RuntimeValue Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement.Clone());
    }

    private static IReadOnlyList<RenderedPromptMessage> Rendered() =>
        PromptRenderer.Render(
            Greet(),
            new Dictionary<string, RuntimeValue>(StringComparer.Ordinal)
            {
                ["name"] = Json("\"Alice\""),
            });

    private static DescriptorReference Profile() =>
        new(DescriptorKind.InferenceProfile, "profile", "1",
            new ContentDigest("sha256", "test", new string('a', 64)));

    private static ExecutionInvocation Invocation() =>
        new("sha256:fuwen-execution/v8:" + new string('a', 64), "workflow/infer", "run/infer", "1", "sha256:req:" + new string('b', 64));

    private static InferenceExecutionRequest Request(InferenceLimits? limits) =>
        new(Invocation(), Profile(), null, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), Greet(), Rendered(), null, limits);

    private static BaizeInferenceExecutor Executor(
        ILlmClient client, int? policyTokens = null) => new(
        [
            new BaizeInferenceBinding(
                Profile(),
                null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                policy: policyTokens is null ? null : new BaizeInferencePolicy(maximumTokens: policyTokens),
                prompt: Greet()),
        ]);

    [Fact]
    public async Task Plan_maxTokens_caps_but_never_raises_the_host_ceiling()
    {
        var client = new FakeClient(
            new LlmResponse("\"hi\""), new LlmResponse("\"hi\""), new LlmResponse("\"hi\""));
        var executor = Executor(client, policyTokens: 8000);

        var capped = await executor.ExecuteAsync(
            Request(new InferenceLimits(4000, null)), TestContext.Current.CancellationToken);
        capped.IsSuccess.Should().BeTrue(
            capped.Failure?.Code.ToString() + " " + capped.Failure?.Message +
            " provider=" + capped.Failure?.ProviderCode);
        client.LastRequest!.MaxTokens.Should().Be(4000);

        var raised = await executor.ExecuteAsync(
            Request(new InferenceLimits(16000, null)), TestContext.Current.CancellationToken);
        raised.IsSuccess.Should().BeTrue();
        client.LastRequest!.MaxTokens.Should().Be(8000);

        var unbounded = await executor.ExecuteAsync(
            Request(null), TestContext.Current.CancellationToken);
        unbounded.IsSuccess.Should().BeTrue();
        client.LastRequest!.MaxTokens.Should().Be(8000);
    }

    [Fact]
    public async Task Timeout_fails_fast_without_retry()
    {
        var client = new SlowClient(TimeSpan.FromSeconds(5));
        var executor = Executor(client);

        var result = await executor.ExecuteAsync(
            Request(new InferenceLimits(null, 1)), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.Timeout);
        result.Failure.Message.Should().Contain("1s timeout");
        client.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Generous_timeout_leaves_fast_calls_untouched()
    {
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var executor = Executor(client);

        var result = await executor.ExecuteAsync(
            Request(new InferenceLimits(4000, 60)), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        client.Calls.Should().Be(1);
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

    private sealed class SlowClient(TimeSpan delay) : ILlmClient, ILlmCompletionClient
    {
        public int Calls { get; private set; }
        public LlmEndpointCapabilities Capabilities { get; } = new()
        {
            NativeStructuredOutput = true,
            StructuredOutputViaTool = true,
            NativeToolCalling = true,
            ToolsWithStructuredOutput = true,
            StrictToolArguments = true,
        };
        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(delay, cancellationToken);
            return new LlmResponse("\"hi\"");
        }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamEvent("");
            await Task.CompletedTask;
        }
    }
}
