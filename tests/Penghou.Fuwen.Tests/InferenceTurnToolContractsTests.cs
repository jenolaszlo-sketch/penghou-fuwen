using System.Text.Json;
using FluentAssertions;

#pragma warning disable xUnit1051 // Intentional explicit CancellationToken tests (canceled + default tokens).

namespace Penghou.Fuwen.Tests;

public sealed class InferenceTurnToolContractsTests
{
    // ---------- helpers ----------

    private static DescriptorReference ToolDescriptor(string name, string hashChar = "a") =>
        new(DescriptorKind.Tool, name, "1", new ContentDigest("sha256", "descriptor/v1", new string(hashChar[0], 64)));

    private static InferenceToolRequirement ToolRequirement(string name, InferenceToolEffect effect = InferenceToolEffect.ReadOnly) =>
        new(ToolDescriptor(name), effect);

    private static InferenceConversationMessage UserMessage(string text = "{\"q\":1}") =>
        new(InferenceTurnRole.User, text);

    private static InferenceTurnRequest TurnRequest(
        string interactionId = "interaction-1",
        int turnOrdinal = 0,
        IReadOnlyList<InferenceConversationMessage>? conversation = null,
        IReadOnlyList<InferenceToolRequirement>? visibleTools = null,
        int? maximumNewToolCalls = null) =>
        new(
            interactionId,
            turnOrdinal,
            conversation ?? [UserMessage()],
            visibleTools ?? [ToolRequirement("lookup")],
            maximumNewToolCalls: maximumNewToolCalls);

    private static InferenceToolCallProposal Proposal(string callId, DescriptorReference? tool = null, string argsJson = "{\"q\":1}") =>
        new(callId, tool ?? ToolDescriptor("lookup"), argsJson);

    private static RuntimeValue JsonArg(string json = "{\"q\":1}")
    {
        using var document = JsonDocument.Parse(json);
        return new JsonRuntimeValue(document.RootElement);
    }

    // ---------- 1. constructor bounds ----------

