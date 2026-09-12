using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Keyed_fan_out_preserves_source_order_and_restarts_only_one_item()
    {
        var fixture = await AdmitFanOutAsync();
        var activity = new UppercaseActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    activity,
                    new UnusedContext(),
                    new UnusedInference(),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
            .CreateAsync("fuwen.fanout", "1", fixture.Admission, TestContext.Current.CancellationToken);
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
            using var input = JsonDocument.Parse("[\"b\",\"a\",\"c\"]");
            var runId = await engine.StartAsync(
                "fuwen.fanout",
                "1",
                input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var first = await engine.WaitForCompletionAsync<JsonElement>(
                runId,
                cancellationToken: TestContext.Current.CancellationToken);

            first.EnumerateArray().Select(static item => item.GetString())
                .Should().Equal("B!", "A!", "C!");
            activity.Calls.Count.Should().Be(6);
            activity.RootInputsWereWorkflowLists.Should().BeTrue();

            var itemPath = RuntimeNodeIdentity.CreateFanOutItem(
                fixture.FanOutPath,
                new StringRuntimeKey("a"));
            var restart = await engine.RestartStepAsync(
                runId,
                itemPath,
                cancellationToken: TestContext.Current.CancellationToken);
            restart.StepsToInvalidate.Select(static item => item.StepKey)
                .Should().Contain(itemPath).And.Contain(fixture.FanOutPath);

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var second = await engine.WaitForCompletionAsync<JsonElement>(
                runId,
                cancellationToken: TestContext.Current.CancellationToken);
            second.EnumerateArray().Select(static item => item.GetString())
                .Should().Equal("B!", "A!", "C!");
            activity.Calls.Count(call => call == ("sample.uppercase", "a")).Should().Be(2);
            activity.Calls.Count(call => call == ("sample.decorate", "A")).Should().Be(2);
            activity.Calls.Count.Should().Be(8);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Keyed_fan_out_rejects_duplicate_keys_before_child_work()
    {
        var fixture = await AdmitFanOutAsync();
        var activity = new UppercaseActivity();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.fanout.duplicates", "1", fixture.Admission, TestContext.Current.CancellationToken);
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
            using var input = JsonDocument.Parse("[\"same\",\"same\"]");
            var runId = await engine.StartAsync(
                "fuwen.fanout.duplicates",
                "1",
                input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);

            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status
                .Should().Be(WorkflowStatus.Failed);
            activity.Calls.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<FanOutFixture> AdmitFanOutAsync()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var list = new ListType(text, 3);
        var activity = new DescriptorReference(
            DescriptorKind.Activity,
            "sample.uppercase",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));
        var decorator = new DescriptorReference(
            DescriptorKind.Activity,
            "sample.decorate",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('f', 64)));
        var fanOutPath = StructuralNodeIdentity.Create("batch", "process");
        var bodyPath = $"{fanOutPath}/$body/uppercase";
        var decoratePath = $"{fanOutPath}/$body/decorate";
        var returnPath = StructuralNodeIdentity.Create("batch", "return_result");
        var fanOut = new FanOutNode(
            "process",
            fanOutPath,
            new InputBinding([]),
            new FanOutItemBinding("item", text),
            new FanOutItemValueBinding([]),
            [
                new ActivityNode(
                    "decorate",
                    decoratePath,
                    decorator,
                    [new ArgumentBinding("value", new NodeOutputBinding(bodyPath, []))],
                    text),
                new ActivityNode(
                    "uppercase",
                    bodyPath,
                    activity,
                    [
                        new ArgumentBinding("root", new InputBinding([])),
                        new ArgumentBinding("value", new FanOutItemValueBinding([])),
                    ],
                    text),
            ],
            new NodeOutputBinding(decoratePath, []),
            list,
            MaximumItems: 3,
            MaximumConcurrency: 2);
        var plan = new WorkflowPlanBuilder("batch", "1", list, list, "routing/1")
            .AddFanOut(fanOut)
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("batch", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [
                    new WorkflowExecutionPhase([bodyPath]),
                    new WorkflowExecutionPhase([decoratePath]),
                ]),
            ]))
            .BuildV4();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(
                        activity,
                        callableContract: new CallableContract(
                            new CallableSignature([
                                new CallableParameter("root", list),
                                new CallableParameter("value", text),
                            ], text),
                            CallableEffect.Read,
                            CallableIdempotency.Idempotent,
                            CallableRetrySafety.Safe)),
                    new TrustedCatalogueDescriptor(
                        decorator,
                        callableContract: new CallableContract(
                            new CallableSignature([new CallableParameter("value", text)], text),
                            CallableEffect.Read,
                            CallableIdempotency.Idempotent,
                            CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(static item => item.Message)));
        return new FanOutFixture(admission, fanOutPath);
    }

    private sealed class UppercaseActivity : IActivityExecutor
    {
        public ConcurrentBag<(string Activity, string Value)> Calls { get; } = [];
        public bool RootInputsWereWorkflowLists { get; private set; } = true;

        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Activity.Name == "sample.uppercase")
                RootInputsWereWorkflowLists &= request.Arguments.Single(item => item.Name == "root").Value is ListRuntimeValue;
            var value = ((JsonRuntimeValue)request.Arguments.Single(item => item.Name == "value").Value).Value.GetString()!;
            Calls.Add((request.Activity.Name, value));
            var output = request.Activity.Name == "sample.uppercase"
                ? value.ToUpperInvariant()
                : value + "!";
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement)));
        }
    }

    private sealed record FanOutFixture(WorkflowAdmissionResult Admission, string FanOutPath);
}
