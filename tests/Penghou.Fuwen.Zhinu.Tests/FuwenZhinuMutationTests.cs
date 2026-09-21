using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>
/// R28: durable step evidence produced under one admitted plan can be reused
/// by a forked run bound to a later admitted plan. The reused evidence keeps
/// its original execution fingerprint and runtime path, so the host must
/// declare the prior fingerprint and the adapter must accept the fork.
/// </summary>
public sealed class FuwenZhinuMutationTests
{
    [Fact]
    public async Task SameFingerprint_fork_reuses_evidence_only_when_current_fingerprint_is_declared()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(BuildChainPlan("1", withC: false), ct);
        admission.Succeeded.Should().BeTrue();
        var fingerprint = admission.Receipt!.ExecutionFingerprint;
        var activity = new CountingActivity();
        var identity = new FuwenZhinuProviderRuntimeIdentity(
            admission.Receipt.CatalogueSnapshotRevision,
            admission.Receipt.ResolvedDescriptorSetFingerprint);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                {
                    // Explicitly authorizes copied evidence from another run
                    // of this exact plan; omission retains strict run fencing.
                    PriorExecutionFingerprints = new HashSet<string>([fingerprint], StringComparer.Ordinal),
                })
            .CreateAsync("mutate", "1", admission, ct);

        var registry = new WorkflowRegistry();
        registration.Register(registry);
        var root = CreateTempRoot();
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
                registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"goal\"");
            var sourceId = await engine.StartAsync("mutate", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(sourceId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(sourceId, cancellationToken: ct))
                .GetString().Should().Be("goal.a.b");

            var forkedId = await engine.ForkAsync(sourceId, "mutate/b", cancellationToken: ct);
            await engine.ExecuteAsync(forkedId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(forkedId, cancellationToken: ct))
                .GetString().Should().Be("goal.a.b");

            activity.Calls.Should().Equal("a:goal", "b:goal.a", "b:goal.a");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CrossVersion_fork_reuses_prior_evidence_with_declared_fingerprint()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstAdmission = await AdmitAsync(BuildChainPlan("1", withC: false), ct);
        var changedAdmission = await AdmitAsync(BuildChainPlan("2", withC: true), ct);
        firstAdmission.Succeeded.Should().BeTrue();
        changedAdmission.Succeeded.Should().BeTrue();
        changedAdmission.Receipt!.ExecutionFingerprint
            .Should().NotBe(firstAdmission.Receipt!.ExecutionFingerprint);

        var activity = new CountingActivity();
        var identity = new FuwenZhinuProviderRuntimeIdentity(
            firstAdmission.Receipt!.CatalogueSnapshotRevision,
            firstAdmission.Receipt.ResolvedDescriptorSetFingerprint);
        var firstRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("mutate", "1", firstAdmission, ct);
        var changedRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())
                {
                    PriorExecutionFingerprints = new HashSet<string>(
                        [firstAdmission.Receipt.ExecutionFingerprint], StringComparer.Ordinal),
                })
            .CreateAsync("mutate", "2", changedAdmission, ct);

        var registry = new WorkflowRegistry();
        firstRegistration.Register(registry);
        changedRegistration.Register(registry);
        var root = CreateTempRoot();

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
                registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"goal\"");
            var sourceId = await engine.StartAsync(
                "mutate", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(sourceId, ct);
            var sourceOutput = await engine.WaitForCompletionAsync<JsonElement>(sourceId, cancellationToken: ct);
            sourceOutput.GetString().Should().Be("goal.a.b");
            activity.Calls.Should().Equal("a:goal", "b:goal.a");

            // Fork at the new node: a and b are reused, c runs, and the return
            // node (whose input now comes from c) is superseded and re-runs.
            var migratedId = await engine.ForkAsync(
                sourceId,
                "mutate/c",
                new ForkRunOptions { TargetWorkflowVersion = "2", Actor = "test", Reason = "plan changed" },
                ct);
            var migrated = await engine.GetRunAsync(migratedId, ct);
            migrated!.WorkflowVersion.Should().Be("2");
            migrated.SourceRunId.Should().Be(sourceId);

            await engine.ExecuteAsync(migratedId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(migratedId, cancellationToken: ct);
            output.GetString().Should().Be("goal.a.b.c");
            activity.Calls.Should().Equal("a:goal", "b:goal.a", "c:goal.a.b");

            (await engine.GetRunAsync(migratedId, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CrossVersion_fork_rejects_prior_evidence_when_fingerprint_is_not_declared()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstAdmission = await AdmitAsync(BuildChainPlan("1", withC: false), ct);
        var changedAdmission = await AdmitAsync(BuildChainPlan("2", withC: true), ct);
        var activity = new CountingActivity();
        var identity = new FuwenZhinuProviderRuntimeIdentity(
            firstAdmission.Receipt!.CatalogueSnapshotRevision,
            firstAdmission.Receipt.ResolvedDescriptorSetFingerprint);
        var firstRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("mutate", "1", firstAdmission, ct);
        var changedRegistration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                identity,
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("mutate", "2", changedAdmission, ct);

        var registry = new WorkflowRegistry();
        firstRegistration.Register(registry);
        changedRegistration.Register(registry);
        var root = CreateTempRoot();

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
                registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"goal\"");
            var sourceId = await engine.StartAsync(
                "mutate", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(sourceId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(sourceId, cancellationToken: ct);

            var migratedId = await engine.ForkAsync(
                sourceId,
                "mutate/c",
                new ForkRunOptions { TargetWorkflowVersion = "2" },
                ct);
            await engine.ExecuteAsync(migratedId, ct);

            var failed = await engine.GetRunAsync(migratedId, ct);
            failed!.Status.Should().Be(WorkflowStatus.Failed);
            failed.Error!.Message.Should().Contain("invocation evidence that does not match");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static WorkflowPlan BuildChainPlan(string version, bool withC)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var aPath = StructuralNodeIdentity.Create("mutate", "a");
        var bPath = StructuralNodeIdentity.Create("mutate", "b");
        var cPath = StructuralNodeIdentity.Create("mutate", "c");
        var returnPath = StructuralNodeIdentity.Create("mutate", "return_result");
        var builder = new WorkflowPlanBuilder("mutate", version, str, str, "routing/1")
            .AddNode(new ActivityNode("a", aPath, Descriptor,
                [new ArgumentBinding("value", new InputBinding([]))], str))
            .AddNode(new ActivityNode("b", bPath, Descriptor,
                [new ArgumentBinding("value", new NodeOutputBinding(aPath, []))], str));
        var phases = new List<WorkflowExecutionPhase>([
            new WorkflowExecutionPhase([aPath]),
            new WorkflowExecutionPhase([bPath]),
        ]);
        if (withC)
        {
            builder.AddNode(new ActivityNode("c", cPath, Descriptor,
                [new ArgumentBinding("value", new NodeOutputBinding(bPath, []))], str));
            phases.Add(new WorkflowExecutionPhase([cPath]));
            phases.Add(new WorkflowExecutionPhase([returnPath]));
            return builder
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(cPath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([new WorkflowExecutionRegion("mutate", phases)]))
                .BuildV3();
        }

        phases.Add(new WorkflowExecutionPhase([returnPath]));
        return builder
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(bPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([new WorkflowExecutionRegion("mutate", phases)]))
            .BuildV3();
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan, CancellationToken ct)
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
            .AdmitAsync(plan, cancellationToken: ct);
    }

    private static DescriptorReference Descriptor { get; } = new(
        DescriptorKind.Activity,
        "sample.chain",
        "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        for (var i = 0; i < 5 && Directory.Exists(root); i++)
        {
            try { Directory.Delete(root, true); return; }
            catch { Thread.Sleep(50 * (i + 1)); }
        }
    }

    private sealed class CountingActivity : IActivityExecutor
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            var node = request.Invocation.StructuralPath.Split('/')[^1];
            var input = ((JsonRuntimeValue)request.Arguments.Single(a => a.Name == "value").Value)
                .Value.GetString()!;
            Calls.Add($"{node}:{input}");
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(input + "." + node));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement)));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");
    }
}
