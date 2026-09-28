using Penghou.Baize;
using Penghou.Fuwen;
using Penghou.Fuwen.Baize;

namespace Penghou.Fuwen.Baize.Tests;

public sealed class BaizeInferenceTurnExecutorTests
{
    [Fact]
    public async Task Executes_one_bounded_turn_and_returns_json_candidate()
    {
        var client = new FakeClient(new LlmResponse("[1,2,3]"));
        var (binding, profile, prompt) = Binding(client);
        var executor = new BaizeInferenceTurnExecutor(binding);

        var result = await executor.ExecuteTurnAsync(Request(), TestContext.Current.CancellationToken);

        Assert.IsType<InferenceFinalCandidateResult>(result);
        Assert.Equal("[1,2,3]", ((InferenceFinalCandidateResult)result).CandidateJson);
        Assert.Equal(1, client.Calls);
        Assert.Equal(32, client.LastRequest!.MaxTokens);
        var report = executor.PreflightTurnDetailed(new InferenceExecutionRequirement(profile, prompt, null));
        Assert.True(report.IsExecutable);
    }

    [Fact]
    public async Task Rejects_unquotable_cost_budget_before_provider_work()
    {
        var client = new FakeClient(new LlmResponse("{}"));
        var (binding, _, _) = Binding(client);
        var executor = new BaizeInferenceTurnExecutor(binding);
        var request = Request(new InferenceLimitSet([
            new(InferenceLimitDimension.CostMicrounits, 100),
        ]));

        var exception = await Assert.ThrowsAsync<InferenceTurnFailureException>(async () =>
            await executor.ExecuteTurnAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(ExecutionFailureCode.BudgetUnknown, exception.Failure.Code);
        Assert.False(exception.Failure.MayHaveCommittedEffect);
        Assert.Equal(0, client.Calls);
        Assert.Equal(InferencePricingQuality.Unknown, executor.TurnFeatureManifest.PricingQuality);
    }

    [Fact]
    public async Task Allows_explicit_advisory_budget_monitoring_without_claiming_a_quote()
    {
        var client = new FakeClient(new LlmResponse("{}"));
        var (binding, _, _) = Binding(client);
        var executor = new BaizeInferenceTurnExecutor(binding);
        var request = new InferenceTurnRequest(
            InferenceBudgetEnforcement.Advisory,
            "interaction-1",
            0,
            [new InferenceConversationMessage(InferenceTurnRole.User, "Return JSON")],
            [],
            new InferenceLimitSet([
                new(InferenceLimitDimension.PromptTokens, 100),
                new(InferenceLimitDimension.TotalTokens, 120),
                new(InferenceLimitDimension.CostMicrounits, 100),
            ]),
            maximumNewToolCalls: 0);

        var result = await executor.ExecuteTurnAsync(request, TestContext.Current.CancellationToken);

        Assert.IsType<InferenceFinalCandidateResult>(result);
        Assert.Equal(1, client.Calls);
        Assert.Equal(InferenceUsageQuality.Unknown, executor.TurnFeatureManifest.UsageQuality);
        Assert.Equal(InferencePricingQuality.Unknown, executor.TurnFeatureManifest.PricingQuality);
    }

    [Fact]
    public async Task Exposes_only_schema_matched_tools_and_preserves_provider_call_identity()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"],\"additionalProperties\":false}";
        var tool = Descriptor(DescriptorKind.Tool, "lookup-descriptor");
        var providerCall = new LlmToolCall("provider-call-7", "lookup", "{\"q\":\"x\"}");
        var client = new FakeClient(new LlmResponse(string.Empty, ToolCalls: [providerCall]));
        var (profile, prompt) = Descriptors();
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [new BaizeEndpointBinding("primary", "provider", "model", client)],
            tools: [new BaizeToolBinding(tool, new LlmTool("lookup", "Lookup", schema))]);
        var executor = new BaizeInferenceTurnExecutor(binding);
        var contract = new InferenceModelToolContract(
            "lookup", schema, "{\"type\":\"string\"}", tool.ContentDigest.Value, "validator/v1", "contract/v1");
        var requirement = new InferenceToolRequirement(tool, InferenceToolEffect.ReadOnly, contract);
        var request = new InferenceTurnRequest(
            "interaction-1", 0,
            [new InferenceConversationMessage(InferenceTurnRole.User, "Look up x")],
            [requirement],
            maximumNewToolCalls: 1);

