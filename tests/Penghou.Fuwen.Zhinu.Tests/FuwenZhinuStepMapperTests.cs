using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

// Exactness/no-drift guard for the public Fuwen-to-Zhinu step map. Every test
// runs a real WorkflowEngine execution and asserts each persisted
// WorkflowStepRun.StepKey is matched exactly once by FuwenZhinuStepMapper, and
// that every declared node is represented even when it never executes. Because
// Zhinu's durable-loop key helpers are internal, these execution-backed
// assertions are the compatibility guard: any Zhinu key-convention drift must
// fail here rather than silently changing the public mapper.
//
// Documented limitations (not blockers for the mapping contract):
// - nested declared repeats are not currently executable by the port, so there
//   is no emitted StepKey family to prove;
// - inference-protocol keys are classified by the matcher and representative
//   forms are covered below; execution-backed exactness is deferred until a
//   protocol host with a turn executor and budget ledger exists.
public sealed partial class FuwenZhinuSequentialInterpreterTests
{
#pragma warning disable xUnit1030

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_activity_and_return()
    {
        var admission = await AdmitAsync();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new RecordingActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.echo", "1", admission, TestContext.Current.CancellationToken);

        await ExecuteAndAssertExactAsync(registration, "fuwen.echo", "1", "\"hello\"", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_conditional_merge()
    {
        var admission = await AdmitConditionalMergeAsync();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new CountingActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.cond-merge", "1", admission, TestContext.Current.CancellationToken);

        await ExecuteAndAssertExactAsync(registration, "fuwen.cond-merge", "1", "\"go\"", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_checkpoint()
    {
        var admission = await AdmitCheckpointAsync();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new PassthroughActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.checkpoint", "1", admission, TestContext.Current.CancellationToken);

        await ExecuteAndAssertExactAsync(registration, "fuwen.checkpoint", "1", "\"hello\"", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_repeat_with_multiple_iterations()
    {
        var admission = await AdmitRepeatAsync(maxIterations: 5, breakOnIter3: true);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new RepeatCountingActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.repeat", "1", admission, TestContext.Current.CancellationToken);

        var steps = await ExecuteAndAssertExactAsync(registration, "fuwen.repeat", "1", "\"start\"", TestContext.Current.CancellationToken);

        steps.Should().Contain(s => s.StepKey == "$loop/loop1/3/body/step");
        steps.Should().Contain(s => s.StepKey == "$loop/loop1/1/condition");
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_fan_out_with_multiple_keys()
    {
        var fixture = await AdmitFanOutAsync();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    new UppercaseActivity(),
                    new UnusedContext(),
                    new UnusedInference(),
                    observer: null,
                    new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
            .CreateAsync("fuwen.fanout", "1", fixture.Admission, TestContext.Current.CancellationToken);

        var steps = await ExecuteAndAssertExactAsync(registration, "fuwen.fanout", "1", "[\"b\",\"a\",\"c\"]", TestContext.Current.CancellationToken);

        steps.Should().Contain(s => s.StepKey.StartsWith($"{fixture.FanOutPath}/$item/sha256-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_wait()
    {
        var admission = await AdmitWaitAsync();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new PassthroughActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("fuwen.wait", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ct = TestContext.Current.CancellationToken;
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
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
            using var input = JsonDocument.Parse("\"go\"");
            var runId = await engine.StartAsync("fuwen.wait", "1", input.RootElement.Clone(), cancellationToken: ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var execution = engine.ExecuteAsync(runId, cts.Token);
            while (true)
            {
                var waiting = await engine.GetStepsAsync(runId, cts.Token);
                if (waiting.Any(s => s.StepKey == "demo/approval" && s.Status == StepStatus.Waiting))
                {
                    break;
                }

                await Task.Delay(25, cts.Token);
            }

            await engine.SendSignalAsync(runId, "approval_request", "approved", cts.Token);
            await execution;
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            AssertExact(registration, await engine.GetStepsAsync(runId, ct));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Step_map_matches_every_persisted_key_for_inference_fallback()
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
        var catalogue = fixture.Descriptors
            .Select(descriptor => new TrustedCatalogueDescriptor(descriptor.Reference, callableContract: descriptor.Contract))
            .ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(catalogue),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));

        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    CurrentInferenceFixture.WithPreflight(new FailingInference())))
            .CreateAsync("fuwen.context", "1", admission, ct);

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
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("fuwen.context", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct);
            var steps = await engine.GetStepsAsync(run, ct);
            steps.Should().Contain(step => step.StepKey == "context/infer/$fallback");
            AssertExact(registration, steps);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Step_map_exposes_inference_protocol_loop_and_matches_its_steps_without_parsing()
    {
        var fixture = CreateContextInferencePlan();
        var plan = fixture.Plan with
        {
            Nodes = fixture.Plan.Nodes.Select(node => node is InferenceNode inference
                ? inference with { Protocol = CurrentInferenceFixture.OneCallProtocol }
                : node).ToArray(),
        };

        var map = FuwenZhinuStepMapper.Map(plan);
        var protocol = map.Steps.Single(descriptor => descriptor.Kind == FuwenZhinuStepKind.InferenceProtocol);
        protocol.LoopNames.Should().HaveCount(1);
        var protocolLoopName = protocol.LoopNames[0];
        protocolLoopName.Should().StartWith("infer-protocol-");

        foreach (var stepName in new[] { "infer-model-turn-0001", "infer-read-tool-0001-call" })
        {
            var key = $"$loop/{protocolLoopName}/1/body/{stepName}";
            FuwenZhinuStepMapper.TryMatchStepKey(map, key, out var match).Should().BeTrue($"'{key}' must match the protocol loop");
            match.Descriptor.Kind.Should().Be(FuwenZhinuStepKind.InferenceProtocol);
            match.Scope.Single(segment => segment.Kind == FuwenZhinuStepScopeKind.Loop).Iteration.Should().Be(1);
        }
    }

    private static async Task<IReadOnlyList<WorkflowStepRun>> ExecuteAndAssertExactAsync(
        FuwenZhinuWorkflowRegistration registration,
        string workflowName,
        string workflowVersion,
        string inputJson,
        CancellationToken ct)
    {
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
            using var input = JsonDocument.Parse(inputJson);
            var runId = await engine.StartAsync(workflowName, workflowVersion, input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            var steps = await engine.GetStepsAsync(runId, ct);
            AssertExact(registration, steps);
            return steps;
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static void AssertExact(
        FuwenZhinuWorkflowRegistration registration,
        IReadOnlyList<WorkflowStepRun> steps)
    {
        var plan = registration.Definition.ReadPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        map.SchemaVersion.Should().Be(FuwenZhinuStepMapper.CurrentSchemaVersion);
        map.ExecutionFingerprint.Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
        map.PlanRevision.Should().Be(plan.Revision);

        steps.Should().NotBeEmpty();
        foreach (var step in steps)
        {
            FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match)
                .Should().BeTrue($"persisted step key '{step.StepKey}' must be classified by the port map");
            match.Descriptor.Should().NotBeNull();
        }

        // Inverse: every declared node is represented, even if it never executed.
        var declared = FlattenDeclared(plan.Nodes).Select(node => node.StructuralPath).ToHashSet(StringComparer.Ordinal);
        var represented = map.Steps
            .SelectMany(descriptor =>
                descriptor.DeclaredNodePath is { } path
                    ? new[] { path }.Concat(descriptor.CoveredDeclaredNodePaths)
                    : descriptor.CoveredDeclaredNodePaths)
            .ToHashSet(StringComparer.Ordinal);
        declared.Should().BeSubsetOf(represented);

        // Map match identity is unique, so a persisted key matches exactly one descriptor.
        map.Steps.Select(MatchKey).Should().OnlyHaveUniqueItems();
    }

    private static string MatchKey(FuwenZhinuStepDescriptor descriptor)
    {
        if (descriptor.ExactStepKey is { } exact)
        {
            return "exact:" + exact;
        }

        var loops = string.Join("/", descriptor.LoopNames);
        if (descriptor.FanOutNodePath is { } fanOut)
        {
            return "fanout:" + fanOut;
        }

        return descriptor.Kind switch
        {
            FuwenZhinuStepKind.InferenceProtocol => "protocol:" + loops,
            FuwenZhinuStepKind.LoopControl => "control:" + loops,
            _ => "body:" + loops + ":" + descriptor.BodyStepName,
        };
    }

    private static IEnumerable<WorkflowNode> FlattenDeclared(IEnumerable<WorkflowNode> source)
    {
        foreach (var node in source)
        {
            yield return node;
            var children = node switch
            {
                ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
                FanOutNode fanOut => fanOut.Body,
                RepeatNode repeat => repeat.Body,
                _ => Enumerable.Empty<WorkflowNode>(),
            };
            foreach (var child in FlattenDeclared(children))
            {
                yield return child;
            }
        }
    }
#pragma warning restore xUnit1030
}
