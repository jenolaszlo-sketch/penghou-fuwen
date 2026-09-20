using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>R31 coverage for tool allowlists across durable execution regions.</summary>
public sealed class R31ToolSurfaceRecoveryTests
{
    private static ContentDigest Digest(char value) =>
        new("sha256", "descriptor/v1", new string(value, 64));

    [Fact]
    public async Task V8_tool_subset_survives_repeat_replay_and_recovery()
    {
        var fixture = await AdmitRepeatAsync();
        var inference = new CapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    fixture.Admission.Receipt!.CatalogueSnapshotRevision,
                    fixture.Admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("r31.repeat", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("\"start\"");
            var runId = await engine.StartAsync("r31.repeat", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken)).GetString()
                .Should().Be("start-ok-ok");
            inference.Requests.Should().HaveCount(2).And.OnlyContain(request =>
                request.Tools != null && request.Tools.SequenceEqual(new[] { fixture.Search }));

            var progress = await engine.GetLoopProgressAsync(
                runId,
                WorkflowLoopReference.Root("loop"),
                TestContext.Current.CancellationToken);
            progress.Should().NotBeNull();
            var targetStep = progress!.Iterations[1].BodySteps.Should().ContainSingle().Subject.StepKey;
            await engine.RestartStepAsync(runId, targetStep, TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken)).GetString()
                .Should().Be("start-ok-ok");
            inference.Requests.Should().HaveCount(3).And.OnlyContain(request =>
                request.Tools != null && request.Tools.SequenceEqual(new[] { fixture.Search }));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task V8_tool_subset_survives_fan_out_replay_and_item_recovery()
    {
        var fixture = await AdmitFanOutAsync();
        var inference = new CapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    fixture.Admission.Receipt!.CatalogueSnapshotRevision,
                    fixture.Admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("r31.fanout", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var input = JsonDocument.Parse("[\"b\",\"a\"]");
            var runId = await engine.StartAsync("r31.fanout", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken))
                .EnumerateArray().Select(item => item.GetString()).Should().Equal("b-ok", "a-ok");
            inference.Requests.Should().HaveCount(2).And.OnlyContain(request =>
                request.Tools != null && request.Tools.SequenceEqual(new[] { fixture.Search }));

            var itemPath = RuntimeNodeIdentity.CreateFanOutItem(fixture.FanOutPath, new StringRuntimeKey("a"));
            await engine.RestartStepAsync(runId, itemPath, TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken))
                .EnumerateArray().Select(item => item.GetString()).Should().Equal("b-ok", "a-ok");
            inference.Requests.Should().HaveCount(3).And.OnlyContain(request =>
                request.Tools != null && request.Tools.SequenceEqual(new[] { fixture.Search }));
        }
        finally { DeleteDirectory(root); }
    }

    private static WorkflowEngine CreateEngine(string root, FuwenZhinuWorkflowRegistration registration) =>
        new(
            new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false }),
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

    private static async Task<RepeatFixture> AdmitRepeatAsync()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a'));
        var template = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('b'));
        var search = new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('c'));
        var loopPath = StructuralNodeIdentity.Create("demo", "loop");
        var answerPath = $"{loopPath}/$body/answer";
        var returnPath = StructuralNodeIdentity.Create("demo", "return");
        var plan = new WorkflowPlanBuilder("demo", "1", text, text, "routing/1")
            .AddNode(new RepeatNode("loop", loopPath, 2, text, new InputBinding([]),
                [new InferenceNode("answer", answerPath, profile, template, [new ArgumentBinding("request", new LoopStateBinding([]))], [], text, [], Tools: [search])],
                new NodeOutputBinding(answerPath, []),
                new ConditionExpression(ConditionOperator.Equal, new LoopIterationBinding([]), new LiteralBinding(JsonDocument.Parse("2").RootElement.Clone())), text))
            .AddNode(new ReturnNode("return", returnPath, new NodeOutputBinding(loopPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([loopPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion($"{loopPath}/$body", [new WorkflowExecutionPhase([answerPath])]),
            ]))
            .BuildV8();
        return new RepeatFixture(await AdmitAsync(plan, [profile, template, search], text), search, answerPath);
    }

    private static async Task<FanOutFixture> AdmitFanOutAsync()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var list = new ListType(text, 2);
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a'));
        var template = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('b'));
        var search = new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('c'));
        var fanOutPath = StructuralNodeIdentity.Create("batch", "process");
        var answerPath = $"{fanOutPath}/$body/answer";
        var returnPath = StructuralNodeIdentity.Create("batch", "return");
        var plan = new WorkflowPlanBuilder("batch", "1", list, list, "routing/1")
            .AddFanOut(new FanOutNode("process", fanOutPath, new InputBinding([]), new FanOutItemBinding("item", text), new FanOutItemValueBinding([]),
                [new InferenceNode("answer", answerPath, profile, template, [new ArgumentBinding("request", new FanOutItemValueBinding([]))], [], text, [], Tools: [search])],
                new NodeOutputBinding(answerPath, []), list, 2, 2))
            .AddNode(new ReturnNode("return", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("batch", [new WorkflowExecutionPhase([fanOutPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [new WorkflowExecutionPhase([answerPath])]),
            ]))
            .BuildV8();
        return new FanOutFixture(await AdmitAsync(plan, [profile, template, search], text), search, fanOutPath);
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan, IReadOnlyList<DescriptorReference> descriptors, FuwenType text)
    {
        var catalogue = descriptors.Select(descriptor => new TrustedCatalogueDescriptor(descriptor,
            callableContract: descriptor.Kind is DescriptorKind.InferenceProfile or DescriptorKind.Tool
                ? new CallableContract(new CallableSignature([new CallableParameter("request", text)], text), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)
                : null)).ToArray();
        var result = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private sealed class CapturingInference : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var value = request.Arguments.Single().Value is JsonRuntimeValue json ? json.Value.GetString()! : "";
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value + "-ok"));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement)));
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity executor should not be called.");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context provider should not be called.");
    }

    private sealed record RepeatFixture(WorkflowAdmissionResult Admission, DescriptorReference Search, string AnswerPath);
    private sealed record FanOutFixture(WorkflowAdmissionResult Admission, DescriptorReference Search, string FanOutPath);
}