        var preflight = executor.PreflightTurnDetailed(new InferenceExecutionRequirement(
            profile, prompt, null, [tool], false, InferenceModality.StructuredText, [requirement]));
        var result = await executor.ExecuteTurnAsync(request, TestContext.Current.CancellationToken);

        Assert.True(preflight.IsExecutable);
        var turn = Assert.IsType<InferenceToolCallTurnResult>(result);
        Assert.Equal("provider-call-7", turn.Proposals.Single().CallId);
        Assert.Equal(tool, turn.Proposals.Single().Tool);
        Assert.Equal("lookup", client.LastRequest!.Tools!.Single().Name);
        Assert.Equal(schema, client.LastRequest.Tools.Single().InputSchemaJson);
    }

    [Fact]
    public void Preflight_rejects_a_tool_when_the_request_omits_its_model_contract()
    {
        var client = new FakeClient(new LlmResponse("{}"));
        var (binding, profile, prompt) = Binding(client);
        var executor = new BaizeInferenceTurnExecutor(binding);
        var tool = Descriptor(DescriptorKind.Tool, "lookup");
        var requirement = new InferenceToolRequirement(tool);

        var report = executor.PreflightTurnDetailed(new InferenceExecutionRequirement(
            profile, prompt, null, [tool], false, InferenceModality.StructuredText, [requirement]));

        Assert.False(report.IsExecutable);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Replays_native_assistant_calls_and_tool_results_with_exact_identities()
    {
        var client = new FakeClient(new LlmResponse("{}"));
        var (profile, prompt) = Descriptors();
        var tool = Descriptor(DescriptorKind.Tool, "lookup-descriptor");
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [new BaizeEndpointBinding("primary", "provider", "model", client)],
            tools: [new BaizeToolBinding(tool, new LlmTool("lookup", "Lookup", "{\"type\":\"object\"}"))]);
        var executor = new BaizeInferenceTurnExecutor(binding);
        var calls = new[] { new InferenceToolCallProposal("call-1", tool, "{\"q\":\"x\"}") };
        var request = new InferenceTurnRequest(
            "interaction-1",
            1,
            [
                new InferenceConversationMessage(InferenceTurnRole.User, "look this up"),
                new InferenceConversationMessage(InferenceTurnRole.Assistant, "checking", toolCalls: calls),
                new InferenceConversationMessage(InferenceTurnRole.Tool, "result", "call-1", tool),
            ],
            [],
            maximumNewToolCalls: 0);

        var result = await executor.ExecuteTurnAsync(request, TestContext.Current.CancellationToken);

        Assert.IsType<InferenceFinalCandidateResult>(result);
        Assert.Equal(1, client.Calls);
        Assert.Contains(client.LastRequest!.Messages[1].Parts, part =>
            part is LlmToolCallContent { ToolCall.Id: "call-1", ToolCall.Name: "lookup" });
        Assert.Contains(client.LastRequest.Messages[2].Parts, part =>
            part is LlmToolResultContent { Result.ToolCallId: "call-1", Result.ToolName: "lookup" });
    }

    private static InferenceTurnRequest Request(InferenceLimitSet? remaining = null) => new(
        "interaction-1",
        0,
        [new InferenceConversationMessage(InferenceTurnRole.User, "Return a JSON value.")],
        [],
        remaining,
        maximumNewToolCalls: 0,
        maxCompletionTokens: 32);

    private static (BaizeInferenceBinding Binding, DescriptorReference Profile, DescriptorReference Prompt) Binding(FakeClient client)
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "profile");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "prompt");
        var binding = new BaizeInferenceBinding(
            profile,
            prompt,
            [new BaizeEndpointBinding("primary", "provider", "model", client)]);
        return (binding, profile, prompt);
    }

    private static (DescriptorReference Profile, DescriptorReference Prompt) Descriptors() =>
        (Descriptor(DescriptorKind.InferenceProfile, "profile"), Descriptor(DescriptorKind.PromptTemplate, "prompt"));

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) => new(
        kind,
        name,
        "1",
        new ContentDigest("sha256", "test", new string('a', 64)));

    private sealed class FakeClient(LlmResponse response) : ILlmClient, ILlmCompletionClient
    {
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
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamEvent(string.Empty);
            await Task.CompletedTask;
        }
    }
}