    [Fact]
    public void Turn_request_rejects_empty_conversation()
    {
        Action act = () => TurnRequest(conversation: []);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Turn_request_rejects_too_many_messages()
    {
        var conversation = Enumerable.Range(0, InferenceTurnRequest.MaximumMessages + 1)
            .Select(_ => UserMessage())
            .ToList();
        Action act = () => TurnRequest(conversation: conversation);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Turn_request_rejects_negative_ordinal()
    {
        Action act = () => TurnRequest(turnOrdinal: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Turn_request_rejects_duplicate_visible_tools()
    {
        var tools = new List<InferenceToolRequirement> { ToolRequirement("lookup"), ToolRequirement("lookup") };
        Action act = () => TurnRequest(visibleTools: tools);
        act.Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Fact]
    public void Turn_request_rejects_out_of_range_maximum_new_tool_calls()
    {
        Action zero = () => TurnRequest(maximumNewToolCalls: 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        Action tooMany = () => TurnRequest(maximumNewToolCalls: InferenceTurnRequest.MaximumProposals + 1);
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Tool_call_result_rejects_empty_proposals()
    {
        Action act = () => new InferenceToolCallTurnResult([]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Tool_call_result_rejects_too_many_proposals()
    {
        var proposals = Enumerable.Range(0, InferenceTurnRequest.MaximumProposals + 1)
            .Select(i => Proposal($"call-{i}"))
            .ToList();
        Action act = () => new InferenceToolCallTurnResult(proposals);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, -1)]
    public void Turn_usage_rejects_negative_tokens(int? prompt, int? completion, int? total)
    {
        Action act = () => new InferenceTurnUsage(prompt, completion, total);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tool_message_requires_call_id(string? callId)
    {
        Action act = () => new InferenceConversationMessage(InferenceTurnRole.Tool, "result", callId);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(InferenceTurnRole.System)]
    [InlineData(InferenceTurnRole.User)]
    [InlineData(InferenceTurnRole.Assistant)]
    public void Non_tool_message_rejects_call_id(InferenceTurnRole role)
    {
        Action act = () => new InferenceConversationMessage(role, "text", "call-1");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Conversation_message_rejects_oversized_text()
    {
        var oversized = new string('x', InferenceConversationMessage.MaximumTextUtf8Bytes + 1);
        Action act = () => new InferenceConversationMessage(InferenceTurnRole.User, oversized);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Proposal_rejects_oversized_arguments()
    {
        var oversized = "{\"q\":\"" + new string('x', InferenceToolCallProposal.MaximumArgumentsUtf8Bytes + 1) + "\"}";
        Action act = () => Proposal("call-1", argsJson: oversized);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Proposal_rejects_oversized_call_id()
    {
        var oversized = new string('c', InferenceToolCallProposal.MaximumCallIdUtf8Bytes + 1);
        Action act = () => Proposal(oversized);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Final_candidate_rejects_oversized_json()
    {
        var oversized = new string('x', InferenceFinalCandidateResult.MaximumCandidateUtf8Bytes + 1);
        Action act = () => new InferenceFinalCandidateResult(oversized);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructors_reject_undefined_enum_values()
    {
        Action role = () => new InferenceConversationMessage((InferenceTurnRole)999, "hi");
        role.Should().Throw<ArgumentOutOfRangeException>();
        Action effect = () => new InferenceToolRequirement(ToolDescriptor("lookup"), (InferenceToolEffect)999);
        effect.Should().Throw<ArgumentOutOfRangeException>();
        Action retry = () => new InferenceReadToolRequest(ToolDescriptor("lookup"), JsonArg(), "scope", "op", (InferenceReadToolRetrySafety)999);
        retry.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Read_tool_request_rejects_oversized_scope_and_operation_key()
    {
        var oversized = new string('s', InferenceReadToolRequest.MaximumScopeUtf8Bytes + 1);
        Action scope = () => new InferenceReadToolRequest(ToolDescriptor("lookup"), JsonArg(), oversized, "op");
        scope.Should().Throw<ArgumentOutOfRangeException>();
        var oversizedKey = new string('k', InferenceReadToolRequest.MaximumOperationKeyUtf8Bytes + 1);
        Action key = () => new InferenceReadToolRequest(ToolDescriptor("lookup"), JsonArg(), "scope", oversizedKey);
        key.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------- 2. detach / snapshot ----------

    [Fact]
    public void Turn_request_detaches_caller_lists()
    {
        var conversation = new List<InferenceConversationMessage> { UserMessage("{\"q\":1}") };
        var visible = new List<InferenceToolRequirement> { ToolRequirement("lookup") };
        var request = new InferenceTurnRequest("interaction-1", 0, conversation, visible);

        conversation.Add(UserMessage("{\"q\":2}"));
        conversation.Clear();
        visible.Clear();

        request.Conversation.Should().HaveCount(1);
        request.VisibleTools.Should().HaveCount(1);
        request.Conversation[0].Text.Should().Be("{\"q\":1}");
    }

    [Fact]
    public void Turn_request_detaches_individual_messages_and_tools()
    {
        var message = UserMessage("hello");
        var requirement = ToolRequirement("lookup");
        var request = new InferenceTurnRequest("interaction-1", 0, [message], [requirement]);

        request.Conversation[0].Should().NotBeSameAs(message);
        request.VisibleTools[0].Should().NotBeSameAs(requirement);
        request.Conversation[0].Role.Should().Be(message.Role);
        request.Conversation[0].Text.Should().Be(message.Text);
        request.Conversation[0].ToolCallId.Should().Be(message.ToolCallId);
    }

    [Fact]
    public void Tool_call_result_detaches_proposals_and_usage()
    {
        var proposals = new List<InferenceToolCallProposal> { Proposal("call-1") };
        var usage = new InferenceTurnUsage(promptTokens: 3, completionTokens: 2, totalTokens: 5);
        var result = new InferenceToolCallTurnResult(proposals, usage);

        proposals.Clear();

        result.Proposals.Should().HaveCount(1);
        result.Proposals[0].CallId.Should().Be("call-1");
        result.Usage.Should().NotBeNull();
        result.Usage!.TotalTokens.Should().Be(5);
        result.Usage.Should().NotBeSameAs(usage);
    }

    [Fact]
    public void Read_tool_request_and_result_snapshot_runtime_values()
    {
        var tool = ToolDescriptor("lookup");
        var args = JsonArg("{\"q\":1}");
        var request = new InferenceReadToolRequest(tool, args, "scope", "op-1");

        request.Arguments.Should().NotBeSameAs(args);

        var output = JsonArg("{\"answer\":42}");
        var result = InferenceReadToolResult.Succeeded(output);
        result.Output.Should().NotBeSameAs(output);
        CanonicalJson.Serialize(result.Output).Should().Equal(CanonicalJson.Serialize(output));
    }

    // ---------- 3. validator matrix ----------

    [Fact]
    public void Validator_accepts_valid_multi_proposal_batch()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposals = new List<InferenceToolCallProposal>
        {
            new("call-1", tool, "{\"q\":1}"),
            new("call-2", tool, "{\"q\":2}"),
        };

        InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192)
            .Should().BeNull();
    }

    [Fact]
    public void Validator_rejects_undeclared_tool()
    {
        var visible = new List<InferenceToolRequirement> { new(ToolDescriptor("lookup")) };
        var proposals = new List<InferenceToolCallProposal> { Proposal("call-1", ToolDescriptor("unknown", "b")) };

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_write_capable_tool()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool, InferenceToolEffect.IdempotentWrite) };
        var proposals = new List<InferenceToolCallProposal> { new("call-1", tool, "{\"q\":1}") };

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_duplicate_call_ids()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposal = new InferenceToolCallProposal("call-1", tool, "{\"q\":1}");

        var failure = InferenceTurnValidation.ValidateProposals([proposal, proposal], visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_oversized_arguments()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposals = new List<InferenceToolCallProposal> { new("call-1", tool, "{\"q\":12345}") };

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 4);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_non_json_arguments()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposals = new List<InferenceToolCallProposal> { new("call-1", tool, "not-json") };

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_json_array_arguments()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposals = new List<InferenceToolCallProposal> { new("call-1", tool, "[1,2]") };

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_empty_proposals()
    {
        var visible = new List<InferenceToolRequirement> { new(ToolDescriptor("lookup")) };

        var failure = InferenceTurnValidation.ValidateProposals([], visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    [Fact]
    public void Validator_rejects_too_many_proposals()
    {
        var tool = ToolDescriptor("lookup");
        var visible = new List<InferenceToolRequirement> { new(tool) };
        var proposals = Enumerable.Range(0, InferenceTurnRequest.MaximumProposals + 1)
            .Select(i => new InferenceToolCallProposal($"call-{i}", tool, "{\"q\":1}"))
            .ToList();

        var failure = InferenceTurnValidation.ValidateProposals(proposals, visible, maximumArgumentBytes: 8_192);
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.ToolMappingFailure);
    }

    // ---------- 4. fakes ----------

    [Fact]
    public async Task Fake_final_candidate_round_trips_json_and_usage()
    {
        var usage = new InferenceTurnUsage(promptTokens: 10, completionTokens: 5, totalTokens: 15);
        var executor = DeterministicFakeTurnExecutor.FinalCandidate("{\"answer\":\"ok\"}", usage);
        var result = await executor.ExecuteTurnAsync(TurnRequest());

        result.IsToolCall.Should().BeFalse();
        var final = result.Should().BeOfType<InferenceFinalCandidateResult>().Subject;
        final.CandidateJson.Should().Be("{\"answer\":\"ok\"}");
        final.Usage.Should().NotBeNull();
        final.Usage!.TotalTokens.Should().Be(15);
        executor.ObservedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Fake_tool_calls_carry_null_usage()
    {
        var executor = DeterministicFakeTurnExecutor.ToolCalls(Proposal("call-1"));
        var result = await executor.ExecuteTurnAsync(TurnRequest());

        result.IsToolCall.Should().BeTrue();
        var calls = result.Should().BeOfType<InferenceToolCallTurnResult>().Subject;
        calls.Proposals.Should().ContainSingle().Which.CallId.Should().Be("call-1");
        calls.Usage.Should().BeNull();
    }

    [Fact]
    public async Task Fake_script_exhaustion_throws()
    {
        var executor = new DeterministicFakeTurnExecutor([new InferenceFinalCandidateResult("{\"answer\":\"ok\"}")]);

        var first = await executor.ExecuteTurnAsync(TurnRequest());
        first.Should().BeOfType<InferenceFinalCandidateResult>();

        Func<Task> exhausted = async () => await executor.ExecuteTurnAsync(TurnRequest());
        await exhausted.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Fake_turn_executor_observes_cancellation()
    {
        var executor = DeterministicFakeTurnExecutor.Cancelling();
        Func<Task> act = async () => await executor.ExecuteTurnAsync(TurnRequest(), new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Fake_read_tool_success_preserves_output_and_operation_key()
    {
        var tool = ToolDescriptor("lookup");
        var output = JsonArg("{\"answer\":42}");
        var fake = new DeterministicFakeReadToolExecutor().RegisterSuccess(tool, output);
        var sample = new InferenceReadToolRequest(tool, JsonArg("{\"q\":1}"), InferenceTurnToolConformance.ScopeFor(tool), "op-1");

        var result = await fake.ExecuteAsync(sample);

        result.IsSuccess.Should().BeTrue();
        result.Output.Should().NotBeNull();
        CanonicalJson.Serialize(result.Output).Should().Equal(CanonicalJson.Serialize(output));
        result.Evidence.Should().NotBeNull();
        result.Evidence!.OperationKey.Should().Be("op-1");
        fake.ObservedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Fake_read_tool_rejects_undeclared_tool()
    {
        var tool = ToolDescriptor("lookup");
        var fake = new DeterministicFakeReadToolExecutor().RegisterSuccess(tool, JsonArg("{\"answer\":1}"));
        var undeclared = new InferenceReadToolRequest(ToolDescriptor("unknown", "b"), JsonArg("{\"q\":1}"), "scope", "op-x");

        var result = await fake.ExecuteAsync(undeclared);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().NotBeNull();
        result.Failure!.Code.Should().Be(ExecutionFailureCode.ProviderError);
    }

    [Fact]
    public async Task Fake_read_tool_observes_cancellation()
    {
        var tool = ToolDescriptor("lookup");
        var fake = new DeterministicFakeReadToolExecutor().RegisterSuccess(tool, JsonArg("{\"answer\":1}"));
        var sample = new InferenceReadToolRequest(tool, JsonArg("{\"q\":1}"), "scope", "op-1");

        Func<Task> act = async () => await fake.ExecuteAsync(sample, new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------- 5. conformance runners ----------

    [Fact]
    public async Task Turn_conformance_passes_against_deterministic_fake()
    {
        var executor = DeterministicFakeTurnExecutor.FinalCandidate("{\"answer\":\"ok\"}");
        Func<Task> act = async () => await InferenceTurnToolConformance.VerifyTurnExecutorAsync(executor);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Read_tool_conformance_passes_against_deterministic_fake()
    {
        var tool = ToolDescriptor("lookup");
        var output = JsonArg("{\"answer\":42}");
        var fake = new DeterministicFakeReadToolExecutor().RegisterSuccess(tool, output);
        var sample = new InferenceReadToolRequest(
            tool,
            JsonArg("{\"q\":1}"),
            InferenceTurnToolConformance.ScopeFor(tool),
            InferenceTurnToolConformance.OperationKeyFor("interaction-1", 0, "call-1"));

        Func<Task> act = async () => await InferenceTurnToolConformance.VerifyReadToolExecutorAsync(fake, sample, output);
        await act.Should().NotThrowAsync();
    }

    // ---------- 6. scope / operation key ----------

    [Fact]
    public void Scope_for_uses_name_at_version_format()
    {
        InferenceTurnToolConformance.ScopeFor(ToolDescriptor("lookup")).Should().Be("lookup@1");
    }

    [Theory]
    [InlineData("interaction-1", 0, "call-1", "interaction-1/tool/0000/call-1")]
    [InlineData("interaction-1", 7, "call-1", "interaction-1/tool/0007/call-1")]
    [InlineData("ix", 42, "c", "ix/tool/0042/c")]
    public void Operation_key_for_uses_exact_format(string interactionId, int ordinal, string callId, string expected)
    {
        InferenceTurnToolConformance.OperationKeyFor(interactionId, ordinal, callId).Should().Be(expected);
    }

    [Fact]
    public void Operation_key_for_rejects_negative_ordinal()
    {
        Action act = () => InferenceTurnToolConformance.OperationKeyFor("interaction-1", -1, "call-1");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Operation_key_for_rejects_blank_interaction_id(string? interactionId)
    {
        Action act = () => InferenceTurnToolConformance.OperationKeyFor(interactionId!, 0, "call-1");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Operation_key_for_rejects_blank_call_id(string? callId)
    {
        Action act = () => InferenceTurnToolConformance.OperationKeyFor("interaction-1", 0, callId!);
        act.Should().Throw<ArgumentException>();
    }

    // ---------- 7. ambiguity + continuation data ----------

    [Fact]
    public async Task Fake_read_tool_can_report_an_ambiguous_may_have_committed_failure()
    {
        var tool = ToolDescriptor("lookup");
        var fake = new DeterministicFakeReadToolExecutor().Register(
            tool,
            request => InferenceReadToolResult.Failed(
                new ExecutionFailure(
                    ExecutionFailureKind.Provider,
                    ExecutionFailureCode.ProviderError,
                    "The ambiguous tool operation may have committed.",
                    mayHaveCommittedEffect: true),
                new InferenceReadToolEvidence(tool, request.OperationKey)));
        var sample = new InferenceReadToolRequest(tool, JsonArg("{\"q\":1}"), "scope", "op-ambiguous");

        var result = await fake.ExecuteAsync(sample);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.MayHaveCommittedEffect.Should().BeTrue();
        result.Evidence!.OperationKey.Should().Be("op-ambiguous");
    }

    [Fact]
    public void Turn_request_carries_remaining_limits_and_continuation_bound()
    {
        var remaining = new InferenceLimitSet([new InferenceLimit(InferenceLimitDimension.ToolCalls, 3)]);
        var request = new InferenceTurnRequest(
            "interaction-1",
            2,
            [UserMessage()],
            [ToolRequirement("lookup")],
            remaining,
            maximumNewToolCalls: 2);

        request.TurnOrdinal.Should().Be(2);
        request.RemainingLimits.GetMaximum(InferenceLimitDimension.ToolCalls).Should().Be(3);
        request.MaximumNewToolCalls.Should().Be(2);
    }

    [Fact]
    public async Task Fake_responder_answers_every_turn_deterministically()
    {
        var executor = new DeterministicFakeTurnExecutor(request =>
            new InferenceFinalCandidateResult($"{{\"turn\":{request.TurnOrdinal}}}"));

        var first = await executor.ExecuteTurnAsync(TurnRequest(turnOrdinal: 0));
        var second = await executor.ExecuteTurnAsync(TurnRequest(turnOrdinal: 1));

        first.Should().BeOfType<InferenceFinalCandidateResult>()
            .Which.CandidateJson.Should().Be("{\"turn\":0}");
        second.Should().BeOfType<InferenceFinalCandidateResult>()
            .Which.CandidateJson.Should().Be("{\"turn\":1}");
        executor.ObservedRequests.Should().HaveCount(2);
    }

    [Fact]
    public void Turn_request_carries_per_call_completion_and_timeout_bounds()
    {
        var request = new InferenceTurnRequest(
            "interaction-1",
            0,
            [UserMessage()],
            [ToolRequirement("lookup")],
            maxCompletionTokens: 512,
            timeoutSeconds: 30);

        request.MaxCompletionTokens.Should().Be(512);
        request.TimeoutSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(1_000_001, 30)]
    [InlineData(512, 0)]
    [InlineData(512, -5)]
    [InlineData(512, 3601)]
    public void Turn_request_rejects_out_of_range_per_call_bounds(int maxTokens, int timeout)
    {
        Action act = () => new InferenceTurnRequest(
            "interaction-1",
            0,
            [UserMessage()],
            [ToolRequirement("lookup")],
            maxCompletionTokens: maxTokens,
            timeoutSeconds: timeout);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Per_call_limit_exceeded_belongs_to_the_contract_kind()
    {
        Action act = () => new ExecutionFailure(
            ExecutionFailureKind.Contract,
            ExecutionFailureCode.PerCallLimitExceeded,
            "per-call bound exceeded.");

        act.Should().NotThrow();
    }

    [Fact]
    public void Read_tool_request_carries_an_optional_result_ceiling()
    {
        var request = new InferenceReadToolRequest(
            ToolDescriptor("lookup"),
            JsonArg(),
            "scope",
            "op-1",
            maximumResultUtf8Bytes: 1024);

        request.MaximumResultUtf8Bytes.Should().Be(1024);
    }

    [Fact]
    public void Read_tool_request_rejects_a_non_positive_result_ceiling()
    {
        Action act = () => new InferenceReadToolRequest(
            ToolDescriptor("lookup"),
            JsonArg(),
            "scope",
            "op-1",
            maximumResultUtf8Bytes: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
