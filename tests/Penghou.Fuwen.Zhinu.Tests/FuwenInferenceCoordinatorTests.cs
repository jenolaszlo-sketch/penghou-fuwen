using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>
/// CI-4 durable complex-inference vertical slice: one logical <c>infer</c> node
/// runs a bounded model → tool → model protocol as durable loop-iteration
/// steps, enforces effective aggregate bounds, and resumes without duplicating
/// completed work.
/// </summary>
public sealed partial class FuwenInferenceCoordinatorTests
{
    private static readonly PrimitiveType Text = new(FuwenPrimitiveKind.String);
    private static readonly DescriptorReference Profile =
        new(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a'));
    private static readonly DescriptorReference Search =
        new(DescriptorKind.Tool, "sample.search", "1", Digest('c'));

    private static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));

    private static PromptDefinition AnswerPrompt() => new(
        "answer_prompt",
        [new PromptParameter("question", Text)],
        [
            new PromptMessage(PromptMessageRole.System, "Answer concisely."),
            new PromptMessage(PromptMessageRole.User, "{{ question }}"),
        ]);

    private static InferenceProtocolLimits Limits(
        long turns,
        long modelCalls,
        long? toolCalls = null,
        long? totalTokens = null,
        long? promptTokens = null,
        long? completionTokens = null,
        long durationMs = 60_000) => new(
            maxTurns: turns,
            maxModelCalls: modelCalls,
            maxToolCalls: toolCalls,
            maxPromptTokens: promptTokens,
            maxCompletionTokens: completionTokens,
            maxTotalTokens: totalTokens,
            maxDurationMilliseconds: durationMs);

    private static WorkflowPlan CreatePlan(
        InferenceProtocolLimits limits,
        IReadOnlyList<DescriptorReference>? tools = null,
        InferenceLimits? perCall = null)
    {
        var inferPath = StructuralNodeIdentity.Create("coord", "infer");
        var returnPath = StructuralNodeIdentity.Create("coord", "return_result");
        var builder = new WorkflowPlanBuilder("coord", "1", Text, Text, "routing/1")
            .AddCatalogueBinding(Profile)
            .AddPrompt(AnswerPrompt())
            .AddNode(new InferenceNode(
                "infer",
                inferPath,
                Profile,
                null,
                [],
                [],
                Text,
                [],
                "answer_prompt",
                [new PromptBinding("question", new InputBinding([]))],
                Tools: tools,
                Limits: perCall,
                Protocol: new InferenceProtocol(limits)))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("coord", [
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
        if (tools is not null)
            foreach (var tool in tools)
                builder.AddCatalogueBinding(tool);
        return builder.Build();
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan, CancellationToken ct)
    {
        var catalogue = plan.CatalogueBindings
            .Select(descriptor => new TrustedCatalogueDescriptor(
                descriptor,
                callableContract: descriptor.Kind is DescriptorKind.InferenceProfile or DescriptorKind.Tool
                    ? new CallableContract(
                        new CallableSignature([new CallableParameter("request", Text)], Text),
                        CallableEffect.Read,
                        CallableIdempotency.Idempotent,
                        CallableRetrySafety.Safe)
                    : null))
            .ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        return admission;
    }

    private static async Task<FuwenZhinuWorkflowRegistration> RegisterAsync(
        WorkflowPlan plan,
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
            .CreateAsync("coord", "1", admission, ct);
    }

    private static WorkflowEngine CreateEngine(
        string root,
        FuwenZhinuWorkflowRegistration registration,
        TimeSpan? leaseDuration = null) =>
        CreateEngine(CreateStore(Path.Combine(root, "workflow.db")), registration, leaseDuration);

    private static WorkflowEngine CreateEngine(
        SqliteWorkflowStore store,
        FuwenZhinuWorkflowRegistration registration,
        TimeSpan? leaseDuration = null) =>
        new(
            store,
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions
            {
                LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(5),
            });

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement.Clone());
    }

    private static InferenceTurnUsage ExactUsage(int prompt = 5, int completion = 3) =>
        new(prompt, completion, prompt + completion);

    [Fact]
    public async Task Zero_tool_one_turn_produces_a_validated_typed_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
            ValueTask.FromResult<InferenceTurnResult>(
                new InferenceFinalCandidateResult("\"hi\"", ExactUsage())));
        var registration = await RegisterAsync(plan, turns, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("hi");
            turns.ObservedRequests.Should().ContainSingle();
            turns.ObservedRequests[0].VisibleTools.Should().BeEmpty();
            turns.ObservedRequests[0].TurnOrdinal.Should().Be(0);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task One_tool_multi_turn_feeds_the_result_back_and_validates_output()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], ExactUsage())
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
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("done");
            turns.ObservedRequests.Should().HaveCount(2);
            tools.ObservedRequests.Should().ContainSingle();
            var operationKey = tools.ObservedRequests[0].OperationKey;
            operationKey.Should().StartWith(turns.ObservedRequests[0].InteractionId + "/tool/");
            operationKey.Should().EndWith("/call-1");

            var secondTurn = turns.ObservedRequests[1];
            secondTurn.Conversation.Should().Contain(message =>
                message.Role == InferenceTurnRole.Tool && message.ToolCallId == "call-1");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Crash_after_a_completed_tool_reuses_its_step_and_only_repeats_the_in_flight_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2), [Search]);
        var turns = new BlockingTurnExecutor();
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":42}"));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            var engine1 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine1.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var interrupted = engine1.ExecuteAsync(runId, interruption.Token);
            await turns.EnteredSecondTurn.Task.WaitAsync(ct);
            await interruption.CancelAsync();
            try { await interrupted; }
            catch (OperationCanceledException) { }
            await engine1.DisposeAsync();

            await Task.Delay(350, ct);
            await using var engine2 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            await engine2.RunAvailableAsync(ct);
            var output = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("done");
            turns.TurnZeroCalls.Should().Be(1);
            turns.TurnOneCalls.Should().Be(2);
            tools.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Turn_bound_stops_before_the_model_turn_and_never_executes_a_tool()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 1, modelCalls: 1, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));
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
            turns.ObservedRequests.Should().ContainSingle();
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Unknown_usage_with_a_finite_token_bound_stops_before_the_next_paid_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2, totalTokens: 1_000), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], usage: null)
                : new InferenceFinalCandidateResult("\"unreachable\"")));
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
            turns.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Host_turn_ceiling_narrows_the_authored_bound()
    {
        var ct = TestContext.Current.CancellationToken;
        // The authored plan allows five turns, but the host ceiling forbids the
        // second turn that the tool proposal would require.
        var plan = CreatePlan(Limits(turns: 5, modelCalls: 5, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")])
                : new InferenceFinalCandidateResult("\"unreachable\"")));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":42}"));
        var ceilings = new InferenceLimitSet([new InferenceLimit(InferenceLimitDimension.Turns, 1)]);
        var registration = await RegisterAsync(plan, turns, tools, ceilings, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            turns.ObservedRequests.Should().ContainSingle();
            tools.ObservedRequests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Validation_failure_rejects_a_final_candidate_that_fails_its_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("42");
        var registration = await RegisterAsync(plan, turns, ct: ct);
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
    public async Task Mistyped_tool_arguments_fail_closed_before_tool_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "not-json"));
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
    public async Task Ambiguous_tool_result_fails_closed_without_a_following_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")])
                : new InferenceFinalCandidateResult("\"unreachable\"")));
        var tools = new DeterministicFakeReadToolExecutor().Register(Search, request =>
            InferenceReadToolResult.Failed(
                new ExecutionFailure(
                    ExecutionFailureKind.Provider,
                    ExecutionFailureCode.ProviderError,
                    "ambiguous",
                    mayHaveCommittedEffect: true),
                new InferenceReadToolEvidence(Search, request.OperationKey)));
        var registration = await RegisterAsync(plan, turns, tools, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            turns.ObservedRequests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Missing_read_tool_executor_is_rejected_at_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));

        Func<Task> act = async () => await RegisterAsync(plan, turns, readToolExecutor: null, ct: ct);

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*no read-tool executor*");
    }

    [Fact]
    public async Task Cancellation_marks_the_run_cancelled_and_observes_it_in_the_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = false;
        var turns = DeterministicFakeTurnExecutor.FromResponder(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                observedCancellation = true;
                throw;
            }
            throw new InvalidOperationException("unreachable");
        });
        var registration = await RegisterAsync(plan, turns, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            var executing = engine.ExecuteAsync(runId, ct);
            await entered.Task.WaitAsync(ct);
            await engine.CancelAsync(runId, ct);
            await executing;

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
            observedCancellation.Should().BeTrue();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Evidence_is_recorded_on_success_with_operations_and_tool_outcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2, totalTokens: 10_000), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((request, _) => ValueTask.FromResult<InferenceTurnResult>(
            request.TurnOrdinal == 0
                ? new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], ExactUsage())
                : new InferenceFinalCandidateResult("\"done\"", ExactUsage())));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":42}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("done");

            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.InteractionId.Should().NotBeNullOrEmpty();
            evidence.Semantics.Should().Be(InferenceProtocolEvidence.CurrentSemantics);
            evidence.Failure.Should().BeNull();
            evidence.CommitmentUncertainty.Should().BeFalse();
            evidence.RecoveryDisposition.Should().Be(InferenceRecoveryDisposition.Fresh);
            evidence.Operations.Should().Contain(operation => operation.Kind == InferenceOperationKind.ModelTurn && operation.Disposition == InferenceOperationDisposition.Succeeded);
            evidence.Operations.Should().Contain(operation => operation.Kind == InferenceOperationKind.Validation && operation.Disposition == InferenceOperationDisposition.Succeeded);
            evidence.ToolOutcomes.Should().ContainSingle();
            evidence.ToolOutcomes[0].Tool.Should().Be(Search);
            evidence.ToolOutcomes[0].ResultUtf8Bytes.Should().BeGreaterThan(0);
            evidence.ToolOutcomes[0].ResultDigest.Should().NotBeNull();
            evidence.UsageQuality.Should().Be(InferenceUsageQuality.Exact);
            evidence.EffectiveLimits.GetMaximum(InferenceLimitDimension.TotalTokens).Should().Be(10_000);
            evidence.ProtectedPayloads.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Evidence_records_the_terminal_failure_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 1, modelCalls: 1, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));
        var tools = new DeterministicFakeReadToolExecutor().RegisterSuccess(Search, Json("{\"answer\":1}"));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, tools, evidenceSink: sink, ct: ct);
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
            evidence.Failure!.Code.Should().Be(ExecutionFailureCode.TurnLimitExceeded);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Ambiguous_tool_marks_commitment_uncertainty()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 3, modelCalls: 3, toolCalls: 2), [Search]);
        var turns = DeterministicFakeTurnExecutor.ToolCalls(
            new InferenceToolCallProposal("call-1", Search, "{\"q\":1}"));
        var tools = new DeterministicFakeReadToolExecutor().Register(Search, _ =>
            InferenceReadToolResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError,
                "ambiguous", mayHaveCommittedEffect: true)));
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(plan, turns, tools, evidenceSink: sink, ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.CommitmentUncertainty.Should().BeTrue();
            evidence.RecoveryDisposition.Should().Be(InferenceRecoveryDisposition.Ambiguous);
            evidence.ToolOutcomes.Should().Contain(outcome => outcome.MayHaveCommittedEffect);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Evidence_sink_failure_does_not_alter_execution_truth()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2));
        var turns = DeterministicFakeTurnExecutor.FinalCandidate("\"hi\"", ExactUsage());
        var registration = await RegisterAsync(plan, turns, evidenceSink: new ThrowingEvidenceSink(), ct: ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("hi");
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class RecordingEvidenceSink : IInferenceEvidenceSink
    {
        public List<InferenceProtocolEvidence> Items { get; } = [];

        public ValueTask RecordAsync(InferenceProtocolEvidence evidence, CancellationToken cancellationToken = default)
        {
            Items.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingEvidenceSink : IInferenceEvidenceSink
    {
        public ValueTask RecordAsync(InferenceProtocolEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("evidence sink is unreachable");
    }

    private static SqliteWorkflowStore CreateStore(string databasePath) =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(2),
        });

    /// <summary>
    /// Turn zero commits a tool proposal; the first second-turn attempt blocks so
    /// the run can be interrupted mid-turn, and every later second-turn attempt
    /// returns the final candidate so recovery can complete.
    /// </summary>
    private sealed class BlockingTurnExecutor : IInferenceTurnExecutor
    {
        private int turnZero;
        private int turnOne;

        public TaskCompletionSource EnteredSecondTurn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TurnZeroCalls => Volatile.Read(ref turnZero);
        public int TurnOneCalls => Volatile.Read(ref turnOne);

        public async ValueTask<InferenceTurnResult> ExecuteTurnAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default)
        {
            if (request.TurnOrdinal == 0)
            {
                Interlocked.Increment(ref turnZero);
                return new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")]);
            }

            var ordinalOneCall = Interlocked.Increment(ref turnOne);
            if (ordinalOneCall == 1)
            {
                EnteredSecondTurn.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new InferenceFinalCandidateResult("\"done\"");
        }
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The one-call inference executor should not be used by the coordinator.");
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity executor should not be called.");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context provider should not be called.");
    }
}
