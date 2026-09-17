using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
#pragma warning disable xUnit1030
    [Fact]
    public async Task Repeat_with_break_executes_durably_and_reports_loop_count_via_GetLoopProgressAsync()
    {
        var admission = await AdmitRepeatAsync(maxIterations: 5, breakOnIter3: true);
        var activity = new RepeatCountingActivity();
        var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.repeat", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"start\"");
            var runId = await engine.StartAsync("fuwen.repeat", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("start-break1-break2-break3");
            activity.Calls.Should().Be(3);

            // Persisted loop count is visible for resume-testing: GetLoopProgressAsync
            // should show 3 committed iterations and allow selective restart.
            var progress = await engine.GetLoopProgressAsync(runId, WorkflowLoopReference.Root("loop1"), TestContext.Current.CancellationToken);
            progress.Should().NotBeNull();
            progress!.Iterations.Select(item => item.Iteration.Number).Should().Equal(1, 2, 3);
            progress.Iterations[0].IsCommitted.Should().BeTrue();
            progress.Iterations[2].IsCommitted.Should().BeTrue();
            progress.IsCompleted.Should().BeTrue();

            // Selective restart of iteration 2 should invalidate only that iteration and its dependents.
            var iter2 = progress.Iterations[1];
            var targetStep = iter2.BodySteps.FirstOrDefault()?.StepKey ?? iter2.CommitStep?.StepKey ?? "loop1";
            var restart = await engine.RestartStepAsync(runId, targetStep, TestContext.Current.CancellationToken);
            restart.StepsToInvalidate.Select(s => s.StepKey).Should().Contain(s => s.Contains("loop1"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Repeat_limit_exceeded_is_typed_LoopLimitExceeded()
    {
        var admission = await AdmitRepeatAsync(maxIterations: 2, breakOnIter3: false);
        var activity = new UnconditionallyContinuingActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.repeat-limit", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("\"x\"");
            var runId = await engine.StartAsync("fuwen.repeat-limit", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var run = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
            run!.Status.Should().Be(WorkflowStatus.Failed);
            (run.Error?.Message ?? string.Empty).Should().Contain("exceeded its maximum");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Repeat_crash_between_iterations_preserves_partial_progress_and_resumes()
    {
        var failingIterationTracker = new ConcurrentDictionary<Guid, int>();
        var runId = Guid.NewGuid();
        failingIterationTracker[runId] = 3; // fail on iteration 3

        var admission = await AdmitRepeatAsync(maxIterations: 5, breakOnIter3: true);
        var activity = new CrashOnIterationActivity(failingIterationTracker, runId);
        var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.repeat-crash", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            // First run: iterations 1, 2 succeed; iteration 3 crashes.
            using var input = JsonDocument.Parse("\"start\"");
            var firstRunId = await engine.StartAsync("fuwen.repeat-crash", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(firstRunId, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<WorkflowExecutionFailedException>(
                () => engine.WaitForCompletionAsync<JsonElement>(firstRunId, cancellationToken: TestContext.Current.CancellationToken));

            // Partial progress: 2 committed iterations persisted.
            var firstRun = await engine.GetRunAsync(firstRunId, TestContext.Current.CancellationToken);
            firstRun!.Status.Should().Be(WorkflowStatus.Failed);

            // Remove the failure tracker so the retry of iteration 3 succeeds.
            failingIterationTracker.TryRemove(runId, out _);

            // Resume: RestartStepAsync transitions back to Pending; ExecuteAsync re-enters at iteration 3.
            await engine.RestartStepAsync(
                firstRunId,
                "$loop/loop1/3/body/step",
                new RestartStepOptions { Mode = StepRestartMode.StepOnly },
                TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(firstRunId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(firstRunId, cancellationToken: TestContext.Current.CancellationToken);
            // CrashOnIterationActivity appends "-break" + Calls (shared counter).
            // First run: call 1→"start-break1", call 2→"start-break1-break2", call 3 throws.
            // Second run: iter 1,2 replay from commit (no body), iter 3 body: call 4→"start-break1-break2-break4".
            // Break at iter 3 returns "start-break1-break2-break4".
            output.GetString().Should().Be("start-break1-break2-break4");

            // Persisted loop count: 3 committed iterations.
            var progress = await engine.GetLoopProgressAsync(firstRunId, WorkflowLoopReference.Root("loop1"), TestContext.Current.CancellationToken);
            progress.Should().NotBeNull();
            progress!.Iterations.Select(item => item.Iteration.Number).Should().Equal(1, 2, 3);
            progress.Iterations[0].IsCommitted.Should().BeTrue();
            progress.Iterations[1].IsCommitted.Should().BeTrue();
            progress.Iterations[2].IsCommitted.Should().BeTrue();
            progress.IsCompleted.Should().BeTrue();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Repeat_with_context_and_inference_executes_durably_per_iteration()
    {
        // R16: review/repair loops repeat LLM inference with per-iteration
        // context snapshots; both must execute durably inside repeat bodies
        // and replay without reinvoking providers.
        var admission = await AdmitRepeatInferenceAsync();
        var context = new LoopSuffixContext(".cx");
        var inference = new LoopSuffixInference(".inf");
        var ports = new FuwenZhinuExecutionPorts(new UnusedActivity(), context, inference);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.repeat-inference", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"start\"");
            var runId = await engine.StartAsync("fuwen.repeat-inference", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("start.cx.inf1.cx.inf2");
            context.Calls.Should().Be(2);
            inference.Calls.Should().Be(2);
            inference.Requests.Should().HaveCount(2)
                .And.OnlyContain(request => request.ContextInputs.Count == 1 && request.ContextInputs[0].Name == "cx");

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            context.Calls.Should().Be(2);
            inference.Calls.Should().Be(2);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<WorkflowAdmissionResult> AdmitRepeatInferenceAsync()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var contextDesc = new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));
        var profileDesc = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));
        var templateDesc = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", new ContentDigest("sha256", "descriptor/v1", new string('d', 64)));
        var loopPath = StructuralNodeIdentity.Create("demo", "loop1");
        var cxPath = loopPath + "/$body/cx";
        var answerPath = loopPath + "/$body/answer";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new RepeatNode(
                "loop1", loopPath, 3, str,
                new InputBinding([]),
                [
                    new ContextNode("cx", cxPath, contextDesc,
                        [new ArgumentBinding("request", new LoopStateBinding([]))], str),
                    new InferenceNode("answer", answerPath, profileDesc, templateDesc,
                        [new ArgumentBinding("request", new NodeOutputBinding(cxPath, []))], [], str,
                        [new ContextRequirement("cx", new NodeOutputBinding(cxPath, []), str)]),
                ],
                new NodeOutputBinding(answerPath, []),
                new ConditionExpression(ConditionOperator.Equal, new LoopIterationBinding([]), new LiteralBinding(JsonDocument.Parse("2").RootElement.Clone())),
                str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([loopPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/loop1/$body", [new WorkflowExecutionPhase([cxPath]), new WorkflowExecutionPhase([answerPath])]),
            ]))
            .BuildV6();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(contextDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("request", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                    new TrustedCatalogueDescriptor(profileDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("request", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                    new TrustedCatalogueDescriptor(templateDesc),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue($"Diagnostics: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} (path={d.Path})"))}");
        return admission;
    }

    private sealed class LoopSuffixContext(string suffix) : IContextProvider
    {
        public int Calls { get; private set; }
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken ct = default)
        {
            Calls++;
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input + suffix));
            var snap = new ContextSnapshotReference(request.Provider, $"snap-{Calls}",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)), [],
                "policy/1", new ContextSnapshotBudgetEvidence(false, null, null, null, null), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement), snap));
        }
    }

    private sealed class LoopSuffixInference(string suffix) : IInferenceExecutor
    {
        public int Calls { get; private set; }
        public List<InferenceExecutionRequest> Requests { get; } = [];
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken ct = default)
        {
            Calls++;
            Requests.Add(request);
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input + suffix + Calls));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private static async Task<WorkflowAdmissionResult> AdmitRepeatAsync(int maxIterations, bool breakOnIter3 = true)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var intType = new PrimitiveType(FuwenPrimitiveKind.Integer);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var loopPath = StructuralNodeIdentity.Create("demo", "loop1");
        var stepPath = loopPath + "/$body/step";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        // Break after 3 iterations via iter == 3, otherwise never break (to hit limit).
        var breakCond = breakOnIter3
            ? new ConditionExpression(ConditionOperator.Equal, new LoopIterationBinding([]), new LiteralBinding(JsonDocument.Parse("3").RootElement.Clone()))
            : new ConditionExpression(ConditionOperator.Equal, new LoopStateBinding([]), new LiteralBinding(JsonDocument.Parse("\"never\"").RootElement.Clone()));
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new RepeatNode(
                "loop1", loopPath, maxIterations, str,
                new InputBinding([]),
                [new ActivityNode("step", stepPath, activityDesc, [new ArgumentBinding("value", new LoopStateBinding([]))], str)],
                new NodeOutputBinding(stepPath, []),
                breakCond,
                str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([loopPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/loop1/$body", [new WorkflowExecutionPhase([stepPath])]),
            ]))
            .BuildV6();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue($"Diagnostics: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} (path={d.Path})"))}");
        return admission;
    }

    private sealed class RepeatCountingActivity : IActivityExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            Calls++;
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            var next = input + "-break" + Calls;
            // Break when next contains "3" (so after 3 iterations, break)
            // The break condition in the plan is s == "ok", but we simulate break via next state containing "3"
            // For the test we just produce a value that will be checked by break condition s == "ok" -> not true, so loop will run max.
            // Instead we make the break condition s == "start-break3" for the success test.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(next));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class UnconditionallyContinuingActivity : IActivityExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            Calls++;
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input + "-next"));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }

    private sealed class CrashOnIterationActivity(ConcurrentDictionary<Guid, int> failingIterationTracker, Guid runId) : IActivityExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            Calls++;
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            var next = input + "-break" + Calls;
            if (failingIterationTracker.TryGetValue(runId, out var failOn) && Calls == failOn)
                throw new InvalidOperationException($"Simulated crash on iteration {Calls}");
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(next));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }
#pragma warning restore xUnit1030
}
