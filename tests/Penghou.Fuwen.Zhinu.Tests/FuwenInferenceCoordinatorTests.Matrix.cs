using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>
/// CI-6 recovery and compatibility matrix for coordinated complex inference:
/// aggregate limits, multiple tool calls, unknown cost, interaction identity,
/// and the deterministic multi-turn planning trace. Region coverage for the
/// one-call path inside repeat and fan-out lives in
/// <see cref="R31ToolSurfaceRecoveryTests"/>.
/// </summary>
public sealed partial class FuwenInferenceCoordinatorTests
{
    private static WorkflowPlan CreateRepeatPlan(InferenceProtocolLimits limits)
    {
        var loopPath = StructuralNodeIdentity.Create("coordloop", "loop");
        var inferPath = $"{loopPath}/$body/infer";
        var returnPath = StructuralNodeIdentity.Create("coordloop", "return_result");
        return new WorkflowPlanBuilder("coordloop", "1", Text, Text, "routing/1")
            .AddCatalogueBinding(Profile)
            .AddCatalogueBinding(Search)
            .AddPrompt(AnswerPrompt())
            .AddNode(new RepeatNode(
                "loop", loopPath, 2, Text, new InputBinding([]),
                [
                    new InferenceNode(
                        "infer", inferPath, Profile, null, [], [], Text, [],
                        "answer_prompt",
                        [new PromptBinding("question", new LoopStateBinding([]))],
                        Tools: [Search],
                        Protocol: new InferenceProtocol(limits)),
                ],
                new NodeOutputBinding(inferPath, []),
                new ConditionExpression(
                    ConditionOperator.Equal,
                    new LoopIterationBinding([]),
                    new LiteralBinding(JsonDocument.Parse("2").RootElement.Clone())),
                Text))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("coordloop", [
                    new WorkflowExecutionPhase([loopPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{loopPath}/$body", [
                    new WorkflowExecutionPhase([inferPath]),
                ]),
            ]))
            .Build();
    }

    private static async Task<FuwenZhinuWorkflowRegistration> RegisterNamedAsync(
        WorkflowPlan plan,
        string name,
        IInferenceTurnExecutor turnExecutor,
        IInferenceReadToolExecutor? readToolExecutor = null,
        InferenceLimitSet? hostCeilings = null,
        IInferenceEvidenceSink? evidenceSink = null,
        CancellationToken ct = default)
    {
        var admission = await AdmitAsync(plan, ct);
        return await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new UnusedContext(),
                    CurrentInferenceFixture.WithPreflight(new UnusedInference()),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(),
                    turnExecutor,
                    readToolExecutor,
                    hostCeilings,
                    evidenceSink))
            .CreateAsync(name, "1", admission, ct);
    }

    private static InferenceProtocolLimits TokenLimit(long total, long turns = 3, long toolCalls = 2) =>
        new(maxTurns: turns, maxModelCalls: turns, maxToolCalls: toolCalls, maxTotalTokens: total,
            maxDurationMilliseconds: 60_000);

    private static InferenceProtocolLimits CostLimit(long microunits, long turns = 3, long toolCalls = 2) =>
        new(maxTurns: turns, maxModelCalls: turns, maxToolCalls: toolCalls,
            maxDurationMilliseconds: 60_000, cost: new InferenceCostLimit("USD", microunits));

    [Fact]
    public async Task Multiple_tool_calls_in_one_turn_execute_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 3), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [
                        new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"),
                        new InferenceToolCallProposal("call-2", Search, "{\"q\":2}"),
                    ], ExactUsage())
                : new InferenceFinalCandidateResult("\"done\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":42}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("done");
            tools.ObservedRequests.Should().HaveCount(2);
            tools.ObservedRequests[0].OperationKey.Should().EndWith("/call-1");
            tools.ObservedRequests[1].OperationKey.Should().EndWith("/call-2");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Model_call_bound_stops_before_a_proposed_tool()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 1, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Tool_call_bound_stops_before_a_second_proposed_tool()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 4, modelCalls: 4, toolCalls: 1), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [
                        new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"),
                        new InferenceToolCallProposal("call-2", Search, "{\"q\":2}"),
                    ])
                : new InferenceFinalCandidateResult("\"unreachable\"")));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Total_token_bound_stops_before_the_next_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(TokenLimit(total: 1), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) => ValueTask.FromResult<InferenceTurnResult>(
            new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")],
                new InferenceTurnUsage(100, 100, 200))));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Cost_bound_stops_before_the_next_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(CostLimit(microunits: 1), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) => ValueTask.FromResult<InferenceTurnResult>(
            new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")],
                new InferenceTurnUsage(5, 3, 8, new InferenceCostEvidence("USD", 5, isEstimated: false)))));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Unknown_cost_stops_before_the_next_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(CostLimit(microunits: 100_000), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) => ValueTask.FromResult<InferenceTurnResult>(
            new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")],
                new InferenceTurnUsage(5, 3, 8, cost: null))));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Tool_argument_byte_ceiling_rejects_before_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(
            new InferenceProtocolLimits(
                maxTurns: 3, maxModelCalls: 3, maxToolCalls: 2,
                maxToolArgumentBytes: 4, maxDurationMilliseconds: 60_000),
            [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"query\":\"long\"}"));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Tool_result_byte_ceiling_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(
            new InferenceProtocolLimits(
                maxTurns: 3, maxModelCalls: 3, maxToolCalls: 2,
                maxToolResultBytes: 8, maxDurationMilliseconds: 60_000),
            [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":42}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Duration_budget_stops_the_activity()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2, durationMs: 50), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder(async (_, token) =>
        {
            await Task.Delay(300, token);
            return new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")]);
        });
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Interaction_identity_changes_when_admitted_plan_changes()
    {
        var ct = TestContext.Current.CancellationToken;

        async Task<string> RunForToolsAsync(IReadOnlyList<DescriptorReference> tools)
        {
            var plan = CreatePlan(Limits(turns: 2, modelCalls: 2, toolCalls: 2), tools);
            var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"ok\"", ExactUsage());
            var tools2 = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
            var sink = new RecordingEvidenceSink();
            var registration = await RegisterAsync(plan, turns, tools2, evidenceSink: sink, ct: ct);
            var root = NewRoot();
            try
            {
                await using var engine = CreateEngine(root, registration);
                using var input = JsonDocument.Parse("\"q\"");
                var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
                await engine.ExecuteAsync(runId, ct);
                return sink.Items.Should().ContainSingle().Subject.InteractionId;
            }
            finally { DeleteDirectory(root); }
        }

        var withTools = await RunForToolsAsync([Search]);
        var withoutTools = await RunForToolsAsync([]);
        withTools.Should().NotBe(withoutTools);
    }

    [Fact]
    public async Task Marang_planning_scenario_runs_the_deterministic_three_model_two_tool_trace()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(
            new InferenceProtocolLimits(
                maxTurns: 5, maxModelCalls: 5, maxToolCalls: 4, maxTotalTokens: 20_000,
                maxDurationMilliseconds: 120_000),
            [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal switch
            {
                0 => new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("model-call-1", Search, "{\"query\":\"InferenceExecutionRequest\",\"maxResults\":10}")],
                    ExactUsage()),
                1 => new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("model-call-2", Search, "{\"path\":\"src/InferenceExecutionRequest.cs\"}")],
                    ExactUsage()),
                _ => new InferenceFinalCandidateResult("\"bounded plan\"", ExactUsage()),
            }));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(
            Search, Json("{\"matches\":[{\"path\":\"src/InferenceExecutionRequest.cs\",\"line\":1}]}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"Prepare a bounded implementation plan.\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            turns.ObservedRequests.Should().HaveCount(3);
            tools.ObservedRequests.Should().HaveCount(2);
            tools.ObservedRequests[0].OperationKey.Should().EndWith("/model-call-1");
            tools.ObservedRequests[1].OperationKey.Should().EndWith("/model-call-2");

            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure.Should().BeNull();
            evidence.Operations.Should().HaveCount(3 + 1 + 2); // three turns, one validation, two tool ops
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.ModelTurn).Should().Be(3);
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.ToolCall).Should().Be(2);
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.Validation).Should().Be(1);
            evidence.ToolOutcomes.Should().HaveCount(2);
            evidence.CommitmentUncertainty.Should().BeFalse();
            evidence.Failure.Should().BeNull();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Coordinated_inference_runs_inside_a_repeat_region()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreateRepeatPlan(Limits(turns: 2, modelCalls: 2, toolCalls: 2));
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
            ValueTask.FromResult<InferenceTurnResult>(
                new InferenceFinalCandidateResult("\"iteration-done\"", ExactUsage())));
        var sink = new RecordingEvidenceSink();
        var readTools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterNamedAsync(plan, "coordloop", turns, readTools, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"seed\"");
            var runId = await engine.StartAsync("coordloop", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString()
                .Should().Be("iteration-done");
            turns.ObservedRequests.Should().HaveCount(2);
            turns.ObservedRequests.Select(request => request.InteractionId).Distinct().Should().HaveCount(2);
            sink.Items.Should().HaveCount(2).And.OnlyContain(evidence => evidence.Failure == null);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Model_turn_can_finalize_after_the_last_tool_budget_is_used()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2, toolCalls: 1), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], ExactUsage())
                : new InferenceFinalCandidateResult("\"done\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("done");
            tools.ObservedRequests.Should().ContainSingle();
            turns.ObservedRequests.Should().HaveCount(2);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task One_call_inference_without_protocol_still_registers()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var plan = baseline with
        {
            Nodes = baseline.Nodes
                .Select(node => node is InferenceNode inference ? inference with { Protocol = null } : node)
                .ToArray(),
        };
        var admission = await AdmitAsync(plan, ct);

        // No turn executor is configured, so the node keeps the one-call path
        // and registration must not require aggregate protocol limits.
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new UnusedContext(),
                    CurrentInferenceFixture.WithPreflight(new UnusedInference())))
            .CreateAsync("coord", "1", admission, ct);

        registration.Definition.ExecutionFingerprint.Should().Be(admission.Receipt.ExecutionFingerprint);
    }

    [Fact]
    public async Task Crash_before_the_first_model_completion_reruns_the_turn_under_the_same_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var turns = new FirstTurnBlockingExecutor();
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            var engine1 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine1.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var interrupted = engine1.ExecuteAsync(runId, interruption.Token);
            await turns.Entered.Task.WaitAsync(ct);
            await interruption.CancelAsync();
            try { await interrupted; }
            catch (OperationCanceledException) { }
            await engine1.DisposeAsync();

            await Task.Delay(350, ct);
            await using var engine2 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            await engine2.RunAvailableAsync(ct);
            var output = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("recovered");
            turns.Calls.Should().Be(2);
            sink.Items.Should().Contain(evidence => evidence.Failure == null);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Per_call_completion_bound_fails_closed_when_exceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2), perCall: new InferenceLimits(MaxTokens: 10, TimeoutSeconds: null));
        var turns = DeterministicFakeTurnExecutor.FinalCandidate(
            "\"hi\"", new InferenceTurnUsage(promptTokens: 5, completionTokens: 50, totalTokens: 55));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure.Should().NotBeNull();
            evidence.Failure!.Code.Should().Be(ExecutionFailureCode.PerCallLimitExceeded);
            turns.ObservedRequests.Should().ContainSingle()
                .Which.MaxCompletionTokens.Should().Be(10);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Per_call_completion_bound_with_unknown_usage_stops_as_budget_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2), perCall: new InferenceLimits(MaxTokens: 100, TimeoutSeconds: null));
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"", usage: null);
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            sink.Items.Should().ContainSingle().Subject.Failure!.Code
                .Should().Be(ExecutionFailureCode.BudgetUnknown);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Per_call_timeout_fails_closed_when_the_turn_overruns()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2), perCall: new InferenceLimits(MaxTokens: null, TimeoutSeconds: 1));
        var turns = DeterministicFakeTurnExecutor.FromResponder(async (_, token) =>
        {
            await Task.Delay(1500, token);
            return new InferenceFinalCandidateResult("\"late\"");
        });
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            sink.Items.Should().ContainSingle().Subject.Failure!.Code
                .Should().Be(ExecutionFailureCode.Timeout);
            turns.ObservedRequests.Should().ContainSingle()
                .Which.TimeoutSeconds.Should().Be(1);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Template_prompt_with_protocol_is_rejected_at_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var template = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));
        var inferPath = StructuralNodeIdentity.Create("coord", "infer");
        var returnPath = StructuralNodeIdentity.Create("coord", "return_result");
        var plan = new WorkflowPlanBuilder("coord", "1", Text, Text, "routing/1")
            .AddCatalogueBinding(Profile)
            .AddCatalogueBinding(template)
            .AddNode(new InferenceNode(
                "infer", inferPath, Profile, template,
                [new ArgumentBinding("request", new InputBinding([]))],
                [], Text, [],
                Tools: null,
                Protocol: new InferenceProtocol(Limits(turns: 2, modelCalls: 2))))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("coord", [
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
        var admission = await AdmitAsync(plan, ct);
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"");

        Func<Task> act = async () => await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(),
                    CurrentInferenceFixture.WithPreflight(new UnusedInference()),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns))
            .CreateAsync("coord", "1", admission, ct);

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*workflow-owned prompt*");
    }

    [Fact]
    public async Task Coordinated_inference_inside_fan_out_is_rejected_at_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var fanOutPath = StructuralNodeIdentity.Create("batch", "process");
        var answerPath = $"{fanOutPath}/$body/answer";
        var returnPath = StructuralNodeIdentity.Create("batch", "return");
        var list = new ListType(Text, 2);
        var plan = new WorkflowPlanBuilder("batch", "1", list, list, "routing/1")
            .AddCatalogueBinding(Profile)
            .AddCatalogueBinding(Search)
            .AddPrompt(AnswerPrompt())
            .AddFanOut(new FanOutNode(
                "process", fanOutPath, new InputBinding([]), new FanOutItemBinding("item", Text),
                new FanOutItemValueBinding([]),
                [new InferenceNode(
                    "answer", answerPath, Profile, null, [], [], Text, [],
                    "answer_prompt",
                    [new PromptBinding("question", new FanOutItemValueBinding([]))],
                    Tools: [Search],
                    Protocol: new InferenceProtocol(Limits(turns: 2, modelCalls: 2, toolCalls: 2)))],
                new NodeOutputBinding(answerPath, []), list, 2, 2))
            .AddNode(new ReturnNode("return", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("batch", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([answerPath]),
                ]),
            ]))
            .Build();
        var admission = await AdmitAsync(plan, ct);
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"");

        Func<Task> act = async () => await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(),
                    CurrentInferenceFixture.WithPreflight(new UnusedInference()),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns))
            .CreateAsync("batch", "1", admission, ct);

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*fan-out*");
    }

    [Fact]
    public async Task Turn_manifest_is_preferred_over_the_one_call_manifest_at_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        // The one-call manifest below accepts the tool; the turn manifest does
        // not, and the turn manifest must win for coordinated nodes.
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2, toolCalls: 2), [Search]);
        var admission = await AdmitAsync(plan, ct);
        var turns = new RestrictiveTurnManifestExecutor();

        Func<Task> act = async () => await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(),
                    CurrentInferenceFixture.WithPreflight(new UnusedInference()),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns))
            .CreateAsync("coord", "1", admission, ct);

        // The one-call manifest would accept this requirement; only the turn
        // manifest's missing bindings can produce this failure.
        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>();
        turns.TurnFeatureManifest.Profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Turn_manifest_allows_registration_without_a_one_call_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var admission = await AdmitAsync(plan, ct);
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"");

        // UnusedInference carries no manifest or preflight hook; the fake turn
        // executor's accept-all manifest must satisfy registration alone.
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(), new UnusedInference(),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns))
            .CreateAsync("coord", "1", admission, ct);

        registration.Definition.ExecutionFingerprint.Should().Be(admission.Receipt!.ExecutionFingerprint);
    }

    [Fact]
    public async Task Replay_of_a_committed_protocol_does_not_recall_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"", ExactUsage());
        var registration = await RegisterAsync(plan, turns, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("hi");
            turns.ObservedRequests.Should().ContainSingle();

            // Re-executing the completed run reuses committed loop steps and
            // must not issue a second paid model turn.
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("hi");
            turns.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>
    /// A turn executor whose manifest deliberately binds nothing, proving the
    /// factory prefers the turn manifest over the one-call manifest.
    /// </summary>
    private sealed class RestrictiveTurnManifestExecutor : IInferenceTurnExecutor, IInferenceTurnExecutorManifest
    {
        private readonly InferenceFeatureManifest manifest;

        public RestrictiveTurnManifestExecutor()
        {
            manifest = new InferenceFeatureManifest(
                [InferencePromptForm.WorkflowOwned],
                [InferenceModality.StructuredText],
                supportsContextDelivery: false,
                maximumContextPayloadUtf8Bytes: null,
                [InferenceToolEffect.ReadOnly],
                [new InferenceLimit(InferenceLimitDimension.Turns, 10)],
                InferenceRecoveryQuality.Unsupported,
                InferenceUsageQuality.Unknown,
                InferencePricingQuality.Unknown);
        }

        public InferenceFeatureManifest TurnFeatureManifest => manifest;

        public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement) =>
            InferencePreflight.Evaluate(requirement, manifest);

        public ValueTask<InferenceTurnResult> ExecuteTurnAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Turn execution should not be reached after failed preflight.");
    }

    /// <summary>Blocks on the first model turn, then succeeds on every retry.</summary>
    private sealed class FirstTurnBlockingExecutor : IInferenceTurnExecutor
    {
        private int calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);

        public async ValueTask<InferenceTurnResult> ExecuteTurnAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new InferenceFinalCandidateResult("\"recovered\"", ExactUsage());
        }
    }
}
