using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.MarangConsumer.Tests;

// End-to-end proof that Marang can supervise one bounded complex inference as
// a consumer of only published Fuwen packages: compile, explain, preflight,
// admit, run, fail, resume, inspect evidence, restart, and receive a typed
// planning result plus a separately authorized promotion.
public sealed class MarangPlanningTests
{
    private static async Task<(WorkflowAdmissionResult Admission, string PromptDigest)> AdmitAsync(
        CancellationToken ct)
    {
        var compiled = await new FuwenSourceCompiler(MarangScenario.Catalogue())
            .CompileAsync(MarangScenario.Source, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        compiled.Plan.Should().NotBeNull();

        var explanation = WorkflowExplanation.Create(compiled.Compilation);
        explanation.CompilationSucceeded.Should().BeTrue();

        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                MarangScenario.Catalogue(),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        var digest = compiled.Plan!.Prompts!
            .Single(prompt => prompt.Name == "marang_planning")
            .GetSemanticDigest();
        var admissionExplanation = WorkflowExplanation.Create(admission);
        admissionExplanation.ExecutionFingerprint.Should().Be(admission.Receipt!.ExecutionFingerprint);
        return (admission, digest);
    }

    private static InferenceFeatureManifest ManifestFor(string promptDigest) => new(
        [InferencePromptForm.WorkflowOwned],
        [InferenceModality.StructuredText],
        supportsContextDelivery: true,
        maximumContextPayloadUtf8Bytes: 16384,
        [InferenceToolEffect.ReadOnly],
        [
            new InferenceLimit(InferenceLimitDimension.Turns, 1000000),
            new InferenceLimit(InferenceLimitDimension.ModelCalls, 1000000),
            new InferenceLimit(InferenceLimitDimension.ToolCalls, 1000000),
            new InferenceLimit(InferenceLimitDimension.PromptTokens, 1000000),
            new InferenceLimit(InferenceLimitDimension.CompletionTokens, 1000000),
            new InferenceLimit(InferenceLimitDimension.TotalTokens, 1000000),
            new InferenceLimit(InferenceLimitDimension.DurationMilliseconds, 1000000),
            new InferenceLimit(InferenceLimitDimension.ToolArgumentBytes, 1000000),
            new InferenceLimit(InferenceLimitDimension.ToolResultBytes, 1000000),
        ],
        InferenceRecoveryQuality.Unsupported,
        InferenceUsageQuality.Unknown,
        InferencePricingQuality.Unknown,
        supportsStructuredOutput: true,
        profiles: [MarangDescriptors.Profile],
        tools: [MarangDescriptors.Search, MarangDescriptors.ReadFile, MarangDescriptors.Impact],
        workflowPromptDigests: [promptDigest]);

    private static async Task<FuwenZhinuWorkflowRegistration> RegisterAsync(
        WorkflowAdmissionResult admission,
        MarangPlanningTurns turns,
        MarangRepositoryTools tools,
        MarangWorkspaceProvider workspace,
        MarangPromotionActivity promotion,
        MarangEvidenceSink sink,
        CancellationToken ct) =>
        await RegisterWithForkPolicyAsync(admission, turns, tools, workspace, promotion, sink, null, ct);

    private static async Task<FuwenZhinuWorkflowRegistration> RegisterAsync(
        WorkflowAdmissionResult admission,
        MarangPlanningTurns turns,
        MarangRepositoryTools tools,
        MarangWorkspaceProvider workspace,
        MarangPromotionActivity promotion,
        MarangEvidenceSink sink,
        IReadOnlySet<string> priorFingerprints,
        CancellationToken ct) =>
        await RegisterWithForkPolicyAsync(admission, turns, tools, workspace, promotion, sink, priorFingerprints, ct);

    private static async Task<FuwenZhinuWorkflowRegistration> RegisterWithForkPolicyAsync(
        WorkflowAdmissionResult admission,
        MarangPlanningTurns turns,
        MarangRepositoryTools tools,
        MarangWorkspaceProvider workspace,
        MarangPromotionActivity promotion,
        MarangEvidenceSink sink,
        IReadOnlySet<string>? priorFingerprints,
        CancellationToken ct) =>
        await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    promotion,
                    workspace,
                    new MarangUnusedInference(),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(),
                    turns,
                    tools,
                    MarangScenario.HostCeilings(),
                    sink)
                {
                    PriorExecutionFingerprints = priorFingerprints ?? new HashSet<string>(StringComparer.Ordinal),
                })
            .CreateAsync(MarangScenario.WorkflowName, "1", admission, ct);

    private static WorkflowEngine CreateEngine(string root, FuwenZhinuWorkflowRegistration registration, TimeSpan? lease = null) =>
        new(
            new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
                BusyTimeout = TimeSpan.FromSeconds(2),
            }),
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions
            {
                LeaseDuration = lease ?? TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(5),
            });

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-marang", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static JsonElement Input(string objective)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(objective));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Full_lifecycle_returns_typed_planning_result_and_promotes_explicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        var manifest = ManifestFor(digest);

        // Marang explains executability before paying for anything.
        var requirement = new InferenceExecutionRequirement(
            MarangDescriptors.Profile, null, digest,
            [MarangDescriptors.Search, MarangDescriptors.ReadFile, MarangDescriptors.Impact],
            hasContextInputs: true);
        var report = manifest.Preflight(requirement);
        report.IsExecutable.Should().BeTrue();
        var rendered = InferencePreflightReportRenderer.RenderHuman(report);
        rendered.Should().Contain("executable");
        InferencePreflightReportRenderer.RenderJson(report).Should().Contain("\"executable\":true");

        var turns = MarangPlanningTurns.SuccessTrace(manifest);
        var tools = new MarangRepositoryTools();
        var workspace = new MarangWorkspaceProvider();
        var promotion = new MarangPromotionActivity();
        var sink = new MarangEvidenceSink();
        var registration = await RegisterAsync(admission, turns, tools, workspace, promotion, sink, ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            var runId = await engine.StartAsync(
                MarangScenario.WorkflowName, "1", Input(MarangScenario.Objective), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            // Typed planning result, not a free-form string.
            output.GetProperty("summary").GetString().Should().NotBeNullOrEmpty();
            output.GetProperty("affectedPaths").EnumerateArray().Should().NotBeEmpty();
            output.GetProperty("verification").EnumerateArray().Should().NotBeEmpty();
            output.GetProperty("proposedMutation").GetString().Should().Be("update the implementation plan documentation");
            output.GetProperty("requiresExplicitActivity").GetBoolean().Should().BeTrue();

            // The exact allowlist is what the model saw, nothing more.
            turns.Requests.Should().HaveCount(3);
            turns.Requests.Select(request => request.TurnOrdinal).Should().Equal(0, 1, 2);
            turns.Requests[0].VisibleTools.Select(tool => tool.Descriptor.Name)
                .Should().BeEquivalentTo("marang.repo.search", "marang.repo.readFile", "marang.graph.impact");
            turns.Requests[2].Conversation.Should().Contain(message =>
                message.Role == InferenceTurnRole.Tool && message.ToolCallId == "model-call-2");

            // Authored per-call bounds reach the transport on every turn.
            turns.Requests.Should().OnlyContain(request =>
                request.MaxCompletionTokens == 800 && request.TimeoutSeconds == 30);
            // Every tool call carries its exact scope and the result ceiling.
            tools.Requests.Should().OnlyContain(request =>
                request.Scope == $"{request.Tool.Name}@{request.Tool.Version}" &&
                request.MaximumResultUtf8Bytes == 24000);

            // The mutation was promoted only by the explicit activity step.
            promotion.Committed.Should().ContainSingle()
                .Which.Should().Be(("update the implementation plan documentation", true));

            // Evidence answers why: three turns, two tools, one validation.
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure.Should().BeNull();
            evidence.CommitmentUncertainty.Should().BeFalse();
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.ModelTurn).Should().Be(3);
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.ToolCall).Should().Be(2);
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.Validation).Should().Be(1);
            evidence.ToolOutcomes.Should().HaveCount(2);
            evidence.ToolOutcomes.Select(outcome => outcome.Tool.Name)
                .Should().Equal("marang.repo.search", "marang.repo.readFile");
            evidence.ToolOutcomes.Should().OnlyContain(outcome =>
                outcome.ResultDigest != null && outcome.ResultUtf8Bytes > 0);
            evidence.UsageQuality.Should().Be(InferenceUsageQuality.Exact);
            evidence.TotalTokens.Should().Be(1260 + 1470 + 1720);
            // Source bounds win over the wider Marang host ceilings.
            evidence.EffectiveLimits.GetMaximum(InferenceLimitDimension.TotalTokens).Should().Be(15000);
            evidence.EffectiveLimits.GetMaximum(InferenceLimitDimension.Turns).Should().Be(3);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Unsupported_tool_combination_fails_preflight_before_paid_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        _ = admission;
        var manifest = ManifestFor(digest);
        var unknown = new DescriptorReference(
            DescriptorKind.Tool, "marang.evil.write", "1", new ContentDigest("sha256", "descriptor/v1", new string('7', 64)));
        var requirement = new InferenceExecutionRequirement(
            MarangDescriptors.Profile, null, digest, [unknown], hasContextInputs: true);

        var report = manifest.Preflight(requirement);

        report.IsExecutable.Should().BeFalse();
        report.MissingBindings.Should().Contain("marang.evil.write");
        report.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Tool_failure_fails_the_run_without_promotion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        var manifest = ManifestFor(digest);
        var turns = MarangPlanningTurns.SuccessTrace(manifest);
        var tools = new MarangRepositoryTools(request => request.Tool.Name == "marang.repo.search"
            ? InferenceReadToolResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, "search unavailable"))
            : null);
        var workspace = new MarangWorkspaceProvider();
        var promotion = new MarangPromotionActivity();
        var sink = new MarangEvidenceSink();
        var registration = await RegisterAsync(admission, turns, tools, workspace, promotion, sink, ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            var runId = await engine.StartAsync(
                MarangScenario.WorkflowName, "1", Input(MarangScenario.Objective), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            promotion.Committed.Should().BeEmpty();
            sink.Items.Should().ContainSingle().Subject.Failure.Should().NotBeNull();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Crash_after_tool_completion_resumes_without_duplicate_effects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        var manifest = ManifestFor(digest);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTurnCalls = 0;
        var turns = new MarangPlanningTurns(manifest, async (request, token) =>
        {
            if (request.TurnOrdinal == 1 && Interlocked.Increment(ref secondTurnCalls) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return request.TurnOrdinal switch
            {
                0 => new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal(
                        "model-call-1", MarangDescriptors.Search,
                        """{"query":"x","pathPrefix":"src","maxResults":10}""")],
                    new InferenceTurnUsage(1200, 60, 1260)),
                1 => new InferenceToolCallTurnResult(
                    [new InferenceToolCallProposal(
                        "model-call-2", MarangDescriptors.ReadFile,
                        """{"path":"src/a.cs","startLine":1,"endLine":80}""")],
                    new InferenceTurnUsage(1400, 70, 1470)),
                _ => new InferenceFinalCandidateResult(
                    """{"summary":"recovered","affectedPaths":["src/a.cs"],"verification":["dotnet test"],"proposedMutation":"none","requiresExplicitActivity":true}""",
                    new InferenceTurnUsage(1600, 120, 1720)),
            };
        });
        var tools = new MarangRepositoryTools();
        var workspace = new MarangWorkspaceProvider();
        var promotion = new MarangPromotionActivity();
        var sink = new MarangEvidenceSink();
        var registration = await RegisterAsync(admission, turns, tools, workspace, promotion, sink, ct);
        var root = NewRoot();

        try
        {
            var engine1 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            var runId = await engine1.StartAsync(
                MarangScenario.WorkflowName, "1", Input(MarangScenario.Objective), cancellationToken: ct);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var interrupted = engine1.ExecuteAsync(runId, interruption.Token);
            await entered.Task.WaitAsync(ct);
            await interruption.CancelAsync();
            try { await interrupted; }
            catch (OperationCanceledException) { }
            await engine1.DisposeAsync();

            await Task.Delay(350, ct);
            await using var engine2 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            await engine2.RunAvailableAsync(ct);
            var output = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetProperty("summary").GetString().Should().Be("recovered");
            tools.Requests.Count(request => request.Tool.Name == "marang.repo.search").Should().Be(1);
            tools.Requests.Count(request => request.Tool.Name == "marang.repo.readFile").Should().Be(1);
            turns.Requests.Count(request => request.TurnOrdinal == 0).Should().Be(1);
            // The interrupted turn ran twice under one identity; the completed
            // turn ran once.
            turns.Requests.Count(request => request.TurnOrdinal == 1).Should().Be(2);
            promotion.Committed.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Read_tool_rejects_wrong_scope_and_oversized_results_without_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = new MarangRepositoryTools();
        using var document = JsonDocument.Parse("{}");
        var arguments = RuntimeValue.FromJson(document.RootElement.Clone());

        var wrongScope = new InferenceReadToolRequest(
            MarangDescriptors.Search, arguments, "wrong-scope", "op/scope");
        var scoped = await tools.ExecuteAsync(wrongScope, ct);
        scoped.IsSuccess.Should().BeFalse();
        scoped.Failure!.Code.Should().Be(ExecutionFailureCode.PolicyRejected);

        var tinyCeiling = new InferenceReadToolRequest(
            MarangDescriptors.Search, arguments, "marang.repo.search@1", "op/ceiling",
            maximumResultUtf8Bytes: 8);
        var capped = await tools.ExecuteAsync(tinyCeiling, ct);
        capped.IsSuccess.Should().BeFalse();
        capped.Failure!.Code.Should().Be(ExecutionFailureCode.PayloadLimitExceeded);
    }

    [Fact]
    public async Task Same_fingerprint_fork_reuses_committed_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        var manifest = ManifestFor(digest);
        var turns = MarangPlanningTurns.SuccessTrace(manifest);
        var tools = new MarangRepositoryTools();
        var workspace = new MarangWorkspaceProvider();
        var promotion = new MarangPromotionActivity();
        var sink = new MarangEvidenceSink();
        var registration = await RegisterAsync(
            admission, turns, tools, workspace, promotion, sink,
            new HashSet<string>([admission.Receipt!.ExecutionFingerprint], StringComparer.Ordinal), ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            var runId = await engine.StartAsync(
                MarangScenario.WorkflowName, "1", Input(MarangScenario.Objective), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var first = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            var turnCalls = turns.Requests.Count;

            var forkedId = await engine.ForkAsync(runId, "marang_planning/return_result", cancellationToken: ct);
            await engine.ExecuteAsync(forkedId, ct);
            var forked = await engine.WaitForCompletionAsync<JsonElement>(forkedId, cancellationToken: ct);

            forked.GetRawText().Should().Be(first.GetRawText());
            turns.Requests.Should().HaveCount(turnCalls);
            tools.Requests.Should().HaveCount(2);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Focused_restart_reuses_committed_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, digest) = await AdmitAsync(ct);
        var manifest = ManifestFor(digest);
        var turns = MarangPlanningTurns.SuccessTrace(manifest);
        var tools = new MarangRepositoryTools();
        var workspace = new MarangWorkspaceProvider();
        var promotion = new MarangPromotionActivity();
        var sink = new MarangEvidenceSink();
        var registration = await RegisterAsync(admission, turns, tools, workspace, promotion, sink, ct);
        var root = NewRoot();

        try
        {
            await using var engine = CreateEngine(root, registration);
            var runId = await engine.StartAsync(
                MarangScenario.WorkflowName, "1", Input(MarangScenario.Objective), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct))
                .GetProperty("summary").GetString().Should().NotBeNullOrEmpty();
            var turnCalls = turns.Requests.Count;
            var toolCalls = tools.Requests.Count;
            var promotions = promotion.Committed.Count;

            var steps = await engine.GetStepsAsync(runId, ct);
            var returnStep = steps.Single(step => step.StepKey.EndsWith("/return_result", StringComparison.Ordinal));
            await engine.RestartStepAsync(runId, returnStep.StepKey, ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct))
                .GetProperty("summary").GetString().Should().NotBeNullOrEmpty();

            turns.Requests.Should().HaveCount(turnCalls);
            tools.Requests.Should().HaveCount(toolCalls);
            promotion.Committed.Should().HaveCount(promotions);
        }
        finally { DeleteDirectory(root); }
    }
}
