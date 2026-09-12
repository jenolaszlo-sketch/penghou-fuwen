using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Sqlite_v3_vertical_slice_publishes_selected_activity_artifact_with_fuwen_identity()
    {
        var fixture = await AdmitVerticalAsync();
        var activity = new VerticalActivity(fixture.SelectedActivity, fixture.UnselectedActivity, fixture.ArtifactDescriptor);
        var context = new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor);
        var inference = new VerticalInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(new CompositeActivity(activity), context, inference))
            .CreateAsync("fuwen.vertical", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync(
                "fuwen.vertical", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            output.GetString().Should().Be("selected");
            context.Requests.Should().ContainSingle();
            inference.Requests.Should().ContainSingle();
            activity.SelectedCalls.Should().Be(1);
            activity.UnselectedCalls.Should().Be(0);

            var artifacts = await engine.QueryArtifactsAsync(
                runId,
                new ArtifactQuery { Name = "answer.json" },
                TestContext.Current.CancellationToken);
            artifacts.Should().ContainSingle();
            var artifact = artifacts[0];
            artifact.ProducerStepKey.Should().Be("vertical/check/$then/publish");
            artifact.ProducerStepRevision.Should().Be(1);
            artifact.ArtifactType.Should().Be(fixture.ArtifactDescriptor.Name);
            artifact.ArtifactVersion.Should().Be(fixture.ArtifactDescriptor.Version);
            artifact.Metadata.Should().ContainKey("fuwen.executionFingerprint")
                .WhoseValue.Should().Be(fixture.Admission.Receipt!.ExecutionFingerprint);
            artifact.Metadata.Should().ContainKey("fuwen.structuralPath")
                .WhoseValue.Should().Be("vertical/check/$then/publish");
            artifact.Metadata.Should().ContainKey("fuwen.operationKey")
                .WhoseValue.Should().StartWith("sha256:fuwen-operation/v1:");

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.GetArtifactsAsync(runId, TestContext.Current.CancellationToken))
                .Should().ContainSingle(item => item.Revision == 1);
            activity.SelectedCalls.Should().Be(1);
            activity.UnselectedCalls.Should().Be(0);
            context.Requests.Should().ContainSingle();
            inference.Requests.Should().ContainSingle();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_artifact_publication_is_idempotent_across_post_publication_recovery()
    {
        var fixture = await AdmitVerticalAsync();
        var activity = new VerticalActivity(fixture.SelectedActivity, fixture.UnselectedActivity, fixture.ArtifactDescriptor);
        var observer = new InterruptAfterPublicationObserver("vertical/check/$then/publish");
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    new CompositeActivity(activity),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    new VerticalInference(),
                    observer))
            .CreateAsync("fuwen.vertical", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            var store = CreateStore(databasePath);
            var firstEngine = CreateEngine(store, registration, TimeSpan.FromMilliseconds(150));
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await firstEngine.StartAsync(
                "fuwen.vertical", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var interrupted = firstEngine.ExecuteAsync(runId, interruption.Token);
            await observer.PublicationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            await interruption.CancelAsync();
            await interrupted;
            (await firstEngine.GetArtifactsAsync(runId, TestContext.Current.CancellationToken))
                .Should().ContainSingle(item => item.Revision == 1);
            await firstEngine.DisposeAsync();

            await Task.Delay(350, TestContext.Current.CancellationToken);
            await using var recoveryEngine = CreateEngine(store, registration, TimeSpan.FromMilliseconds(150));
            await recoveryEngine.RunAvailableAsync(TestContext.Current.CancellationToken);
            await recoveryEngine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            var artifacts = await recoveryEngine.GetArtifactsAsync(runId, TestContext.Current.CancellationToken);
            artifacts.Should().ContainSingle(item => item.Revision == 1);
            activity.Requests.Should().HaveCount(2);
            activity.Requests[1].Invocation.OperationKey
                .Should().Be(activity.Requests[0].Invocation.OperationKey);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_rejects_publication_receipt_not_bound_to_current_operation()
    {
        var fixture = await AdmitVerticalAsync();
        var activity = new VerticalActivity(
            fixture.SelectedActivity,
            fixture.UnselectedActivity,
            fixture.ArtifactDescriptor,
            invalidPublicationKey: true);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    new CompositeActivity(activity),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    new VerticalInference()))
            .CreateAsync("fuwen.vertical", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync(
                "fuwen.vertical", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Failed);
            (await engine.GetArtifactsAsync(runId, TestContext.Current.CancellationToken))
                .Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_rejects_a_semantically_altered_persisted_publication_receipt_on_replay()
    {
        var fixture = await AdmitVerticalAsync();
        var activity = new VerticalActivity(fixture.SelectedActivity, fixture.UnselectedActivity, fixture.ArtifactDescriptor);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    new CompositeActivity(activity),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    new VerticalInference()))
            .CreateAsync("fuwen.vertical", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            var store = CreateStore(databasePath);
            await using var engine = CreateEngine(store, registration);
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync(
                "fuwen.vertical", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT output_json FROM workflow_steps WHERE workflow_run_id = $run AND step_key = $step";
                read.Parameters.AddWithValue("$run", runId.ToString("D"));
                read.Parameters.AddWithValue("$step", "vertical/check/$then/publish");
                var outputJson = (string?)await read.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                var envelope = JsonNode.Parse(outputJson!)!.AsObject();
                envelope["publications"]!.AsArray()[0]!["idempotencyKey"] = "forged-operation";

                await using var write = connection.CreateCommand();
                write.CommandText = "UPDATE workflow_steps SET output_json = $output WHERE workflow_run_id = $run AND step_key = $step";
                write.Parameters.AddWithValue("$output", envelope.ToJsonString());
                write.Parameters.AddWithValue("$run", runId.ToString("D"));
                write.Parameters.AddWithValue("$step", "vertical/check/$then/publish");
                await write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await engine.RestartStepAsync(runId, "vertical/return_result", TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Failed);
            activity.SelectedCalls.Should().Be(1);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<VerticalFixture> AdmitVerticalAsync()
    {
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        var stringType = new PrimitiveType(FuwenPrimitiveKind.String);
        var boolType = new PrimitiveType(FuwenPrimitiveKind.Boolean);
        var contextDescriptor = new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('a'));
        var artifactDescriptor = new DescriptorReference(DescriptorKind.Artifact, "sample.answer", "1", Digest('b'));
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('c'));
        var prompt = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('d'));
        var selectedActivity = new DescriptorReference(DescriptorKind.Activity, "sample.selected", "1", Digest('e'));
        var unselectedActivity = new DescriptorReference(DescriptorKind.Activity, "sample.unselected", "1", Digest('f'));
        var contextPath = StructuralNodeIdentity.Create("vertical", "context");
        var inferencePath = StructuralNodeIdentity.Create("vertical", "infer");
        var checkPath = StructuralNodeIdentity.Create("vertical", "check");
        var selectedPath = $"{checkPath}/$then/publish";
        var unselectedPath = $"{checkPath}/$else/unselected";
        var returnPath = StructuralNodeIdentity.Create("vertical", "return_result");
        var contextType = new ArtifactType(artifactDescriptor);
        var context = new ContextNode(
            "context", contextPath, contextDescriptor,
            [new ArgumentBinding("request", new InputBinding([]))], contextType);
        var inference = new InferenceNode(
            "infer", inferencePath, profile, prompt,
            [new ArgumentBinding("request", new InputBinding([]))], [], boolType,
            [new ContextRequirement("context", new NodeOutputBinding(contextPath, []), contextType)]);
        var conditional = new ConditionalNode(
            "check", checkPath,
            new ConditionExpression(
                ConditionOperator.Equal,
                new NodeOutputBinding(inferencePath, []),
                new LiteralBinding(JsonSerializer.SerializeToElement(true))),
            [new ActivityNode(
                "publish", selectedPath, selectedActivity,
                [new ArgumentBinding("value", new InputBinding([]))], stringType)],
            [new ActivityNode(
                "unselected", unselectedPath, unselectedActivity,
                [new ArgumentBinding("value", new InputBinding([]))], stringType)]);
        var plan = new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            "vertical", "1", stringType, stringType, "routing/1", [],
            [contextDescriptor, artifactDescriptor, profile, prompt, selectedActivity, unselectedActivity],
            new CapabilityManifest([]),
            [context, inference, conditional, new ReturnNode(
                "return_result", returnPath, new LiteralBinding(JsonSerializer.SerializeToElement("selected")))],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("vertical", [
                    new WorkflowExecutionPhase([contextPath]),
                    new WorkflowExecutionPhase([inferencePath]),
                    new WorkflowExecutionPhase([checkPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{checkPath}/$then", [new WorkflowExecutionPhase([selectedPath])]),
                new WorkflowExecutionRegion($"{checkPath}/$else", [new WorkflowExecutionPhase([unselectedPath])]),
            ]));

        var catalogue = new List<TrustedCatalogueDescriptor>
        {
            Callable(contextDescriptor, "request", contextType),
            new TrustedCatalogueDescriptor(artifactDescriptor),
            Callable(profile, "request", boolType),
            new TrustedCatalogueDescriptor(prompt),
            Callable(selectedActivity, "value", stringType),
            Callable(unselectedActivity, "value", stringType),
        };
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}: {item.Message} path={item.Path} expected={item.Expected} actual={item.Actual}")));
        return new VerticalFixture(
            admission, contextDescriptor, artifactDescriptor, selectedActivity, unselectedActivity);

        static TrustedCatalogueDescriptor Callable(
            DescriptorReference descriptor,
            string parameterName,
            FuwenType outputType) =>
            new(
                descriptor,
                callableContract: new CallableContract(
                    new CallableSignature(
                        [new CallableParameter(parameterName, new PrimitiveType(FuwenPrimitiveKind.String))],
                        outputType),
                    CallableEffect.Read,
                    CallableIdempotency.Idempotent,
                    CallableRetrySafety.Safe));
    }

    private sealed record VerticalFixture(
        WorkflowAdmissionResult Admission,
        DescriptorReference ContextDescriptor,
        DescriptorReference ArtifactDescriptor,
        DescriptorReference SelectedActivity,
        DescriptorReference UnselectedActivity);

    private sealed class CompositeActivity(
        VerticalActivity activity) : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            activity.ExecuteAsync(request, cancellationToken);
    }

    private sealed class VerticalActivity(
        DescriptorReference selected,
        DescriptorReference unselected,
        DescriptorReference artifactDescriptor,
        bool invalidPublicationKey = false)
    {
        public int SelectedCalls { get; private set; }
        public int UnselectedCalls { get; private set; }
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Activity == unselected)
            {
                UnselectedCalls++;
                throw new InvalidOperationException("The unselected activity must not execute.");
            }

            request.Activity.Should().Be(selected);
            SelectedCalls++;
            var artifact = new ArtifactReference(
                "answer-store",
                "answer-1",
                artifactDescriptor,
                new ContentDigest("sha256", "content/v1", new string('1', 64)),
                7,
                "answer.json");
            var receipt = new ArtifactPublicationReceipt(
                invalidPublicationKey ? "unbound-operation" : request.Invocation.OperationKey,
                artifact,
                "provider-receipt-1",
                PublicationDisposition.Created);
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement("selected")),
                [receipt]));
        }
    }

    private sealed class InterruptAfterPublicationObserver(string structuralPath) : IExecutionObserver
    {
        private int interruptions;
        public TaskCompletionSource PublicationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ObserveAsync(
            ExecutionObservation observation,
            CancellationToken cancellationToken = default)
        {
            if (observation.Kind != ExecutionObservationKind.Succeeded ||
                !string.Equals(observation.Invocation.StructuralPath, structuralPath, StringComparison.Ordinal) ||
                Interlocked.Increment(ref interruptions) != 1)
                return;

            PublicationObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class VerticalInference : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement(true))));
        }
    }
}
