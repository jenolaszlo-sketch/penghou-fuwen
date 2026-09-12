using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Executes_admitted_v3_activity_with_durable_step_boundaries()
    {
        var admission = await AdmitAsync();
        var activity = new RecordingActivity();
        var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
                BusyTimeout = TimeSpan.FromSeconds(2),
            });
            await using var engine = new WorkflowEngine(
                store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions
                {
                    LeaseDuration = TimeSpan.FromSeconds(2),
                    LeaseRenewalInterval = TimeSpan.FromMilliseconds(250),
                    PollInterval = TimeSpan.FromMilliseconds(5),
                });

            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.echo",
                "1",
                inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(
                runId,
                cancellationToken: TestContext.Current.CancellationToken);

            output.GetString().Should().Be("hello");
            activity.Requests.Should().ContainSingle();
            activity.Requests[0].Invocation.ExecutionFingerprint.Should().Be(admission.Receipt!.ExecutionFingerprint);
            activity.Requests[0].Invocation.StructuralPath.Should().Be("echo/echo");
            (await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken))
                .Should().Contain(items => items.StepKey == "echo/echo" && items.Status == StepStatus.Completed)
                .And.Contain(items => items.StepKey == "echo/return_result" && items.Status == StepStatus.Completed);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Fails_closed_when_a_provider_returns_the_wrong_type()
    {
        var admission = await AdmitAsync();
        var ports = new FuwenZhinuExecutionPorts(new WrongTypeActivity(), new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
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
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync("fuwen.echo", "1", inputDocument.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var failed = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
            failed!.Status.Should().Be(WorkflowStatus.Failed);
            failed.Error.Should().NotBeNull();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Preserves_context_snapshot_evidence_for_inference()
    {
        var fixture = await AdmitContextInferenceAsync();
        var contextProvider = new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor);
        var inference = new RecordingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), contextProvider, inference))
            .CreateAsync("fuwen.context", "1", fixture.Admission, TestContext.Current.CancellationToken);
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
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var result = await engine.RunAsync<JsonElement, JsonElement>(
                "fuwen.context",
                "1",
                inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);

            result.GetString().Should().Be("answer");
            contextProvider.Requests.Should().ContainSingle();
            inference.Requests.Should().ContainSingle();
            inference.Requests[0].ContextInputs.Should().ContainSingle();
            inference.Requests[0].ContextInputs[0].ContextSnapshot.SnapshotId.Should().Be("snapshot-1");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync()
    {
        var plan = CreatePlan();
        var descriptor = plan.CatalogueBindings[0];
        return await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(
                        descriptor,
                        callableContract: new CallableContract(
                            new CallableSignature(
                                [new CallableParameter("value", new PrimitiveType(FuwenPrimitiveKind.String))],
                                new PrimitiveType(FuwenPrimitiveKind.String)),
                            CallableEffect.Read,
                            CallableIdempotency.Idempotent,
                            CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<ContextFixture> AdmitContextInferenceAsync()
    {
        var fixture = CreateContextInferencePlan();
        var catalogue = fixture.Descriptors
            .Select(descriptor => new TrustedCatalogueDescriptor(
                descriptor.Reference,
                callableContract: descriptor.Contract))
            .ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(fixture.Plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));
        return fixture with { Admission = admission };
    }

    private static WorkflowPlan CreatePlan()
    {
        var activity = new DescriptorReference(
            DescriptorKind.Activity,
            "sample.echo",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = StructuralNodeIdentity.Create("echo", "echo");
        var returnPath = StructuralNodeIdentity.Create("echo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            "echo",
            "1",
            new PrimitiveType(FuwenPrimitiveKind.String),
            new PrimitiveType(FuwenPrimitiveKind.String),
            "routing/1",
            [],
            [activity],
            new CapabilityManifest([]),
            [
                new ActivityNode("echo", activityPath, activity, [new ArgumentBinding("value", new InputBinding([]))], new PrimitiveType(FuwenPrimitiveKind.String)),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("echo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private static FuwenZhinuProviderRuntimeIdentity IdentityFor(WorkflowAdmissionResult admission) => new(
        admission.Receipt!.CatalogueSnapshotRevision,
        admission.Receipt.ResolvedDescriptorSetFingerprint);

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private sealed class RecordingActivity : IActivityExecutor
    {
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(request.Arguments.Single().Value));
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity executor should not be called.");
    }

    private sealed class RecordingContextProvider(DescriptorReference provider, DescriptorReference artifactDescriptor) : IContextProvider
    {
        public List<ContextExecutionRequest> Requests { get; } = [];

        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var artifact = new ArtifactReference(
                "fake",
                "artifact-1",
                artifactDescriptor,
                new ContentDigest("sha256", "content/v1", new string('b', 64)),
                5,
                "context.txt");
            var snapshot = new ContextSnapshotReference(
                provider,
                "snapshot-1",
                new ContentDigest("sha256", "request/v1", new string('c', 64)),
                new ContentDigest("sha256", "content/v1", new string('d', 64)),
                [],
                "policy/1",
                new ContextSnapshotBudgetEvidence(false, null, null, null, null),
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(ContextExecutionResult.Succeeded(RuntimeValue.FromArtifact(artifact), snapshot));
        }
    }

    private sealed class RecordingInference : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonDocument.Parse("\"answer\"").RootElement)));
        }
    }

    private sealed record DescriptorFixture(DescriptorReference Reference, CallableContract? Contract);

    private sealed record ContextFixture(
        WorkflowPlan Plan,
        DescriptorReference ContextDescriptor,
        DescriptorReference ArtifactDescriptor,
        IReadOnlyList<DescriptorFixture> Descriptors,
        WorkflowAdmissionResult Admission = null!);

    private static ContextFixture CreateContextInferencePlan()
    {
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        var contextDescriptor = new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('a'));
        var artifactDescriptor = new DescriptorReference(DescriptorKind.Artifact, "sample.artifact", "1", Digest('b'));
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('c'));
        var template = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('d'));
        var contextPath = StructuralNodeIdentity.Create("context", "context");
        var inferencePath = StructuralNodeIdentity.Create("context", "infer");
        var returnPath = StructuralNodeIdentity.Create("context", "return_result");
        var stringType = new PrimitiveType(FuwenPrimitiveKind.String);
        var artifactType = new ArtifactType(artifactDescriptor);
        var descriptors = new[]
        {
            new DescriptorFixture(contextDescriptor, new CallableContract(new CallableSignature([new CallableParameter("request", stringType)], artifactType), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new DescriptorFixture(artifactDescriptor, null),
            new DescriptorFixture(profile, new CallableContract(new CallableSignature([new CallableParameter("request", stringType)], stringType), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new DescriptorFixture(template, null),
        };
        var plan = new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            "context",
            "1",
            stringType,
            stringType,
            "routing/1",
            [],
            descriptors.Select(static descriptor => descriptor.Reference).ToArray(),
            new CapabilityManifest([]),
            [
                new ContextNode("context", contextPath, contextDescriptor, [new ArgumentBinding("request", new InputBinding([]))], artifactType),
                new InferenceNode("infer", inferencePath, profile, template, [new ArgumentBinding("request", new InputBinding([]))], [], stringType,
                    [new ContextRequirement("context", new NodeOutputBinding(contextPath, []), artifactType)]),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferencePath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("context", [
                    new WorkflowExecutionPhase([contextPath]),
                    new WorkflowExecutionPhase([inferencePath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
        return new ContextFixture(plan, contextDescriptor, artifactDescriptor, descriptors);
    }

    private sealed class WrongTypeActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonDocument.Parse("true").RootElement)));
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context provider should not be called.");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Inference executor should not be called.");
    }
}
