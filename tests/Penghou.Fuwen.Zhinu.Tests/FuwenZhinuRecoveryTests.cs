using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Sqlite_retries_only_safe_infrastructure_failure_with_the_same_operation_key()
    {
        var admission = await AdmitAsync();
        var activity = new SafeInfrastructureRetryActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    activity,
                    new UnusedContext(),
                    new UnusedInference(),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(2)))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            await using var engine = CreateEngine(CreateStore(Path.Combine(root, "workflow.db")), registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken)).GetString().Should().Be("hello");

            activity.Requests.Should().HaveCount(2);
            activity.Requests[1].Invocation.OperationKey.Should().Be(activity.Requests[0].Invocation.OperationKey);
            activity.Requests[1].Invocation.StepRevision.Should().Be(activity.Requests[0].Invocation.StepRevision);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_persists_final_typed_provider_failure_without_retrying()
    {
        var admission = await AdmitAsync();
        var activity = new FinalFailureActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    activity,
                    new UnusedContext(),
                    new UnusedInference(),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(8)))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            await using var engine = CreateEngine(CreateStore(databasePath), registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Failed);
            activity.Requests.Should().ContainSingle();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT output_json FROM workflow_steps WHERE workflow_run_id = $run AND step_key = $step";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            command.Parameters.AddWithValue("$step", "echo/echo");
            var persisted = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            using var envelope = JsonDocument.Parse(persisted!);
            envelope.RootElement.GetProperty("failure").GetProperty("code").GetString()
                .Should().Be("providerError");
            envelope.RootElement.GetProperty("output").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_recovery_replays_a_claimed_node_without_duplicate_provider_effect()
    {
        var admission = await AdmitAsync();
        var activity = new RecoveryActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            var store = CreateStore(databasePath);
            var engine1 = CreateEngine(store, registration, leaseDuration: TimeSpan.FromMilliseconds(150));
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine1.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var interrupted = engine1.ExecuteAsync(runId, interruption.Token);
            await activity.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await interruption.CancelAsync();
            try
            {
                await interrupted;
            }
            catch (OperationCanceledException)
            {
                // Host interruption leaves the claimed step for lease recovery.
            }
            await engine1.DisposeAsync();

            await Task.Delay(350, TestContext.Current.CancellationToken);
            var engine2 = CreateEngine(store, registration, leaseDuration: TimeSpan.FromMilliseconds(150));
            await engine2.RunAvailableAsync(TestContext.Current.CancellationToken);
            var output = await engine2.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            output.GetString().Should().Be("hello");
            activity.Requests.Should().HaveCount(2);
            activity.CommittedEffects.Should().Be(1);
            activity.Requests[1].Invocation.OperationKey
                .Should().Be(activity.Requests[0].Invocation.OperationKey);
            activity.Requests[1].Invocation.StepRevision
                .Should().Be(activity.Requests[0].Invocation.StepRevision);
            activity.Requests[0].Invocation.RuntimePath
                .Should().Be($"{runId:D}/echo/echo");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Execution_observer_receives_provider_lifecycle_without_observing_replay()
    {
        var admission = await AdmitAsync();
        var activity = new RecordingActivity();
        var observer = new RecordingObserver();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(), observer))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            observer.Observations.Select(observation => observation.Kind)
                .Should().Equal(ExecutionObservationKind.Requested, ExecutionObservationKind.Succeeded);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            observer.Observations.Should().HaveCount(2);

            observer.ThrowOnObserve = true;
            await engine.RestartStepAsync(
                runId, "echo/echo", TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Completed);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_restart_invalidates_only_the_target_and_transitive_dependents()
    {
        var admission = await AdmitBranchingAsync();
        var activity = new BranchingActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.branch", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.branch", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            var before = activity.Requests.ToDictionary(
                request => request.Activity.Name,
                request => request.Invocation,
                StringComparer.Ordinal);
            var plan = await engine.PlanRestartAsync(
                runId, "branch/a", cancellationToken: TestContext.Current.CancellationToken);
            plan.StepsToInvalidate.Select(step => step.StepKey)
                .Should().BeEquivalentTo(["branch/a", "branch/b", "branch/return_result"]);

            var restartOptions = new RestartStepOptions { OperationId = Guid.NewGuid() };
            var applied = await engine.RestartStepWithReceiptAsync(
                runId, "branch/a", restartOptions, TestContext.Current.CancellationToken);
            applied.WasApplied.Should().BeTrue();
            var replayed = await engine.RestartStepWithReceiptAsync(
                runId, "branch/a", restartOptions, TestContext.Current.CancellationToken);
            replayed.WasApplied.Should().BeFalse();
            replayed.Plan.Should().BeEquivalentTo(applied.Plan);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            activity.Requests.Select(request => request.Activity.Name)
                .Should().BeEquivalentTo(["sample.a", "sample.b", "sample.c", "sample.a", "sample.b"]);
            var after = activity.Requests.Skip(3).ToArray();
            after[0].Invocation.OperationKey.Should().NotBe(before["sample.a"].OperationKey);
            after[1].Invocation.OperationKey.Should().NotBe(before["sample.b"].OperationKey);
            activity.Requests.Count(request => request.Activity.Name == "sample.c").Should().Be(1);

            var steps = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Single(step => step.StepKey == "branch/a").Revision.Should().Be(2);
            steps.Single(step => step.StepKey == "branch/b").Revision.Should().Be(2);
            steps.Single(step => step.StepKey == "branch/c").Revision.Should().Be(1);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_same_input_runs_have_distinct_operation_keys_and_restart_changes_revision_key()
    {
        var admission = await AdmitAsync();
        var activity = new RecordingActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var firstRun = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(firstRun, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                firstRun, cancellationToken: TestContext.Current.CancellationToken);
            var secondRun = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(secondRun, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                secondRun, cancellationToken: TestContext.Current.CancellationToken);

            activity.Requests.Should().HaveCount(2);
            activity.Requests[0].Invocation.OperationKey
                .Should().NotBe(activity.Requests[1].Invocation.OperationKey);
            activity.Requests[0].Invocation.RuntimePath
                .Should().NotBe(activity.Requests[1].Invocation.RuntimePath);

            var firstRunSteps = await engine.GetStepsAsync(firstRun, TestContext.Current.CancellationToken);
            firstRunSteps.Should().ContainSingle(step => step.StepKey == "echo/echo");
            await engine.RestartStepAsync(
                firstRun, "echo/echo", cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(firstRun, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                firstRun, cancellationToken: TestContext.Current.CancellationToken);

            activity.Requests.Should().HaveCount(3);
            activity.Requests[2].Invocation.OperationKey
                .Should().NotBe(activity.Requests[0].Invocation.OperationKey);
            activity.Requests[2].Invocation.StepRevision.Should().Be("2");
            (await engine.GetRunAsync(secondRun, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Completed);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_rejects_a_changed_definition_for_an_existing_run_id()
    {
        var firstAdmission = await AdmitAsync();
        var changedAdmission = await AdmitChangedAsync();
        changedAdmission.Receipt!.ExecutionFingerprint
            .Should().NotBe(firstAdmission.Receipt!.ExecutionFingerprint);
        var firstRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(firstAdmission),
                new FuwenZhinuExecutionPorts(new RecordingActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", firstAdmission, TestContext.Current.CancellationToken);
        var changedRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(changedAdmission),
                new FuwenZhinuExecutionPorts(new RecordingActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", changedAdmission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            await using (var firstEngine = CreateEngine(store, firstRegistration))
            {
                var runId = await firstEngine.StartAsync(
                    "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                    cancellationToken: TestContext.Current.CancellationToken);
                await firstEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
                await firstEngine.WaitForCompletionAsync<JsonElement>(
                    runId, cancellationToken: TestContext.Current.CancellationToken);

                await using var changedEngine = CreateEngine(store, changedRegistration);
                await changedEngine.RestartStepAsync(
                    runId, "echo/echo", TestContext.Current.CancellationToken);
                await changedEngine.ExecuteAsync(
                    runId,
                    TestContext.Current.CancellationToken);
                var failed = await changedEngine.GetRunAsync(
                    runId, TestContext.Current.CancellationToken);
                failed!.Status.Should().Be(WorkflowStatus.Failed);
                failed.Error!.Message.Should().Contain("fingerprint does not match");
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_cancellation_marks_the_run_cancelled_and_stops_the_provider()
    {
        var admission = await AdmitAsync();
        var activity = new CancellationActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            var store = CreateStore(Path.Combine(root, "workflow.db"));
            await using var engine = CreateEngine(store, registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.echo", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            var execution = engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await activity.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await engine.CancelAsync(runId, TestContext.Current.CancellationToken);
            await execution;

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Cancelled);
            activity.CancellationObserved.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sqlite_malformed_persisted_context_evidence_fails_closed_before_inference()
    {
        var fixture = await AdmitContextInferenceAsync();
        var contextProvider = new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor);
        var inference = new RecordingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), contextProvider, inference))
            .CreateAsync("fuwen.context", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            var store = CreateStore(databasePath);
            await using var engine = CreateEngine(store, registration);
            using var inputDocument = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync(
                "fuwen.context", "1", inputDocument.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(
                runId, cancellationToken: TestContext.Current.CancellationToken);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_steps SET output_json = $output WHERE workflow_run_id = $run AND step_key = $step";
                command.Parameters.AddWithValue("$output", "{\"output\":");
                command.Parameters.AddWithValue("$run", runId.ToString("D"));
                command.Parameters.AddWithValue("$step", "context/context");
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await engine.RestartStepAsync(runId, "context/infer", TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var failed = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
            failed!.Status.Should().Be(WorkflowStatus.Failed);
            contextProvider.Requests.Should().ContainSingle();
            inference.Requests.Should().ContainSingle();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static SqliteWorkflowStore CreateStore(string databasePath) =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(2),
        });

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

    private static async Task<WorkflowAdmissionResult> AdmitChangedAsync()
    {
        var descriptor = new DescriptorReference(
            DescriptorKind.Activity,
            "sample.changed",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));
        var plan = CreatePlanWithActivity(descriptor, "echo");
        return await AdmitPlanAsync(plan);
    }

    private static async Task<WorkflowAdmissionResult> AdmitPlanAsync(WorkflowPlan plan)
    {
        var catalogue = plan.CatalogueBindings
            .Select(descriptor => new TrustedCatalogueDescriptor(
                descriptor,
                callableContract: new CallableContract(
                    new CallableSignature(
                        [new CallableParameter("value", new PrimitiveType(FuwenPrimitiveKind.String))],
                        new PrimitiveType(FuwenPrimitiveKind.String)),
                    CallableEffect.Read,
                    CallableIdempotency.Idempotent,
                    CallableRetrySafety.Safe)))
            .ToArray();
        return await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static WorkflowPlan CreatePlanWithActivity(DescriptorReference descriptor, string rootName)
    {
        var activityPath = StructuralNodeIdentity.Create(rootName, "echo");
        var returnPath = StructuralNodeIdentity.Create(rootName, "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            rootName,
            "1",
            new PrimitiveType(FuwenPrimitiveKind.String),
            new PrimitiveType(FuwenPrimitiveKind.String),
            "routing/1",
            [],
            [descriptor],
            new CapabilityManifest([]),
            [
                new ActivityNode(rootName, activityPath, descriptor,
                    [new ArgumentBinding("value", new InputBinding([]))],
                    new PrimitiveType(FuwenPrimitiveKind.String)),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion(rootName, [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private static async Task<WorkflowAdmissionResult> AdmitBranchingAsync()
    {
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        var descriptors = new[]
        {
            new DescriptorReference(DescriptorKind.Activity, "sample.a", "1", Digest('a')),
            new DescriptorReference(DescriptorKind.Activity, "sample.b", "1", Digest('b')),
            new DescriptorReference(DescriptorKind.Activity, "sample.c", "1", Digest('c')),
        };
        var stringType = new PrimitiveType(FuwenPrimitiveKind.String);
        var nodes = new WorkflowNode[]
        {
            new ActivityNode("a", StructuralNodeIdentity.Create("branch", "a"), descriptors[0],
                [new ArgumentBinding("value", new InputBinding([]))], stringType),
            new ActivityNode("b", StructuralNodeIdentity.Create("branch", "b"), descriptors[1],
                [new ArgumentBinding("value", new NodeOutputBinding(StructuralNodeIdentity.Create("branch", "a"), []))], stringType),
            new ActivityNode("c", StructuralNodeIdentity.Create("branch", "c"), descriptors[2],
                [new ArgumentBinding("value", new InputBinding([]))], stringType),
            new ReturnNode("return_result", StructuralNodeIdentity.Create("branch", "return_result"),
                new NodeOutputBinding(StructuralNodeIdentity.Create("branch", "b"), [])),
        };
        var plan = new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            "branch",
            "1",
            stringType,
            stringType,
            "routing/1",
            [],
            descriptors,
            new CapabilityManifest([]),
            nodes,
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("branch", [
                    new WorkflowExecutionPhase([nodes[0].StructuralPath, nodes[2].StructuralPath]),
                    new WorkflowExecutionPhase([nodes[1].StructuralPath]),
                    new WorkflowExecutionPhase([nodes[3].StructuralPath]),
                ]),
            ]));
        return await AdmitPlanAsync(plan);
    }

    private sealed class RecoveryActivity : IActivityExecutor
    {
        private int calls;
        private readonly ConcurrentDictionary<string, byte> committedEffects = new(StringComparer.Ordinal);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ActivityExecutionRequest> Requests { get; } = [];
        public int CommittedEffects => committedEffects.Count;

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            committedEffects.TryAdd(request.Invocation.OperationKey, 0);
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return ActivityExecutionResult.Succeeded(request.Arguments.Single().Value);
        }
    }

    private sealed class SafeInfrastructureRetryActivity : IActivityExecutor
    {
        private int calls;
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Interlocked.Increment(ref calls) == 1)
            {
                return ValueTask.FromResult(ActivityExecutionResult.Failed(new ExecutionFailure(
                    ExecutionFailureKind.Infrastructure,
                    ExecutionFailureCode.TransientInfrastructureFailure,
                    "temporary infrastructure outage",
                    ExecutionRetryDisposition.InfrastructureOnly,
                    mayHaveCommittedEffect: false)));
            }

            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(request.Arguments.Single().Value));
        }
    }

    private sealed class FinalFailureActivity : IActivityExecutor
    {
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ActivityExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Provider,
                ExecutionFailureCode.ProviderError,
                "provider rejected the request",
                ExecutionRetryDisposition.Never,
                mayHaveCommittedEffect: true,
                providerCode: "E_FINAL")));
        }
    }

    private sealed class BranchingActivity : IActivityExecutor
    {
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement(request.Activity.Name))));
        }
    }

    private sealed class CancellationActivity : IActivityExecutor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
            return ActivityExecutionResult.Succeeded(request.Arguments.Single().Value);
        }
    }

    private sealed class RecordingObserver : IExecutionObserver
    {
        public List<ExecutionObservation> Observations { get; } = [];
        public bool ThrowOnObserve { get; set; }

        public ValueTask ObserveAsync(
            ExecutionObservation observation,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnObserve)
                throw new InvalidOperationException("observer is intentionally unavailable");
            Observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }
}
