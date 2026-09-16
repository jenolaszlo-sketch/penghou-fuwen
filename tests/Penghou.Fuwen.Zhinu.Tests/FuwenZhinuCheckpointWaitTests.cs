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
    public async Task Checkpoint_node_persists_value_durably()
    {
        var admission = await AdmitCheckpointAsync();
        var ports = new FuwenZhinuExecutionPorts(new PassthroughActivity(), new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.checkpoint", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"hello\"");
            var runId = await engine.StartAsync("fuwen.checkpoint", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("hello");

            var steps = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Should().Contain(s => s.StepKey == "demo/saved" && s.Status == StepStatus.Completed);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Wait_node_suspends_until_signal_delivered()
    {
        var admission = await AdmitWaitAsync();
        var ports = new FuwenZhinuExecutionPorts(new PassthroughActivity(), new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.wait", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

            using var input = JsonDocument.Parse("\"go\"");
            var runId = await engine.StartAsync("fuwen.wait", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var execution = engine.ExecuteAsync(runId, cts.Token);

            // Wait until the step reaches Waiting status
            while (true)
            {
                var steps = await engine.GetStepsAsync(runId, cts.Token);
                if (steps.Any(s => s.StepKey == "demo/approval" && s.Status == StepStatus.Waiting))
                    break;
                await Task.Delay(25, cts.Token);
            }

            // Deliver signal
            await engine.SendSignalAsync(runId, "approval_request", "approved", cts.Token);
            await execution;

            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("approved");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<WorkflowAdmissionResult> AdmitCheckpointAsync()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var checkpointPath = StructuralNodeIdentity.Create("demo", "saved");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new CheckpointNode("saved", checkpointPath, new InputBinding([]), str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(checkpointPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([checkpointPath]), new WorkflowExecutionPhase([returnPath])]),
            ]))
            .BuildV7();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue($"Diagnostics: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} (path={d.Path})"))}");
        return admission;
    }

    private static async Task<WorkflowAdmissionResult> AdmitWaitAsync()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var waitPath = StructuralNodeIdentity.Create("demo", "approval");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new WaitNode("approval", waitPath, "approval_request", str))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(waitPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([waitPath]), new WorkflowExecutionPhase([returnPath])]),
            ]))
            .BuildV7();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue($"Diagnostics: {string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message} (path={d.Path})"))}");
        return admission;
    }

    private sealed class PassthroughActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value;
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(value.Clone())));
        }
    }

    [Fact]
    public async Task Approval_gate_deny_leaves_generation_resumable()
    {
        var admission = await AdmitWaitAsync();
        var ports = new FuwenZhinuExecutionPorts(new PassthroughActivity(), new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.approval", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

            // First run: send "denied" signal
            using var input = JsonDocument.Parse("\"review\"");
            var runId = await engine.StartAsync("fuwen.approval", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var execution = engine.ExecuteAsync(runId, cts.Token);

            while (true)
            {
                var steps = await engine.GetStepsAsync(runId, cts.Token);
                if (steps.Any(s => s.StepKey == "demo/approval" && s.Status == StepStatus.Waiting))
                    break;
                await Task.Delay(25, cts.Token);
            }

            await engine.SendSignalAsync(runId, "approval_request", "denied", cts.Token);
            await execution;

            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("denied");

            // The run completed; restart it to prove resumability
            var restart = await engine.RestartStepAsync(runId, "demo/approval", TestContext.Current.CancellationToken);
            restart.StepsToInvalidate.Should().NotBeEmpty();

            // Second run: send "approved" signal
            var execution2 = engine.ExecuteAsync(runId, cts.Token);

            while (true)
            {
                var steps = await engine.GetStepsAsync(runId, cts.Token);
                if (steps.Any(s => s.StepKey == "demo/approval" && s.Status == StepStatus.Waiting))
                    break;
                await Task.Delay(25, cts.Token);
            }

            await engine.SendSignalAsync(runId, "approval_request", "approved", cts.Token);
            await execution2;

            var output2 = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            output2.GetString().Should().Be("approved");
        }
        finally { DeleteDirectory(root); }
    }
#pragma warning restore xUnit1030
}
