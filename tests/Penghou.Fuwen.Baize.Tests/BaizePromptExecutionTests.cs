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

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
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

    private static DescriptorReference ToolDescriptor(string name, char digest) =>
        new(DescriptorKind.Tool, name, "1",
            new ContentDigest("sha256", "descriptor/v1", new string(digest, 64)));

    private static BaizeToolBinding ToolBinding(string name, char digest) =>
        new(ToolDescriptor(name, digest), new LlmTool(name, name, "{\"type\":\"string\"}"));

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
    public async Task Workflow_owned_prompt_delivers_canonical_context_and_records_snapshot_evidence()
    {
        var definition = Greet();
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var contextProvider = new DescriptorReference(
            DescriptorKind.ContextProvider, "research", "1",
            new ContentDigest("sha256", "test", new string('c', 64)));
        var snapshot = Snapshot(
            contextProvider,
            "snapshot-redacted",
            new ContextSnapshotBudgetEvidence(true, null, 2048, null, 4096));
        var request = new InferenceExecutionRequest(
            Invocation(), Profile(), null, [],
            [new InferenceContextInput(
                "research",
                new PrimitiveType(FuwenPrimitiveKind.Json),
                Json("{\"uniqueFact\":\"orchids bloom at night\",\"secret\":\"[REDACTED]\"}"),
                snapshot)],
            new PrimitiveType(FuwenPrimitiveKind.String), definition, Rendered());
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(), null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                prompt: definition,
                contextDeliveryPolicy: new BaizeContextDeliveryPolicy("context-map/2", 1024)),
        ]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var text = client.LastRequest!.Messages[^1].Parts.OfType<LlmTextContent>().Single().Text;
        text.Should().Contain("\"uniqueFact\":\"orchids bloom at night\"")
            .And.Contain("\"secret\":\"[REDACTED]\"")
            .And.NotContain("raw-secret");
        result.Evidence!.ContextInputs.Should().ContainSingle();
        result.Evidence.ContextInputs[0].Name.Should().Be("research");
        result.Evidence.ContextInputs[0].ContextSnapshot.SnapshotId.Should().Be("snapshot-redacted");
        result.Evidence.ContextInputs[0].ContextSnapshot.Budget.WasTruncated.Should().BeTrue();
        result.Evidence.ContextDeliveryPolicyRevision.Should().Be("context-map/2");
        result.Evidence.ContextPayloadDigest!.Contract.Should().Be("fuwen-context-payload/v1");
        result.Evidence.ContextPayloadUtf8Bytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Workflow_owned_prompt_rejects_unmapped_or_oversized_context_before_provider_call()
    {
        var definition = Greet();
        var provider = new DescriptorReference(
            DescriptorKind.ContextProvider, "research", "1",
            new ContentDigest("sha256", "test", new string('c', 64)));
        var context = new InferenceContextInput(
            "research",
            new PrimitiveType(FuwenPrimitiveKind.String),
            Json("\"a value that exceeds the tiny policy\""),
            Snapshot(provider, "snapshot-1"));
        var unmappedClient = new FakeClient(new LlmResponse("\"never\""));
        var oversizedClient = new FakeClient(new LlmResponse("\"never\""));
        var unmapped = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(), null,
                [new BaizeEndpointBinding("primary", "provider", "model", unmappedClient)],
                prompt: definition),
        ]);
        var oversized = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(), null,
                [new BaizeEndpointBinding("primary", "provider", "model", oversizedClient)],
                prompt: definition,
                contextDeliveryPolicy: new BaizeContextDeliveryPolicy("context-map/1", 8)),
        ]);
        var request = new InferenceExecutionRequest(
            Invocation(), Profile(), null, [], [context],
            new PrimitiveType(FuwenPrimitiveKind.String), definition, Rendered());

        var unmappedResult = await unmapped.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var oversizedResult = await oversized.ExecuteAsync(request, TestContext.Current.CancellationToken);

        unmappedResult.Failure!.Code.Should().Be(ExecutionFailureCode.PolicyRejected);
        oversizedResult.Failure!.Code.Should().Be(ExecutionFailureCode.PolicyRejected);
        oversizedResult.Failure.Message.Should().Contain("never silently truncated");
        unmappedClient.Calls.Should().Be(0);
        oversizedClient.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Artifact_context_exposes_only_its_detached_identity()
    {
        var definition = Greet();
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var provider = new DescriptorReference(
            DescriptorKind.ContextProvider, "assets", "1",
            new ContentDigest("sha256", "test", new string('c', 64)));
        var artifactDescriptor = new DescriptorReference(
            DescriptorKind.Artifact, "document", "1",
            new ContentDigest("sha256", "test", new string('d', 64)));
        var artifact = new ArtifactReference(
            "artifact-store", "artifact-42", artifactDescriptor,
            new ContentDigest("sha256", "artifact/v1", new string('e', 64)),
            123, "report.pdf");
        var request = new InferenceExecutionRequest(
            Invocation(), Profile(), null, [],
            [new InferenceContextInput(
                "document",
                new ArtifactType(artifactDescriptor),
                RuntimeValue.FromArtifact(artifact),
                Snapshot(provider, "snapshot-artifact"))],
            new PrimitiveType(FuwenPrimitiveKind.String), definition, Rendered());
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(), null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                prompt: definition,
                contextDeliveryPolicy: new BaizeContextDeliveryPolicy("context-map/1")),
        ]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var text = client.LastRequest!.Messages[^1].Parts.OfType<LlmTextContent>().Single().Text;
        text.Should().Contain("artifact-42").And.Contain("artifact-store").And.Contain("report.pdf");
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
                ["name"] = Json("\"Alice\""),
            });

        var result = await executor.ExecuteAsync(
            Request(other, otherRendered), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Declared_tools_bound_the_model_call_and_appear_in_evidence()
    {
        var definition = Greet();
        var rendered = Rendered();
        var search = ToolDescriptor("search", 'c');
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(),
                null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                prompt: definition,
                tools: [ToolBinding("search", 'c'), ToolBinding("read", 'd')]),
        ]);
        var request = new InferenceExecutionRequest(
            Invocation(), Profile(), null, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), definition, rendered,
            [search]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        client.LastRequest!.Tools.Should().ContainSingle().Which.Name.Should().Be("search");
        result.Evidence!.AdmittedTools.Should().ContainSingle().Which.Should().Be(search);
    }

    [Fact]
    public async Task Declared_tool_missing_from_binding_fails_closed()
    {
        var definition = Greet();
        var rendered = Rendered();
        var client = new FakeClient(new LlmResponse("\"hi\""));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                Profile(),
                null,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                prompt: definition,
                tools: [ToolBinding("search", 'c')]),
        ]);
        var request = new InferenceExecutionRequest(
            Invocation(), Profile(), null, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), definition, rendered,
            [ToolDescriptor("read", 'd')]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

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

    private static ContextSnapshotReference Snapshot(
        DescriptorReference provider,
        string snapshotId,
        ContextSnapshotBudgetEvidence? budget = null) =>
        new(
            provider,
            snapshotId,
            new ContentDigest("sha256", "request/v1", new string('e', 64)),
            new ContentDigest("sha256", "content/v1", new string('f', 64)),
            [],
            "redaction/3",
            budget ?? new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UnixEpoch.AddDays(1));

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
