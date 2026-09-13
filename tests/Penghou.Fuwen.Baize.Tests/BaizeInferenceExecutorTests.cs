using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Baize.Generation;
using Penghou.Fuwen;
using Penghou.Nuwa;

namespace Penghou.Fuwen.Baize.Tests;

public sealed class BaizeInferenceExecutorTests
{
    [Fact]
    public async Task Structured_success_records_provider_model_usage_and_policy_provenance()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "logical-profile");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "logical-prompt");
        var client = new FakeClient(new LlmResponse("\"answer\"", Usage: new LlmUsage(2, 3, 5)));
        var sink = new RecordingSink();
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(profile, prompt, [new BaizeEndpointBinding("primary", "provider-a", "model-a", client)],
                policy: new BaizeInferencePolicy(policyRevision: "policy-7", routingPolicyRevision: "route-4"))],
            provenanceSink: sink);

        var result = await executor.ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        ((JsonRuntimeValue)result.Output!).Value.GetString().Should().Be("answer");
        result.Evidence.Should().NotBeNull();
        result.Evidence!.Profile.Should().Be(profile);
        result.Evidence.PromptTokens.Should().Be(2);
        result.Evidence.CompletionTokens.Should().Be(3);
        result.Evidence.TotalTokens.Should().Be(5);
        result.Evidence.PolicyRevision.Should().Be("policy-7");
        result.Evidence.RoutingPolicyRevision.Should().Be("route-4");
        result.Evidence.Attempts.Should().ContainSingle().Which.Provider.Should().Be("provider-a");
        sink.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Malformed_output_is_typed_and_repair_success_does_not_skip_final_schema_validation()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(new LlmResponse("{\"answer\":\"wrong\""));
        var executor = new BaizeInferenceExecutor([Binding(profile, prompt, client)], new RepairingPipeline("{\"answer\":\"wrong\"}"));

        var result = await executor.ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.RepairedOutputSchemaInvalid);
        result.Evidence!.WasRepaired.Should().BeTrue();
    }

    [Fact]
    public async Task Valid_json_that_fails_the_admitted_type_is_schema_mismatch()
    {
        var (profile, prompt) = Descriptors();
        var executor = new BaizeInferenceExecutor([Binding(profile, prompt, new FakeClient("42"))]);

        var result = await executor.ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.SchemaMismatch);
        result.Evidence!.WasRepaired.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_tool_and_truncation_are_distinct_provider_output_failures()
    {
        var (profile, prompt) = Descriptors();
        var tool = new BaizeToolBinding(Descriptor(DescriptorKind.Tool, "emit"), new LlmTool("emit", "emit", "{\"type\":\"string\"}"));
        var missing = new BaizeInferenceBinding(profile, prompt, [new BaizeEndpointBinding("primary", "p", "m", new FakeClient(new LlmResponse("")))],
            outputMode: BaizeInferenceOutputMode.ToolCall, expectedToolName: "emit", tools: [tool]);
        var truncated = Binding(profile, prompt, new FakeClient(new LlmResponse("\"partial", FinishReason: "length")));

        var missingResult = await new BaizeInferenceExecutor([missing]).ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);
        var truncatedResult = await new BaizeInferenceExecutor([truncated]).ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        missingResult.Failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
        truncatedResult.Failure!.Code.Should().Be(ExecutionFailureCode.TruncatedOutput);
    }

    [Fact]
    public async Task Policy_rejection_happens_before_provider_call()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("\"never\"");
        var policy = new BaizeInferencePolicy(rejection: _ => "policy says no");
        var binding = new BaizeInferenceBinding(profile, prompt, [new BaizeEndpointBinding("primary", "p", "m", client)], policy: policy);

        var result = await new BaizeInferenceExecutor([binding]).ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.PolicyRejected);
        client.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Transient_provider_failure_falls_back_but_permanent_failure_does_not()
    {
        var (profile, prompt) = Descriptors();
        var transient = new FakeClient(new LlmClientException("busy", LlmClientFailureKind.Availability));
        var fallback = new FakeClient(new LlmResponse("\"ok\""));
        var binding = new BaizeInferenceBinding(profile, prompt, [
            new BaizeEndpointBinding("one", "p1", "m1", transient),
            new BaizeEndpointBinding("two", "p2", "m2", fallback)],
            policy: new BaizeInferencePolicy(maximumAttempts: 2));

        var result = await new BaizeInferenceExecutor([binding]).ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Evidence!.Attempts.Should().HaveCount(2);
        result.Evidence.Attempts[0].Succeeded.Should().BeFalse();
        result.Evidence.Attempts[1].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Trusted_representation_retry_is_bounded()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(new LlmResponse("not-json"), new LlmResponse("still-not-json"), new LlmResponse("\"ok\""));
        var binding = Binding(profile, prompt, client, new BaizeInferencePolicy(maximumAttempts: 2, retryRepresentationFailures: true));

        var result = await new BaizeInferenceExecutor([binding], new FailingRepairPipeline()).ExecuteAsync(Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)), TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        result.Evidence!.Attempts.Should().HaveCount(2);
        client.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Valid_json_is_not_sent_through_repair()
    {
        var (profile, prompt) = Descriptors();
        var repair = new CountingRepairPipeline();
        var executor = new BaizeInferenceExecutor(
            [Binding(profile, prompt, new FakeClient("\"ok\""))],
            repair);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        repair.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unexpected_host_exception_is_not_retried()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(
            new InvalidOperationException("host defect"),
            new LlmResponse("\"must-not-run\""));
        var binding = Binding(
            profile,
            prompt,
            client,
            new BaizeInferencePolicy(maximumAttempts: 2));

        var result = await new BaizeInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.ProviderError);
        client.Calls.Should().Be(1);
        result.Evidence!.Attempts.Should().ContainSingle();
    }

    [Fact]
    public void Duplicate_exact_bindings_are_rejected()
    {
        var (profile, prompt) = Descriptors();
        var binding = Binding(profile, prompt, new FakeClient("\"ok\""));

        var act = () => new BaizeInferenceExecutor([binding, binding]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("bindings");
    }

    [Fact]
    public async Task Optional_output_schema_allows_null()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("null");
        var executor = new BaizeInferenceExecutor([Binding(profile, prompt, client)]);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        using var schema = JsonDocument.Parse(client.LastRequest!.ResponseFormat!.Schema!);
        var alternatives = schema.RootElement.GetProperty("anyOf").EnumerateArray().ToArray();
        alternatives.Should().HaveCount(2);
        alternatives[1].GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Binding_identity_validation_matches_evidence_contract()
    {
        var client = new FakeClient("\"unused\"");

        var utf8Act = () => new BaizeEndpointBinding("primary", new string('界', 86), "model", client);
        var controlAct = () => new BaizeInferencePolicy(policyRevision: "policy\nrevision");

        utf8Act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("provider");
        controlAct.Should().Throw<ArgumentException>().WithParameterName("policyRevision");
    }

    [Fact]
    public async Task Oversized_provider_json_is_a_typed_output_failure()
    {
        var (profile, prompt) = Descriptors();
        var oversized = JsonSerializer.Serialize(new string('x', JsonRuntimeValue.MaximumJsonUtf8Bytes));
        var executor = new BaizeInferenceExecutor([
            Binding(profile, prompt, new FakeClient(oversized)),
        ]);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.ProviderOutput);
        result.Failure.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        result.Evidence.Should().NotBeNull();
    }

    [Fact]
    public async Task Structured_success_records_host_priced_cost_without_floating_point_money()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(new LlmResponse("\"priced\"", Usage: new LlmUsage(2, 3, 5)));
        var endpoint = new BaizeEndpointBinding(
            "primary",
            "provider",
            "model",
            client,
            new BaizeTokenPricing("USD", 1_000_000, 2_000_000));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(profile, prompt, [endpoint]),
        ]);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Evidence!.Modality.Should().Be(InferenceModality.StructuredText);
        result.Evidence.Cost.Should().NotBeNull();
        result.Evidence.Cost!.CurrencyCode.Should().Be("USD");
        result.Evidence.Cost.AmountMicrounits.Should().Be(8);
        result.Evidence.Cost.IsEstimated.Should().BeTrue();
    }

    [Fact]
    public async Task Image_generation_publishes_verified_artifact_and_records_modality_and_cost()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new FakeGenerationClient(new GenerationResult(
            [new GeneratedAsset(
                new InlineGeneratedAssetSource(new byte[] { 1, 2 }, "image/png"),
                "image/png",
                "answer.png",
                2,
                null,
                new Dictionary<string, object?>())],
            new Dictionary<string, object?>()));
        var publisher = new FakeGeneratedAssetPublisher();
        var binding = new BaizeGenerationBinding(
            profile,
            prompt,
            artifactDescriptor,
            BaizeGenerationModality.Image,
            "images-primary",
            "provider-images",
            "image-model",
            generation,
            request => new ImageGenerationRequest
            {
                Prompt = "draw " + request.Arguments.Count,
                Count = 1,
                IdempotencyKey = request.Invocation.OperationKey,
            },
            publisher,
            new BaizeGenerationPolicy(policyRevision: "generation-policy/1", routingPolicyRevision: "images/1"),
            _ => new InferenceCostEvidence("USD", 125_000, isEstimated: false));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().BeOfType<ArtifactRuntimeValue>();
        result.Publications.Should().ContainSingle();
        result.Publications[0].IdempotencyKey.Should().Be(Invocation().OperationKey);
        result.Evidence!.Modality.Should().Be(InferenceModality.Image);
        result.Evidence.Cost!.AmountMicrounits.Should().Be(125_000);
        result.Evidence.Cost.IsEstimated.Should().BeFalse();
        generation.Requests.Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be(Invocation().OperationKey);
        publisher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Generation_rejects_unbound_idempotency_before_provider_call()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new FakeGenerationClient(new GenerationResult([], new Dictionary<string, object?>()));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            _ => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = "wrong" },
            new FakeGeneratedAssetPublisher());

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.Contract);
        result.Failure.Code.Should().Be(ExecutionFailureCode.InvalidInput);
        result.Evidence!.Attempts.Should().BeEmpty();
        generation.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Generation_polls_a_pinned_handle_and_reuses_the_operation_key_on_replay()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var result = new GenerationResult(
            [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]);
        var generation = new QueuedGenerationClient(result);
        var publisher = new FakeGeneratedAssetPublisher();
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest
            {
                Prompt = "draw",
                IdempotencyKey = request.Invocation.OperationKey,
            },
            publisher,
            new BaizeGenerationPolicy(pollingInterval: TimeSpan.FromMilliseconds(1)));
        var executor = new BaizeGenerationInferenceExecutor([binding]);
        var request = Request(profile, prompt, new ArtifactType(artifactDescriptor));

        var first = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var replay = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        first.Publications.Should().ContainSingle().Which.Disposition.Should().Be(PublicationDisposition.Created);
        replay.Publications.Should().ContainSingle().Which.Disposition.Should().Be(PublicationDisposition.Replayed);

        generation.SubmittedKeys.Should().Equal(request.Invocation.OperationKey, request.Invocation.OperationKey);
        generation.PolledHandles.Should().OnlyContain(handle => handle.Id == "operation-1");
        generation.PolledHandles.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cost_ceiling_stops_representation_retry_and_retains_attempt_cost()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(
            new LlmResponse("not-json", Usage: new LlmUsage(1, 1, 2)),
            new LlmResponse("\"must-not-run\"", Usage: new LlmUsage(1, 1, 2)));
        var endpoint = new BaizeEndpointBinding(
            "primary", "provider", "model", client,
            new BaizeTokenPricing("USD", 1_000_000, 1_000_000, "prices/7"));
        var binding = new BaizeInferenceBinding(
            profile, prompt, [endpoint],
            policy: new BaizeInferencePolicy(
                maximumAttempts: 2,
                retryRepresentationFailures: true,
                maximumCostMicrounits: 2));

        var result = await new BaizeInferenceExecutor([binding], new FailingRepairPipeline()).ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        client.Calls.Should().Be(1);
        result.Evidence!.Attempts.Should().ContainSingle();
        result.Evidence.Attempts[0].PromptTokens.Should().Be(1);
        result.Evidence.Attempts[0].Cost!.AmountMicrounits.Should().Be(2);
        result.Evidence.Cost!.PricingRevision.Should().Be("prices/7");
    }

    [Fact]
    public void Generation_requires_crash_safe_endpoint_capabilities()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var client = new FakeGenerationClient(
            new GenerationResult([], new Dictionary<string, object?>()),
            GenerationFeature.TextToImage);

        var act = () => new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", client,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher());

        act.Should().Throw<ArgumentException>().WithParameterName("client");
    }

    [Fact]
    public async Task Generation_rejects_publication_receipt_not_bound_to_operation()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var client = new FakeGenerationClient(new GenerationResult(
            [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", client,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher(invalidOperationKey: true));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.PublicationRejected);
        result.Failure.MayHaveCommittedEffect.Should().BeTrue();
        result.Publications.Should().BeEmpty();
        result.Evidence!.Attempts.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Exact_router_composes_structured_and_generation_executors()
    {
        var (profile, prompt) = Descriptors();
        var target = new RecordingInferenceExecutor();
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, target),
        ]);

        var matched = await router.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            TestContext.Current.CancellationToken);
        var unmatched = await router.ExecuteAsync(
            Request(Descriptor(DescriptorKind.InferenceProfile, "other"), prompt,
                new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            TestContext.Current.CancellationToken);

        matched.IsSuccess.Should().BeTrue();
        target.Calls.Should().Be(1);
        unmatched.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
    }

    private static BaizeInferenceBinding Binding(DescriptorReference profile, DescriptorReference prompt, FakeClient client, BaizeInferencePolicy? policy = null) =>
        new(profile, prompt, [new BaizeEndpointBinding("primary", "provider", "model", client)], policy: policy);

    private static InferenceExecutionRequest Request(DescriptorReference profile, DescriptorReference prompt, FuwenType output) =>
        new(Invocation(), profile, prompt, [], [], output);

    private static (DescriptorReference Profile, DescriptorReference Prompt) Descriptors() =>
        (Descriptor(DescriptorKind.InferenceProfile, "profile"), Descriptor(DescriptorKind.PromptTemplate, "prompt"));

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
        new(kind, name, "1", new ContentDigest("sha256", "test", new string('a', 64)));

    private static ExecutionInvocation Invocation() =>
        new("sha256:fuwen-execution/v3:" + new string('a', 64), "workflow/infer", "run/infer", "1", "sha256:req:" + new string('b', 64));

    private sealed class RecordingSink : IBaizeInferenceProvenanceSink
    {
        public ConcurrentBag<InferenceExecutionEvidence> Items { get; } = [];
        public ValueTask RecordAsync(InferenceExecutionEvidence evidence, CancellationToken cancellationToken = default)
        {
            Items.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRepairPipeline : IJsonRepairPipeline
    {
        public ValueTask<JsonRepairResult> RepairAsync(string input, JsonSchemaExpectation? expectation = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(JsonRepairResult.Failure(input, null, [], []));
    }

    private sealed class CountingRepairPipeline : IJsonRepairPipeline
    {
        public int Calls { get; private set; }

        public ValueTask<JsonRepairResult> RepairAsync(
            string input,
            JsonSchemaExpectation? expectation = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(JsonRepairResult.Failure(input, null, [], []));
        }
    }

    private sealed class RepairingPipeline(string repairedText) : IJsonRepairPipeline
    {
        public ValueTask<JsonRepairResult> RepairAsync(string input, JsonSchemaExpectation? expectation = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(JsonRepairResult.Success(JsonNode.Parse(repairedText)!, input, repairedText, true, [], []));
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

    private sealed class FakeGenerationClient : IGenerationClient
    {
        private readonly GenerationOperation completed;

        public FakeGenerationClient(
            GenerationResult result,
            GenerationFeature features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval)
        {
            var handle = new GenerationOperationHandle(
                "provider", "endpoint", "operation", "model", new Dictionary<string, string>());
            completed = new GenerationOperation(
                handle,
                GenerationOperationState.Succeeded,
                result,
                null,
                null,
                new Dictionary<string, object?>());
            Capabilities = new GenerationCapabilities { Features = features };
        }

        public List<GenerationRequest> Requests { get; } = [];
        public GenerationCapabilities Capabilities { get; }

        public Task<GenerationOperation> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(completed);
        }

        public Task<GenerationOperation> GetAsync(
            GenerationOperationHandle handle,
            CancellationToken cancellationToken = default) => Task.FromResult(completed);

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle handle,
            CancellationToken cancellationToken = default) => Task.FromResult(completed);
    }

    private sealed class FakeGeneratedAssetPublisher(bool invalidOperationKey = false) : IBaizeGeneratedAssetPublisher
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<ArtifactPublicationReceipt>> PublishAsync(
            InferenceExecutionRequest request,
            IReadOnlyList<GeneratedAsset> assets,
            DescriptorReference artifactDescriptor,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<ArtifactPublicationReceipt> receipts = assets.Select((asset, index) =>
                new ArtifactPublicationReceipt(
                    invalidOperationKey ? "wrong-operation" : request.Invocation.OperationKey,
                    new ArtifactReference(
                        "generated-store",
                        $"image-{index}",
                        artifactDescriptor,
                        new ContentDigest("sha256", "content/v1", new string((char)('b' + index), 64)),
                        asset.Size,
                        asset.FileName),
                    $"receipt-{index}",
                    Calls == 1 ? PublicationDisposition.Created : PublicationDisposition.Replayed)).ToArray();
            return ValueTask.FromResult(receipts);
        }
    }

    private sealed class QueuedGenerationClient(GenerationResult result) : IGenerationClient
    {
        private readonly GenerationOperationHandle handle = new(
            "provider", "endpoint", "operation-1", "model", new Dictionary<string, string>());

        public List<string?> SubmittedKeys { get; } = [];
        public List<GenerationOperationHandle> PolledHandles { get; } = [];
        public GenerationCapabilities Capabilities { get; } = new()
        {
            Features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
        };

        public Task<GenerationOperation> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            SubmittedKeys.Add(request.IdempotencyKey);
            return Task.FromResult(new GenerationOperation(
                handle, GenerationOperationState.Queued, ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> GetAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            PolledHandles.Add(operationHandle);
            return Task.FromResult(new GenerationOperation(
                handle, GenerationOperationState.Succeeded, result,
                ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                handle,
                GenerationOperationState.Canceled,
                Error: new GenerationError(GenerationErrorKind.Canceled, "cancelled"),
                ProviderMetadata: new Dictionary<string, object?>()));
    }

    private sealed class RecordingInferenceExecutor : IInferenceExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement(true))));
        }
    }
}
