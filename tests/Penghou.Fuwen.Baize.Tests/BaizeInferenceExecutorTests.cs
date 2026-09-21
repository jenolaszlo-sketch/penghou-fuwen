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
    public async Task Prompt_placeholders_are_expanded_once_without_rescanning_argument_or_context_json()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(new LlmResponse("\"answer\""));
        var contextProvider = Descriptor(DescriptorKind.ContextProvider, "context-provider");
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [new BaizeEndpointBinding("primary", "provider", "model", client)],
            userPromptTemplate: "A {arguments} B {context} C {context}",
            contextDeliveryPolicy: new BaizeContextDeliveryPolicy("context/1"));
        var request = Request(
            profile,
            prompt,
            new PrimitiveType(FuwenPrimitiveKind.String),
            [new RuntimeArgument("arg", RuntimeValue.FromJson(JsonSerializer.SerializeToElement("literal {context}")))],
            [new InferenceContextInput(
                "ctx",
                new PrimitiveType(FuwenPrimitiveKind.String),
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement("literal {arguments}")),
                Snapshot(contextProvider))]);

        var result = await new BaizeInferenceExecutor([binding]).ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        client.LastRequest!.Messages.Should().ContainSingle();
        client.LastRequest.Messages[0].Parts.Should().ContainSingle();
        client.LastRequest.Messages[0].Parts[0].Should().BeOfType<LlmTextContent>().Which.Text.Should()
            .Be("A {\"arg\":\"literal {context}\"} B {\"ctx\":\"literal {arguments}\"} C {\"ctx\":\"literal {arguments}\"}");
    }

    [Fact]
    public void Registered_template_context_requires_an_explicit_placeholder_at_preflight()
    {
        var (profile, prompt) = Descriptors();
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                profile,
                prompt,
                [new BaizeEndpointBinding(
                    "primary", "provider", "model", new FakeClient(new LlmResponse("\"never\"")))],
                contextDeliveryPolicy: new BaizeContextDeliveryPolicy("context/1")),
        ]);

        var failure = executor.Preflight(new InferenceExecutionRequirement(
            profile, prompt, null, hasContextInputs: true));

        failure!.Code.Should().Be(ExecutionFailureCode.PolicyRejected);
        failure.Message.Should().Contain("{context} placeholder");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("subset")]
    [InlineData("full")]
    public async Task Registered_template_exposes_only_explicit_tools_and_evidence_matches_provider_request(string mode)
    {
        var (profile, prompt) = Descriptors();
        var search = Descriptor(DescriptorKind.Tool, "search");
        var write = Descriptor(DescriptorKind.Tool, "write");
        var boundTools = new[]
        {
            new BaizeToolBinding(search, new LlmTool("search", "Search", "{\"type\":\"object\"}")),
            new BaizeToolBinding(write, new LlmTool("write", "Write", "{\"type\":\"object\"}")),
        };
        IReadOnlyList<DescriptorReference> declared = mode switch
        {
            "none" => [],
            "subset" => [search],
            "full" => [search, write],
            _ => throw new InvalidOperationException("Unknown test case."),
        };
        var client = new FakeClient(new LlmResponse("\"answer\""));
        var sink = new RecordingSink();
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                profile,
                prompt,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                tools: boundTools),
        ], provenanceSink: sink);
        var request = new InferenceExecutionRequest(
            Invocation(), profile, prompt, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), tools: declared);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        client.LastRequest!.Tools.Select(tool => tool.Name)
            .Should().Equal(declared.Select(tool => tool.Name));
        result.Evidence!.AdmittedTools.Should().NotBeNull();
        result.Evidence.AdmittedTools!.Should().Equal(declared);
        sink.Items.Should().ContainSingle();
        sink.Items.Single().AdmittedTools.Should().Equal(declared);
    }

    [Fact]
    public async Task Registered_template_missing_declared_tool_fails_before_provider_call_with_evidence()
    {
        var (profile, prompt) = Descriptors();
        var search = Descriptor(DescriptorKind.Tool, "search");
        var missing = Descriptor(DescriptorKind.Tool, "missing");
        var client = new FakeClient(new LlmResponse("\"never\""));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                profile,
                prompt,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                tools: [new BaizeToolBinding(search, new LlmTool("search", "Search", "{\"type\":\"object\"}"))]),
        ]);
        var request = new InferenceExecutionRequest(
            Invocation(), profile, prompt, [], [],
            new PrimitiveType(FuwenPrimitiveKind.String), tools: [missing]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
        client.Calls.Should().Be(0);
        result.Evidence!.AdmittedTools.Should().ContainSingle().Which.Should().Be(missing);
        result.Evidence.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_template_defaults_remain_model_visible_and_are_recorded_in_evidence()
    {
        var (profile, prompt) = Descriptors();
        var search = Descriptor(DescriptorKind.Tool, "search");
        var client = new FakeClient(new LlmResponse("\"answer\""));
        var executor = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                profile,
                prompt,
                [new BaizeEndpointBinding("primary", "provider", "model", client)],
                tools: [new BaizeToolBinding(search, new LlmTool("search", "Search", "{\"type\":\"object\"}"))]),
        ]);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        client.LastRequest!.Tools.Should().ContainSingle().Which.Name.Should().Be("search");
        result.Evidence!.AdmittedTools.Should().ContainSingle().Which.Should().Be(search);
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
    public async Task Raw_output_budget_is_enforced_before_parsing_or_repair()
    {
        var (profile, prompt) = Descriptors();
        var repair = new CountingRepairPipeline();
        var policy = new BaizeInferencePolicy(maximumResponseUtf8Bytes: 32);
        var executor = new BaizeInferenceExecutor([
            Binding(profile, prompt, new FakeClient(JsonSerializer.Serialize(new string('x', 64))), policy),
        ], repair);

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.ProviderOutput);
        result.Failure.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        result.Failure.Message.Should().Contain("32-byte");
        repair.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Repaired_output_budget_is_enforced_before_runtime_value_creation()
    {
        var (profile, prompt) = Descriptors();
        var policy = new BaizeInferencePolicy(maximumResponseUtf8Bytes: 32);
        var executor = new BaizeInferenceExecutor([
            Binding(profile, prompt, new FakeClient("not-json"), policy),
        ], new RepairingPipeline(JsonSerializer.Serialize(new string('x', 64))));

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.ProviderOutput);
        result.Failure.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        result.Failure.Message.Should().Contain("repaired Baize response");
        result.Evidence!.WasRepaired.Should().BeTrue();
    }

    [Fact]
    public async Task Repair_pipeline_contract_failure_is_typed_as_provider_output()
    {
        var (profile, prompt) = Descriptors();
        var executor = new BaizeInferenceExecutor([
            Binding(profile, prompt, new FakeClient("not-json")),
        ], new ThrowingRepairPipeline());

        var result = await executor.ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.ProviderOutput);
        result.Failure.Code.Should().Be(ExecutionFailureCode.MalformedOutput);
        result.Failure.Message.Should().Contain("repair pipeline failed its contract");
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
            new Dictionary<string, object?>()),
            provider: "provider-images",
            endpointId: "images-primary",
            model: "image-model");
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
    public void Generation_manifest_advertises_only_exact_media_bindings_and_bounded_capabilities()
    {
        var imageProfile = Descriptor(DescriptorKind.InferenceProfile, "image-profile");
        var videoProfile = Descriptor(DescriptorKind.InferenceProfile, "video-profile");
        var audioProfile = Descriptor(DescriptorKind.InferenceProfile, "audio-profile");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "media-prompt");
        static DescriptorReference Artifact(string name) => Descriptor(DescriptorKind.Artifact, name);
        static BaizeGenerationBinding Binding(
            DescriptorReference profile,
            DescriptorReference prompt,
            DescriptorReference artifact,
            BaizeGenerationModality modality,
            TimeSpan timeout) => new(
                profile,
                prompt,
                artifact,
                modality,
                "endpoint",
                "provider",
                "model",
                new FakeGenerationClient(new GenerationResult([])),
                request => new ImageGenerationRequest { Prompt = "unused", IdempotencyKey = request.Invocation.OperationKey },
                new FakeGeneratedAssetPublisher(),
                new BaizeGenerationPolicy(timeout: timeout));

        var executor = new BaizeGenerationInferenceExecutor([
            Binding(imageProfile, prompt, Artifact("image"), BaizeGenerationModality.Image, TimeSpan.FromSeconds(5)),
            Binding(videoProfile, prompt, Artifact("video"), BaizeGenerationModality.Video, TimeSpan.FromSeconds(10)),
            Binding(audioProfile, prompt, Artifact("audio"), BaizeGenerationModality.Audio, TimeSpan.FromSeconds(15)),
        ]);

        executor.FeatureManifest.SupportedModalities.Should().BeEquivalentTo(
            [InferenceModality.Image, InferenceModality.Video, InferenceModality.Audio]);
        executor.FeatureManifest.SupportedPromptForms.Should().ContainSingle()
            .Which.Should().Be(InferencePromptForm.RegisteredTemplate);
        executor.FeatureManifest.SupportedLimits.GetMaximum(InferenceLimitDimension.Turns).Should().Be(1);
        executor.FeatureManifest.SupportedLimits.GetMaximum(InferenceLimitDimension.ModelCalls).Should().Be(1);
        executor.FeatureManifest.SupportedLimits.GetMaximum(InferenceLimitDimension.DurationMilliseconds).Should().Be(15_000);
        executor.FeatureManifest.SupportsContextDelivery.Should().BeFalse();
        executor.FeatureManifest.Tools.Should().BeEmpty();
        executor.FeatureManifest.RecoveryQuality.Should().Be(InferenceRecoveryQuality.Unsupported);
        executor.FeatureManifest.UsageQuality.Should().Be(InferenceUsageQuality.Unknown);
        executor.FeatureManifest.PricingQuality.Should().Be(InferencePricingQuality.Unknown);
        executor.FeatureManifest.Profiles.Should().BeEquivalentTo([imageProfile, videoProfile, audioProfile]);
    }

    [Fact]
    public async Task Generation_preflight_rejects_context_before_provider_submission()
    {
        var (profile, prompt) = Descriptors();
        var artifact = Descriptor(DescriptorKind.Artifact, "generated-image");
        var client = new FakeGenerationClient(new GenerationResult([]));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifact, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", client,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher());
        var executor = new BaizeGenerationInferenceExecutor([binding]);
        var contextProvider = Descriptor(DescriptorKind.ContextProvider, "context-provider");
        var request = Request(
            profile,
            prompt,
            new ArtifactType(artifact),
            [],
            [new InferenceContextInput(
                "subject",
                new PrimitiveType(FuwenPrimitiveKind.String),
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement("value")),
                Snapshot(contextProvider))]);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Kind.Should().Be(ExecutionFailureKind.Admission);
        result.Failure.Code.Should().Be(ExecutionFailureCode.PolicyRejected);
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Generation_preserves_asset_order_in_receipts_and_list_output()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new FakeGenerationClient(new GenerationResult(
            [
                new GeneratedAsset(new ProviderGeneratedAssetSource("first", "provider"), "image/png", "first.png"),
                new GeneratedAsset(new ProviderGeneratedAssetSource("second", "provider"), "image/png", "second.png"),
            ]));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest
            {
                Prompt = "draw two",
                Count = 2,
                IdempotencyKey = request.Invocation.OperationKey,
            },
            new FakeGeneratedAssetPublisher(),
            new BaizeGenerationPolicy(maximumAssets: 2));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ListType(new ArtifactType(artifactDescriptor), 2)),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Publications.Select(item => item.Artifact.ArtifactId)
            .Should().Equal("image-0", "image-1");
        result.Output.Should().BeOfType<ListRuntimeValue>().Which.Items
            .Select(item => ((ArtifactRuntimeValue)item).Artifact.ArtifactId)
            .Should().Equal("image-0", "image-1");
    }

    [Fact]
    public async Task Generation_uses_plan_deadline_when_it_is_stricter_than_host_policy()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new DelayedGenerationClient(submissionDelay: TimeSpan.FromSeconds(5));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher(),
            new BaizeGenerationPolicy(
                pollingInterval: TimeSpan.FromMilliseconds(1),
                timeout: TimeSpan.FromSeconds(5)));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(null, 1)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.Timeout);
        result.Failure.ProviderCode.Should().Be("PlanDeadlineExceeded");
        result.Failure.MayHaveCommittedEffect.Should().BeTrue();
        result.Failure.RetryDisposition.Should().Be(ExecutionRetryDisposition.Never);
        generation.SubmissionCalls.Should().Be(1);
        generation.PollingCalls.Should().Be(0);
    }

    [Fact]
    public async Task Generation_uses_host_deadline_when_it_is_stricter_than_plan()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new DelayedGenerationClient(
            pollingDelay: TimeSpan.FromSeconds(5),
            initiallyQueued: true);
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher(),
            new BaizeGenerationPolicy(
                pollingInterval: TimeSpan.FromMilliseconds(1),
                timeout: TimeSpan.FromMilliseconds(50)));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(null, 5)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.Timeout);
        result.Failure.ProviderCode.Should().Be("HostDeadlineExceeded");
        result.Failure.MayHaveCommittedEffect.Should().BeTrue();
        result.Failure.RetryDisposition.Should().Be(ExecutionRetryDisposition.Never);
        generation.SubmissionCalls.Should().Be(1);
        generation.PollingCalls.Should().Be(1);
    }

    [Fact]
    public async Task Generation_deadline_covers_durable_publication()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new DelayedGenerationClient();
        var publisher = new DelayedGeneratedAssetPublisher(TimeSpan.FromSeconds(5));
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            publisher,
            new BaizeGenerationPolicy(
                pollingInterval: TimeSpan.FromMilliseconds(1),
                timeout: TimeSpan.FromMilliseconds(50)));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(null, 5)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.Timeout);
        result.Failure.ProviderCode.Should().Be("HostDeadlineExceeded");
        result.Failure.MayHaveCommittedEffect.Should().BeTrue();
        result.Failure.RetryDisposition.Should().Be(ExecutionRetryDisposition.Never);
        result.Evidence!.Attempts.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        publisher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Generation_rejects_unsupported_token_limit_before_provider_call()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new DelayedGenerationClient();
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher());

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(100, null)),
            TestContext.Current.CancellationToken);

        result.Failure!.Kind.Should().Be(ExecutionFailureKind.Contract);
        result.Failure.Code.Should().Be(ExecutionFailureCode.InvalidInput);
        result.Failure.ProviderCode.Should().Be("UnsupportedTokenLimit");
        result.Evidence!.Attempts.Should().BeEmpty();
        generation.SubmissionCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_preserves_caller_cancellation_instead_of_reporting_local_timeout(bool duringPolling)
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new DelayedGenerationClient(
            submissionDelay: duringPolling ? null : TimeSpan.FromSeconds(5),
            pollingDelay: duringPolling ? TimeSpan.FromSeconds(5) : null,
            initiallyQueued: duringPolling);
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher(),
            new BaizeGenerationPolicy(
                pollingInterval: TimeSpan.FromMilliseconds(1),
                timeout: TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(null, 5)),
            cancellation.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        generation.SubmissionCalls.Should().Be(1);
        generation.PollingCalls.Should().Be(duringPolling ? 1 : 0);
    }

    [Fact]
    public async Task Generation_keeps_provider_timeout_distinct_from_local_deadline()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new TerminalGenerationClient(GenerationErrorKind.TimeoutExceeded);
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
            new FakeGeneratedAssetPublisher(),
            new BaizeGenerationPolicy(timeout: TimeSpan.FromSeconds(5)));

        var result = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor), new InferenceLimits(null, 5)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.Timeout);
        result.Failure.ProviderCode.Should().Be(GenerationErrorKind.TimeoutExceeded.ToString());
        result.Failure.MayHaveCommittedEffect.Should().BeTrue();
        result.Failure.RetryDisposition.Should().Be(ExecutionRetryDisposition.Never);
    }

    [Fact]
    public async Task Generation_can_resume_a_partial_publication_with_the_same_operation_identity()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var generation = new FakeGenerationClient(new GenerationResult(
            [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]));
        var publisher = new ResumeAfterPartialPublisher();
        var binding = new BaizeGenerationBinding(
            profile, prompt, artifactDescriptor, BaizeGenerationModality.Image,
            "endpoint", "provider", "model", generation,
            request => new ImageGenerationRequest
            {
                Prompt = "draw",
                IdempotencyKey = request.Invocation.OperationKey,
            },
            publisher);
        var executor = new BaizeGenerationInferenceExecutor([binding]);
        var request = Request(profile, prompt, new ArtifactType(artifactDescriptor));

        var interrupted = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);
        var resumed = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        interrupted.Failure!.Code.Should().Be(ExecutionFailureCode.PublicationRejected);
        interrupted.Failure.MayHaveCommittedEffect.Should().BeTrue();
        resumed.IsSuccess.Should().BeTrue();
        resumed.Publications.Should().ContainSingle()
            .Which.Disposition.Should().Be(PublicationDisposition.Replayed);
        publisher.OperationKeys.Should().Equal(
            request.Invocation.OperationKey,
            request.Invocation.OperationKey);
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
    public async Task Generation_rejects_a_polled_handle_with_different_operation_identity()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var result = new GenerationResult(
            [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]);
        var generation = new RedirectedGenerationClient(result);
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

        var execution = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor)),
            TestContext.Current.CancellationToken);

        execution.Failure!.Code.Should().Be(ExecutionFailureCode.ProviderError);
        execution.Failure.MayHaveCommittedEffect.Should().BeTrue();
        execution.Evidence!.Attempts.Should().ContainSingle()
            .Which.Succeeded.Should().BeFalse();
        generation.PolledHandles.Should().ContainSingle();
        publisher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Generation_rejects_a_submission_handle_outside_the_configured_route()
    {
        var (profile, prompt) = Descriptors();
        var artifactDescriptor = Descriptor(DescriptorKind.Artifact, "generated-image");
        var result = new GenerationResult(
            [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]);
        var generation = new RedirectedGenerationClient(result, redirectSubmission: true);
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

        var execution = await new BaizeGenerationInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new ArtifactType(artifactDescriptor)),
            TestContext.Current.CancellationToken);

        execution.Failure!.Code.Should().Be(ExecutionFailureCode.ProviderError);
        execution.Failure.MayHaveCommittedEffect.Should().BeTrue();
        generation.PolledHandles.Should().BeEmpty();
        publisher.Calls.Should().Be(0);
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

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1, null, null)]
    [InlineData(null, 1, null)]
    public async Task Cost_ceiling_stops_representation_retry_when_billable_usage_is_missing_or_partial(
        int? promptTokens,
        int? completionTokens,
        int? totalTokens)
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(
            new LlmResponse("not-json", Usage: new LlmUsage(promptTokens, completionTokens, totalTokens)),
            new LlmResponse("\"must-not-run\"", Usage: new LlmUsage(1, 1, 2)));
        var endpoint = new BaizeEndpointBinding(
            "primary", "provider", "model", client,
            new BaizeTokenPricing("USD", 1_000_000, 1_000_000));
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [endpoint],
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
        result.Evidence.Cost.Should().BeNull();
        result.Evidence.Attempts[0].Cost.Should().BeNull();
    }

    [Fact]
    public async Task Cost_ceiling_does_not_fallback_after_a_priced_call_with_unavailable_usage()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient(
            new LlmClientException("busy", LlmClientFailureKind.Availability),
            new LlmResponse("\"must-not-run\"", Usage: new LlmUsage(1, 1, 2)));
        var endpoint = new BaizeEndpointBinding(
            "primary", "provider", "model", client,
            new BaizeTokenPricing("USD", 1_000_000, 1_000_000));
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [endpoint],
            policy: new BaizeInferencePolicy(maximumAttempts: 2, maximumCostMicrounits: 2));

        var result = await new BaizeInferenceExecutor([binding]).ExecuteAsync(
            Request(profile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);

        result.Failure!.Code.Should().Be(ExecutionFailureCode.ProviderError);
        client.Calls.Should().Be(1);
        result.Evidence!.Cost.Should().BeNull();
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

    [Fact]
    public void Exact_router_preflight_rejects_an_unbound_registered_prompt()
    {
        var (profile, prompt) = Descriptors();
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, new RecordingInferenceExecutor()),
        ]);
        var changedPrompt = Descriptor(DescriptorKind.PromptTemplate, "changed-prompt");

        var matched = router.Preflight(new InferenceExecutionRequirement(profile, prompt, null));
        var unmatched = router.Preflight(new InferenceExecutionRequirement(profile, changedPrompt, null));

        matched.Should().BeNull();
        unmatched!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
    }

    [Fact]
    public void Exact_router_detailed_preflight_delegates_to_the_selected_route_manifest()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("\"must-not-run\"");
        var target = new BaizeInferenceExecutor([Binding(profile, prompt, client)]);
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, target),
        ]);

        var report = router.PreflightDetailed(new InferenceExecutionRequirement(profile, prompt, null));

        report.IsExecutable.Should().BeTrue();
        report.Manifest.Profiles.Should().ContainSingle().Which.Should().Be(profile);
        report.Manifest.PromptTemplates.Should().ContainSingle().Which.Should().Be(prompt);
    }

    [Fact]
    public void Exact_router_preserves_legacy_media_modality_and_rejects_an_explicit_mismatch()
    {
        var (profile, prompt) = Descriptors();
        var artifact = Descriptor(DescriptorKind.Artifact, "generated-image");
        var client = new FakeGenerationClient(new GenerationResult([]));
        var generation = new BaizeGenerationInferenceExecutor([
            new BaizeGenerationBinding(
                profile, prompt, artifact, BaizeGenerationModality.Image,
                "endpoint", "provider", "model", client,
                request => new ImageGenerationRequest { Prompt = "draw", IdempotencyKey = request.Invocation.OperationKey },
                new FakeGeneratedAssetPublisher()),
        ]);
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, generation),
        ]);

        var legacy = router.PreflightDetailed(new InferenceExecutionRequirement(profile, prompt, null));
        var mismatched = router.PreflightDetailed(new InferenceExecutionRequirement(
            profile, prompt, null, tools: null, hasContextInputs: false, modality: InferenceModality.Video));

        legacy.IsExecutable.Should().BeTrue();
        legacy.Requirement.Modality.Should().BeNull();
        mismatched.IsExecutable.Should().BeFalse();
        mismatched.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Contain(InferencePreflightDiagnosticCode.UnsupportedModality);
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Exact_router_detailed_preflight_does_not_form_a_cross_route_cartesian_union()
    {
        var (profile, prompt) = Descriptors();
        var otherProfile = Descriptor(DescriptorKind.InferenceProfile, "other-profile");
        var otherPrompt = Descriptor(DescriptorKind.PromptTemplate, "other-prompt");
        var firstClient = new FakeClient("\"must-not-run\"");
        var secondClient = new FakeClient("\"must-not-run\"");
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt,
                new BaizeInferenceExecutor([Binding(profile, prompt, firstClient)])),
            new BaizeInferenceRoute(otherProfile, otherPrompt,
                new BaizeInferenceExecutor([Binding(otherProfile, otherPrompt, secondClient)])),
        ]);

        var report = router.PreflightDetailed(new InferenceExecutionRequirement(profile, otherPrompt, null));

        report.IsExecutable.Should().BeFalse();
        report.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Contain(InferencePreflightDiagnosticCode.MissingProfileBinding);
        report.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Contain(InferencePreflightDiagnosticCode.MissingPromptTemplateBinding);
        firstClient.Calls.Should().Be(0);
        secondClient.Calls.Should().Be(0);
    }

    [Fact]
    public void Exact_router_detailed_preflight_fails_closed_for_a_legacy_route()
    {
        var (profile, prompt) = Descriptors();
        var target = new RecordingInferenceExecutor();
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, target),
        ]);

        var report = router.PreflightDetailed(new InferenceExecutionRequirement(profile, prompt, null));

        report.IsExecutable.Should().BeFalse();
        report.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Contain(InferencePreflightDiagnosticCode.MissingProfileBinding);
        target.Calls.Should().Be(0);
    }

    [Fact]
    public void Exact_router_detailed_preflight_does_no_provider_work()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("\"must-not-run\"");
        var target = new BaizeInferenceExecutor([Binding(profile, prompt, client)]);
        var router = new BaizeRoutedInferenceExecutor([
            new BaizeInferenceRoute(profile, prompt, target),
        ]);

        _ = router.PreflightDetailed(new InferenceExecutionRequirement(profile, prompt, null));

        client.Calls.Should().Be(0);
    }

    [Fact]
    public void Baize_manifest_advertises_exact_one_call_capabilities()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("\"ok\"");
        var executor = new BaizeInferenceExecutor([Binding(profile, prompt, client)]);

        var manifest = executor.FeatureManifest;

        manifest.ProtocolRevision.Should().Be("fuwen-inference/v1");
        manifest.SupportedIrVersions.Should().ContainInOrder(
            FuwenContracts.IrVersionV3,
            FuwenContracts.IrVersionV4,
            FuwenContracts.IrVersionV5,
            FuwenContracts.IrVersionV6,
            FuwenContracts.IrVersionV7,
            FuwenContracts.IrVersionV8);
        manifest.SupportedPromptForms.Should().ContainSingle().Which.Should().Be(InferencePromptForm.RegisteredTemplate);
        manifest.SupportedModalities.Should().ContainSingle().Which.Should().Be(InferenceModality.StructuredText);
        manifest.SupportedLimits.GetMaximum(InferenceLimitDimension.Turns).Should().Be(1);
        manifest.SupportedLimits.GetMaximum(InferenceLimitDimension.ModelCalls).Should().Be(1);
        manifest.SupportedLimits.GetMaximum(InferenceLimitDimension.ToolCalls).Should().BeNull();
        manifest.RecoveryQuality.Should().Be(InferenceRecoveryQuality.Unsupported);
        manifest.UsageQuality.Should().Be(InferenceUsageQuality.Unknown);
        manifest.PricingQuality.Should().Be(InferencePricingQuality.Unknown);
        manifest.Profiles.Should().ContainSingle().Which.Should().Be(profile);
        manifest.PromptTemplates.Should().ContainSingle().Which.Should().Be(prompt);
    }

    [Fact]
    public async Task Detailed_preflight_rejects_an_unbound_profile_without_provider_call()
    {
        var (profile, prompt) = Descriptors();
        var client = new FakeClient("\"must-not-run\"");
        var executor = new BaizeInferenceExecutor([Binding(profile, prompt, client)]);
        var changedProfile = Descriptor(DescriptorKind.InferenceProfile, "changed-profile");
        var requirement = new InferenceExecutionRequirement(changedProfile, prompt, null);

        var report = executor.PreflightDetailed(requirement);

        report.IsExecutable.Should().BeFalse();
        report.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Contain(InferencePreflightDiagnosticCode.MissingProfileBinding);
        client.Calls.Should().Be(0);
        var result = await executor.ExecuteAsync(
            Request(changedProfile, prompt, new PrimitiveType(FuwenPrimitiveKind.String)),
            TestContext.Current.CancellationToken);
        result.Failure!.Code.Should().Be(ExecutionFailureCode.DescriptorUnavailable);
        client.Calls.Should().Be(0);
    }

    private static BaizeInferenceBinding Binding(DescriptorReference profile, DescriptorReference prompt, FakeClient client, BaizeInferencePolicy? policy = null) =>
        new(profile, prompt, [new BaizeEndpointBinding("primary", "provider", "model", client)], policy: policy);

    private static InferenceExecutionRequest Request(
        DescriptorReference profile,
        DescriptorReference prompt,
        FuwenType output,
        InferenceLimits? limits = null) =>
        new(Invocation(), profile, prompt, [], [], output, limits: limits);

    private static InferenceExecutionRequest Request(
        DescriptorReference profile,
        DescriptorReference prompt,
        FuwenType output,
        IReadOnlyList<RuntimeArgument> arguments,
        IReadOnlyList<InferenceContextInput> contextInputs) =>
        new(Invocation(), profile, prompt, arguments, contextInputs, output);

    private static ContextSnapshotReference Snapshot(DescriptorReference provider) =>
        new(
            provider,
            "snapshot-1",
            new ContentDigest("sha256", "request/v1", new string('c', 64)),
            new ContentDigest("sha256", "content/v1", new string('d', 64)),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);

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

    private sealed class ThrowingRepairPipeline : IJsonRepairPipeline
    {
        public ValueTask<JsonRepairResult> RepairAsync(
            string input,
            JsonSchemaExpectation? expectation = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broken repair adapter");
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
            GenerationFeature features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
            string provider = "provider",
            string endpointId = "endpoint",
            string model = "model")
        {
            var handle = new GenerationOperationHandle(
                provider, endpointId, "operation", model, new Dictionary<string, string>());
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

    private sealed class DelayedGenerationClient(
        TimeSpan? submissionDelay = null,
        TimeSpan? pollingDelay = null,
        bool initiallyQueued = false) : IGenerationClient
    {
        private readonly GenerationOperationHandle handle = new(
            "provider", "endpoint", "delayed-operation", "model", new Dictionary<string, string>());

        public int SubmissionCalls { get; private set; }
        public int PollingCalls { get; private set; }
        public GenerationCapabilities Capabilities { get; } = new()
        {
            Features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
        };

        public async Task<GenerationOperation> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            SubmissionCalls++;
            if (submissionDelay is { } delay)
                await Task.Delay(delay, cancellationToken);
            return initiallyQueued
                ? new GenerationOperation(handle, GenerationOperationState.Queued, ProviderMetadata: new Dictionary<string, object?>())
                : Completed();
        }

        public async Task<GenerationOperation> GetAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            PollingCalls++;
            if (pollingDelay is { } delay)
                await Task.Delay(delay, cancellationToken);
            return Completed();
        }

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                handle,
                GenerationOperationState.Canceled,
                Error: new GenerationError(GenerationErrorKind.Canceled, "cancelled"),
                ProviderMetadata: new Dictionary<string, object?>()));

        private GenerationOperation Completed() => new(
            handle,
            GenerationOperationState.Succeeded,
            new GenerationResult(
                [new GeneratedAsset(new ProviderGeneratedAssetSource("asset-1", "provider"), "image/png")]),
            ProviderMetadata: new Dictionary<string, object?>());
    }

    private sealed class DelayedGeneratedAssetPublisher(TimeSpan delay) : IBaizeGeneratedAssetPublisher
    {
        public int Calls { get; private set; }

        public async ValueTask<IReadOnlyList<ArtifactPublicationReceipt>> PublishAsync(
            InferenceExecutionRequest request,
            IReadOnlyList<GeneratedAsset> assets,
            DescriptorReference artifactDescriptor,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(delay, cancellationToken);
            return [];
        }
    }

    private sealed class TerminalGenerationClient(GenerationErrorKind errorKind) : IGenerationClient
    {
        private readonly GenerationOperationHandle handle = new(
            "provider", "endpoint", "terminal-operation", "model", new Dictionary<string, string>());

        public GenerationCapabilities Capabilities { get; } = new()
        {
            Features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
        };

        public Task<GenerationOperation> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                handle,
                GenerationOperationState.Failed,
                Error: new GenerationError(errorKind, "provider deadline"),
                ProviderMetadata: new Dictionary<string, object?>()));

        public Task<GenerationOperation> GetAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Terminal operations are not polled.");

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                handle,
                GenerationOperationState.Canceled,
                Error: new GenerationError(GenerationErrorKind.Canceled, "cancelled"),
                ProviderMetadata: new Dictionary<string, object?>()));
    }

    private sealed class ResumeAfterPartialPublisher : IBaizeGeneratedAssetPublisher
    {
        public List<string> OperationKeys { get; } = [];

        public ValueTask<IReadOnlyList<ArtifactPublicationReceipt>> PublishAsync(
            InferenceExecutionRequest request,
            IReadOnlyList<GeneratedAsset> assets,
            DescriptorReference artifactDescriptor,
            CancellationToken cancellationToken = default)
        {
            OperationKeys.Add(request.Invocation.OperationKey);
            if (OperationKeys.Count == 1)
                throw new InvalidOperationException("interrupted after durable write");

            IReadOnlyList<ArtifactPublicationReceipt> receipts =
            [
                new ArtifactPublicationReceipt(
                    request.Invocation.OperationKey,
                    new ArtifactReference(
                        "generated-store",
                        "image-0",
                        artifactDescriptor,
                        new ContentDigest("sha256", "content/v1", new string('b', 64)),
                        assets[0].Size,
                        assets[0].FileName),
                    "receipt-0",
                    PublicationDisposition.Replayed),
            ];
            return ValueTask.FromResult(receipts);
        }
    }

    private sealed class QueuedGenerationClient(GenerationResult result) : IGenerationClient
    {
        private readonly GenerationOperationHandle submittedHandle = new(
            "provider", "endpoint", "operation-1", "model",
            new Dictionary<string, string> { ["continuation"] = "initial" });
        private readonly GenerationOperationHandle completedHandle = new(
            "provider", "endpoint", "operation-1", "model",
            new Dictionary<string, string> { ["continuation"] = "refreshed" });

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
                submittedHandle, GenerationOperationState.Queued, ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> GetAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            PolledHandles.Add(operationHandle);
            return Task.FromResult(new GenerationOperation(
                completedHandle, GenerationOperationState.Succeeded, result,
                ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                submittedHandle,
                GenerationOperationState.Canceled,
                Error: new GenerationError(GenerationErrorKind.Canceled, "cancelled"),
                ProviderMetadata: new Dictionary<string, object?>()));
    }

    private sealed class RedirectedGenerationClient(
        GenerationResult result,
        bool redirectSubmission = false) : IGenerationClient
    {
        private readonly GenerationOperationHandle submittedHandle = new(
            "provider", redirectSubmission ? "other-endpoint" : "endpoint", "operation-1", "model", new Dictionary<string, string>());
        private readonly GenerationOperationHandle redirectedHandle = new(
            "provider", "endpoint", "operation-2", "model", new Dictionary<string, string>());

        public List<GenerationOperationHandle> PolledHandles { get; } = [];
        public GenerationCapabilities Capabilities { get; } = new()
        {
            Features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
        };

        public Task<GenerationOperation> SubmitAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenerationOperation(
                submittedHandle,
                GenerationOperationState.Queued,
                ProviderMetadata: new Dictionary<string, object?>()));

        public Task<GenerationOperation> GetAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            PolledHandles.Add(operationHandle);
            return Task.FromResult(new GenerationOperation(
                redirectedHandle,
                GenerationOperationState.Succeeded,
                result,
                ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> CancelAsync(
            GenerationOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => Task.FromResult(new GenerationOperation(
                submittedHandle,
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
