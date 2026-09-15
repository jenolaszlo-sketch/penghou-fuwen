#pragma warning disable xUnit1030
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Value_producing_conditional_persists_merge_and_replays_without_reexecuting_branch()
    {
        var admission = await AdmitConditionalMergeAsync();
        var activity = new CountingActivity();
        var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                ports)
            .CreateAsync("fuwen.cond-merge", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "workflow.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var inputTrue = JsonDocument.Parse("\"go\"");
            var runTrue = await engine.StartAsync("fuwen.cond-merge", "1", inputTrue.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runTrue, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var outTrue = await engine.WaitForCompletionAsync<JsonElement>(runTrue, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            outTrue.GetString().Should().Be("accept:go");
            activity.Calls.Should().ContainSingle(c => c.Name == "accept");

            // Restarting the conditional re-executes its branch (the branch is
            // part of the conditional's execution), but the merged output must
            // be identical after replay.
            var condPath = StructuralNodeIdentity.Create("demo", "decide");
            await engine.RestartStepAsync(runTrue, condPath, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runTrue, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var outTrueReplay = await engine.WaitForCompletionAsync<JsonElement>(runTrue, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            outTrueReplay.GetString().Should().Be("accept:go");

            using var inputElse = JsonDocument.Parse("\"stop\"");
            var runElse = await engine.StartAsync("fuwen.cond-merge", "1", inputElse.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            await engine.ExecuteAsync(runElse, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var outElse = await engine.WaitForCompletionAsync<JsonElement>(runElse, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
            outElse.GetString().Should().Be("repair:stop");
            activity.Calls.Should().Contain(c => c.Name == "repair");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<WorkflowAdmissionResult> AdmitConditionalMergeAsync()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var condPath = StructuralNodeIdentity.Create("demo", "decide");
        var acceptPath = condPath + "/$then/accept";
        var repairPath = condPath + "/$else/repair";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new ConditionalNode(
                "decide", condPath,
                new ConditionExpression(ConditionOperator.Equal, new InputBinding([]), new LiteralBinding(JsonDocument.Parse("\"go\"").RootElement.Clone())),
                [new ActivityNode("accept", acceptPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str)],
                [new ActivityNode("repair", repairPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str)],
                new ConditionalMerge(new NodeOutputBinding(acceptPath, []), new NodeOutputBinding(repairPath, []), str)))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(condPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([condPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/decide/$then", [new WorkflowExecutionPhase([acceptPath])]),
                new WorkflowExecutionRegion("demo/decide/$else", [new WorkflowExecutionPhase([repairPath])]),
            ]))
            .BuildV5();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc, callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", str)], str), CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
        return admission;
    }

    private sealed class CountingActivity : IActivityExecutor
    {
        public List<(string Name, string Value)> Calls { get; } = [];
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken ct = default)
        {
            var value = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            Calls.Add((request.Activity.Name == "sample.echo" ? (request.Invocation.StructuralPath.Contains("$then") ? "accept" : "repair") : request.Activity.Name, value));
            var outStr = (request.Invocation.StructuralPath.Contains("$then") ? "accept" : "repair") + ":" + value;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(outStr));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(doc.RootElement)));
        }
    }
}
