using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task One_call_inference_fallback_keeps_failed_provider_step_and_records_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = CreateContextInferencePlan();
        using var literal = JsonDocument.Parse("\"manual review\"");
        var plan = fixture.Plan with
        {
            IrVersion = FuwenContracts.InferenceFallbackIrVersion,
            Nodes = fixture.Plan.Nodes.Select(node => node is InferenceNode inference
                ? inference with
                {
                    FailureFallback = new InferenceFailureFallback(
                        [ExecutionFailureCode.SchemaMismatch],
                        new LiteralBinding(literal.RootElement.Clone())),
                }
                : node).ToArray(),
        };
        var catalogue = fixture.Descriptors.Select(descriptor =>
            new TrustedCatalogueDescriptor(descriptor.Reference, callableContract: descriptor.Contract)).ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
            new InMemoryTrustedCatalogue(catalogue),
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));
        var inference = new FailingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    CurrentInferenceFixture.WithPreflight(inference)))
            .CreateAsync("fuwen.context", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var engine = new WorkflowEngine(
                new SqliteWorkflowStore(new ZhinuSqliteOptions
                {
                    DatabasePath = Path.Combine(root, "workflow.db"),
                    Pooling = false,
                }),
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("fuwen.context", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("manual review");
            inference.Calls.Should().Be(1);
            var steps = await engine.GetStepsAsync(run, ct);
            steps.Should().Contain(step => step.StepKey == "context/infer" && step.Status == StepStatus.Completed);
            steps.Should().Contain(step => step.StepKey == "context/infer/$fallback" && step.Status == StepStatus.Completed);
            using var failedEnvelope = JsonDocument.Parse(
                steps.Single(step => step.StepKey == "context/infer").OutputJson!);
            failedEnvelope.RootElement.GetProperty("failure").GetProperty("code")
                .GetString().Should().Be("schemaMismatch");
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class FailingInference : IInferenceExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.SchemaMismatch,
                "Structured output failed validation.")));
        }
    }

    [Fact]
    public async Task Accepted_evaluation_completes_without_entering_the_human_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await RegisterFallbackHumanGateAsync("accepted", ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(
                store,
                gate.Registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("fuwen.fallback-gate", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("accepted");

            gate.Inference.Calls.Should().Be(1);
            gate.Activities.Requests.Select(request => $"{request.Activity.Name}@{request.Invocation.StructuralPath}")
                .Should().Equal("sample.evaluate@context/evaluate", "sample.record@context/verdict/$then/record");
            var steps = await engine.GetStepsAsync(run, ct);
            steps.Should().Contain(step => step.StepKey == "context/propose/$fallback" && step.Status == StepStatus.Completed);
            steps.Should().Contain(step => step.StepKey == "context/recommend" && step.Status == StepStatus.Completed);
            steps.Should().NotContain(step => step.StepKey == "context/verdict/$else/decision");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Pending_evaluation_recovers_through_the_human_gate_after_interruption()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await RegisterFallbackHumanGateAsync("pending", ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            var firstEngine = new WorkflowEngine(
                store,
                gate.Registration.Register(new WorkflowRegistry()),
                new ZhinuOptions
                {
                    LeaseDuration = TimeSpan.FromMilliseconds(150),
                    LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                    PollInterval = TimeSpan.FromMilliseconds(5),
                });
            using var input = JsonDocument.Parse("\"question\"");
            var run = await firstEngine.StartAsync("fuwen.fallback-gate", "1", input.RootElement.Clone(), cancellationToken: ct);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var execution = firstEngine.ExecuteAsync(run, interruption.Token);
            var parkingDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            IReadOnlyList<WorkflowStepRun>? parkingSteps = null;
            while (DateTimeOffset.UtcNow < parkingDeadline)
            {
                parkingSteps = await firstEngine.GetStepsAsync(run, ct);
                if (parkingSteps.Any(step => step.StepKey == "context/verdict/$else/decision" && step.Status == StepStatus.Waiting))
                    break;
                await Task.Delay(25, ct);
            }
            parkingSteps.Should().Contain(
                step => step.StepKey == "context/verdict/$else/decision" && step.Status == StepStatus.Waiting);
            using (var recommendation = JsonDocument.Parse(
                parkingSteps.Single(step => step.StepKey == "context/recommend").OutputJson!))
            {
                recommendation.RootElement.GetString().Should().Be("pending");
            }
            await interruption.CancelAsync();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
            }
            (await firstEngine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Running);
            await firstEngine.DisposeAsync();

            await Task.Delay(350, ct);
            await using var recoveryEngine = new WorkflowEngine(
                store,
                gate.Registration.Register(new WorkflowRegistry()),
                new ZhinuOptions
                {
                    LeaseDuration = TimeSpan.FromMilliseconds(150),
                    LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                    PollInterval = TimeSpan.FromMilliseconds(5),
                });
            gate.Inference.Calls.Should().Be(1);
            gate.Activities.Requests.Count(request => request.Activity.Name == "sample.evaluate").Should().Be(1);

            await recoveryEngine.SendSignalAsync(run, "human_decision", "accepted", ct);
            await recoveryEngine.ExecuteAsync(run, ct);
            (await recoveryEngine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("accepted");

            gate.Inference.Calls.Should().Be(1);
            var steps = await recoveryEngine.GetStepsAsync(run, ct);
            steps.Single(step => step.StepKey == "context/verdict").OutputJson.Should().Be("false");
            steps.Should().Contain(step => step.StepKey == "context/propose" && step.Status == StepStatus.Completed);
            steps.Should().Contain(step => step.StepKey == "context/propose/$fallback" && step.Status == StepStatus.Completed);
            steps.Should().Contain(step => step.StepKey == "context/verdict/$else/decision" && step.Status == StepStatus.Completed);
            using var failedEnvelope = JsonDocument.Parse(
                steps.Single(step => step.StepKey == "context/propose").OutputJson!);
            failedEnvelope.RootElement.GetProperty("failure").GetProperty("code")
                .GetString().Should().Be("schemaMismatch");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<FallbackHumanGate> RegisterFallbackHumanGateAsync(string recommendation, CancellationToken ct)
    {
        var fixture = CreateContextInferencePlan();
        var sourceInference = fixture.Plan.Nodes.OfType<InferenceNode>().Single();
        var sourceContext = fixture.Plan.Nodes.OfType<ContextNode>().Single();
        var stringType = (PrimitiveType)sourceInference.OutputType;
        var evaluator = ActivityDescriptor("sample.evaluate", 'e');
        var recorder = ActivityDescriptor("sample.record", 'f');
        var contextPath = StructuralNodeIdentity.Create("context", "facts");
        var inferencePath = StructuralNodeIdentity.Create("context", "propose");
        var evaluatePath = StructuralNodeIdentity.Create("context", "evaluate");
        var recommendPath = StructuralNodeIdentity.Create("context", "recommend");
        var verdictPath = StructuralNodeIdentity.Create("context", "verdict");
        var directPath = $"{verdictPath}/$then/record";
        var waitPath = $"{verdictPath}/$else/decision";
        var acceptPath = $"{verdictPath}/$else/accept";
        var returnPath = StructuralNodeIdentity.Create("context", "return_result");
        using var fallbackDocument = JsonDocument.Parse("\"pending-review\"");
        using var acceptedDocument = JsonDocument.Parse("\"accepted\"");
        var plan = fixture.Plan with
        {
            IrVersion = FuwenContracts.InferenceFallbackIrVersion,
            CatalogueBindings = [.. fixture.Plan.CatalogueBindings, evaluator, recorder],
            Nodes =
            [
                sourceContext with { Name = "facts", StructuralPath = contextPath },
                sourceInference with
                {
                    Name = "propose",
                    StructuralPath = inferencePath,
                    ContextRequirements = [sourceInference.ContextRequirements!.Single() with { Source = new NodeOutputBinding(contextPath, []) }],
                    FailureFallback = new InferenceFailureFallback(
                        [ExecutionFailureCode.SchemaMismatch],
                        new LiteralBinding(fallbackDocument.RootElement.Clone())),
                },
                new ActivityNode(
                    "evaluate",
                    evaluatePath,
                    evaluator,
                    [new ArgumentBinding("proposal", new NodeOutputBinding(inferencePath, []))],
                    stringType),
                new CheckpointNode(
                    "recommend",
                    recommendPath,
                    new NodeOutputBinding(evaluatePath, []),
                    stringType),
                new ConditionalNode(
                    "verdict",
                    verdictPath,
                    new ConditionExpression(
                        ConditionOperator.Equal,
                        new NodeOutputBinding(evaluatePath, []),
                        new LiteralBinding(acceptedDocument.RootElement.Clone())),
                    [
                        new ActivityNode(
                            "record",
                            directPath,
                            recorder,
                            [new ArgumentBinding("value", new LiteralBinding(acceptedDocument.RootElement.Clone()))],
                            stringType),
                    ],
                    [
                        new WaitNode("decision", waitPath, "human_decision", stringType),
                        new ActivityNode(
                            "accept",
                            acceptPath,
                            recorder,
                            [new ArgumentBinding("value", new NodeOutputBinding(waitPath, []))],
                            stringType),
                    ],
                    new ConditionalMerge(
                        new NodeOutputBinding(directPath, []),
                        new NodeOutputBinding(acceptPath, []),
                        stringType)),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(verdictPath, [])),
            ],
            ExecutionOrder = new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("context", [
                    new WorkflowExecutionPhase([contextPath]),
                    new WorkflowExecutionPhase([inferencePath]),
                    new WorkflowExecutionPhase([evaluatePath]),
                    new WorkflowExecutionPhase([recommendPath]),
                    new WorkflowExecutionPhase([verdictPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{verdictPath}/$then", [new WorkflowExecutionPhase([directPath])]),
                new WorkflowExecutionRegion($"{verdictPath}/$else", [new WorkflowExecutionPhase([waitPath]), new WorkflowExecutionPhase([acceptPath])]),
            ]),
        };
        var catalogue = plan.CatalogueBindings
            .Select(reference =>
            {
                CallableContract? contract = reference.Equals(evaluator)
                    ? new CallableContract(
                        new CallableSignature([new CallableParameter("proposal", stringType)], stringType),
                        CallableEffect.Read,
                        CallableIdempotency.Idempotent,
                        CallableRetrySafety.Safe)
                    : reference.Equals(recorder)
                        ? new CallableContract(
                            new CallableSignature([new CallableParameter("value", stringType)], stringType),
                            CallableEffect.Read,
                            CallableIdempotency.Idempotent,
                            CallableRetrySafety.Safe)
                        : fixture.Descriptors.Single(descriptor => descriptor.Reference.Equals(reference)).Contract;
                return new TrustedCatalogueDescriptor(reference, callableContract: contract);
            })
            .ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));
        var activities = new FallbackHumanGateActivity(recommendation);
        var inference = new FailingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    activities,
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    CurrentInferenceFixture.WithPreflight(inference)))
            .CreateAsync("fuwen.fallback-gate", "1", admission, ct);
        return new FallbackHumanGate(registration, activities, inference);
    }

    private static DescriptorReference ActivityDescriptor(string name, char digest) =>
        new(DescriptorKind.Activity, name, "1", new ContentDigest("sha256", "descriptor/v1", new string(digest, 64)));

    private sealed record FallbackHumanGate(
        FuwenZhinuWorkflowRegistration Registration,
        FallbackHumanGateActivity Activities,
        FailingInference Inference);

    private sealed class FallbackHumanGateActivity(string recommendation) : IActivityExecutor
    {
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            var output = (request.Activity.Name, request.Invocation.StructuralPath) switch
            {
                ("sample.evaluate", _) when value == "pending-review" => recommendation,
                ("sample.record", var path) when path.EndsWith("/$then/record", StringComparison.Ordinal) => value,
                ("sample.record", var path) when path.EndsWith("/$else/accept", StringComparison.Ordinal)
                    && value is "accepted" or "rejected" => value,
                _ => throw new WorkflowStateException($"Unsupported human-gate activity input '{request.Activity.Name}:{value}'."),
            };
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement(output))));
        }
    }
}
