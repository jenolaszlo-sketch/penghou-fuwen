using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Grouped_repeat_creates_one_nested_occurrence_tree_per_iteration_and_maps_every_step()
    {
        const string source = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              checkpoint saved value inner -> string;
              yield saved;
            }
            workflow demo(input: string) -> string {
              group root {
                repeat loop max 3 state current: string = input {
                  group iteration {
                    use call = Echo(value: current);
                    yield call;
                  }
                } continue iteration break iter == 3 -> string;
                yield loop;
              }
              return root;
            }
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-repeat", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root),
                    IdentityFor(admission.Admission), new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-repeat", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            var runId = await engine.StartAsync("grouped-repeat", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("start");
            activity.Requests.Should().HaveCount(3);

            var steps = await store.GetStepsAsync(runId, ct);
            AssertEveryStepMatches(registration.Definition.ReadPlan(), steps);
            var bodySteps = await ReadIterationBodySteps(store, registration.Definition.ReadPlan(), steps, ct);
            bodySteps.Select(item => item.Iteration).Distinct().Should().BeEquivalentTo([1, 2, 3]);
            bodySteps.Select(item => item.Step.Id).Should().OnlyHaveUniqueItems();
            bodySteps.Should().OnlyContain(item => item.Membership != null &&
                (item.MemberPath.EndsWith("/inner", StringComparison.Ordinal)
                    ? item.Membership.Role == ActivityGroupMemberRole.Work
                    : item.MemberPath.EndsWith("/saved", StringComparison.Ordinal) && item.Membership.Role == ActivityGroupMemberRole.Control));

            using var reader = new WorkflowGroupReader(store);
            var occurrences = await ReadGroupOccurrences(reader, runId, ct);
            var rootOccurrence = occurrences.Should().ContainSingle(item => item.GroupKey == "demo/$group/root").Which;
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var controlSteps = steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.LoopControl).ToArray();
            controlSteps.Should().NotBeEmpty();
            foreach (var control in controlSteps)
            {
                var membership = await store.GetStepGroupMembershipAsync(control.Id, ct);
                membership.Should().NotBeNull(control.StepKey);
                membership!.GroupOccurrenceId.Should().Be(rootOccurrence.OccurrenceId);
                membership.Role.Should().Be(ActivityGroupMemberRole.Control);
            }
            var iterations = occurrences.Where(item => item.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal)).ToArray();
            iterations.Should().HaveCount(3);
            iterations.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();
            iterations.Should().OnlyContain(item => item.ParentOccurrenceId == rootOccurrence.OccurrenceId);
            var calls = occurrences.Where(item => item.GroupKey.EndsWith("/$group/call", StringComparison.Ordinal)).ToArray();
            calls.Should().HaveCount(3);
            foreach (var call in calls)
            {
                var iterationParent = iterations.Single(item => item.OccurrenceId == call.ParentOccurrenceId);
                var callIteration = bodySteps.Where(item => item.Membership!.GroupOccurrenceId == call.OccurrenceId)
                    .Select(item => item.Iteration).Distinct().Should().ContainSingle().Which;
                callIteration.Should().BeInRange(1, 3);
                iterationParent.ParentOccurrenceId.Should().Be(rootOccurrence.OccurrenceId);
            }
            calls.Select(call => bodySteps.Where(item => item.Membership!.GroupOccurrenceId == call.OccurrenceId)
                    .Select(item => item.Iteration).Distinct().Single())
                .Should().BeEquivalentTo([1, 2, 3]);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Same_named_repeat_loops_in_sibling_macros_have_distinct_native_keys_and_group_trees()
    {
        const string source = """
            macro EchoLoop(value: string) {
              repeat loop max 2 state current: string = value {
                group iteration {
                  activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: current;) -> string;
                  checkpoint saved value inner -> string;
                  yield saved;
                }
              } continue iteration break iter == 2 -> string;
              yield loop;
            }
            workflow demo(input: string) -> string {
              group root {
                use first = EchoLoop(value: input);
                use second = EchoLoop(value: first);
                yield second;
              }
              return root;
            }
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-repeat-siblings", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root),
                    IdentityFor(admission.Admission), new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-repeat-siblings", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            var runId = await engine.StartAsync("grouped-repeat-siblings", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("start");
            activity.Requests.Should().HaveCount(4);

            var steps = await store.GetStepsAsync(runId, ct);
            AssertEveryStepMatches(registration.Definition.ReadPlan(), steps);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var loopKeys = map.Steps.Where(descriptor => descriptor.Kind == FuwenZhinuStepKind.LoopControl)
                .SelectMany(descriptor => descriptor.LoopNames).Distinct(StringComparer.Ordinal).ToArray();
            loopKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();

            using var reader = new WorkflowGroupReader(store);
            var occurrences = await ReadGroupOccurrences(reader, runId, ct);
            var rootOccurrence = occurrences.Should().ContainSingle(item => item.GroupKey == "demo/$group/root").Which;
            var invocations = occurrences.Where(item => item.GroupKey is "demo/$group/root/$group/first" or "demo/$group/root/$group/second").ToArray();
            invocations.Should().HaveCount(2);
            invocations.Should().OnlyContain(item => item.ParentOccurrenceId == rootOccurrence.OccurrenceId);
            var iterations = occurrences.Where(item => item.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal)).ToArray();
            iterations.Should().HaveCount(4);
            foreach (var invocation in invocations)
                iterations.Where(item => item.ParentOccurrenceId == invocation.OccurrenceId).Should().HaveCount(2);
            invocations.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();
            iterations.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Resuming_at_iteration_two_wait_preserves_iteration_one_membership_and_does_not_repeat_its_activity()
    {
        const string source = """
            macro Review(value: string) {
              activity work = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              wait approval signal approve type string;
              checkpoint saved value approval -> string;
              yield saved;
            }
            workflow demo(input: string) -> string {
              group root {
                repeat loop max 3 state current: string = input {
                  group iteration {
                    use review = Review(value: current);
                    yield review;
                  }
                } continue iteration break iter == 3 -> string;
                yield loop;
              }
              return root;
            }
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-repeat-recovery", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var definitions = new DirectoryWorkflowDefinitionStore(root);
            var registration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-repeat-recovery", "1", admission, ct);
            var databasePath = Path.Combine(root, "run.db");
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
            var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5), LeaseDuration = TimeSpan.FromMilliseconds(200), LeaseRenewalInterval = TimeSpan.FromMilliseconds(50) });
            Guid runId;
            WorkflowStepRun firstWait;
            WorkflowStepRun secondWait;
            WorkflowStepRun[] iterationOneSteps;
            Dictionary<Guid, ActivityGroupStepMembership> iterationOneMemberships;
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            using (var firstInterruption = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    runId = await engine.StartAsync("grouped-repeat-recovery", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: ct);
                    var firstExecution = engine.ExecuteAsync(runId, firstInterruption.Token);
                    firstWait = await WaitForNewWaitingStep(store, runId, [], ct);
                    await firstInterruption.CancelAsync();
                    try { await firstExecution; }
                    catch (OperationCanceledException) { }
                    await engine.SendSignalAsync(runId, "approve", "first", ct);
                    using var iterationTwoInterruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var secondExecution = engine.ExecuteAsync(runId, iterationTwoInterruption.Token);
                    secondWait = await WaitForNewWaitingStep(store, runId, [firstWait.StepKey], ct);
                    var beforeInterruption = await store.GetStepsAsync(runId, ct);
                    iterationOneSteps = (await ReadIterationBodySteps(store, registration.Definition.ReadPlan(), beforeInterruption, ct))
                        .Where(item => item.Iteration == 1).Select(item => item.Step).ToArray();
                    iterationOneSteps.Should().NotBeEmpty();
                    iterationOneSteps.Should().OnlyContain(step => step.Status == StepStatus.Completed);
                    iterationOneMemberships = new Dictionary<Guid, ActivityGroupStepMembership>();
                    foreach (var step in iterationOneSteps)
                        iterationOneMemberships.Add(step.Id, (await store.GetStepGroupMembershipAsync(step.Id, ct))!);
                    iterationOneMemberships.Values.Should().OnlyContain(membership => membership.StepId != Guid.Empty);
                    secondWait.StepKey.Should().NotBe(firstWait.StepKey);
                    activity.Requests.Should().HaveCount(2);

                    await iterationTwoInterruption.CancelAsync();
                    try { await secondExecution; }
                    catch (OperationCanceledException) { }
                }
                finally { await engine.DisposeAsync(); }
            }

            var recoveredStore = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
            await Task.Delay(250, ct);
            await recoveredStore.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, ct);
            await using var recovered = new WorkflowEngine(recoveredStore, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5), LeaseDuration = TimeSpan.FromMilliseconds(200), LeaseRenewalInterval = TimeSpan.FromMilliseconds(50) });
            await recovered.SendSignalAsync(runId, "approve", "second", ct);
            using var secondInterruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var recoveredExecution = recovered.ExecuteAsync(runId, secondInterruption.Token);
            var thirdWait = await WaitForNewWaitingStep(recoveredStore, runId, [firstWait.StepKey, secondWait.StepKey], ct);
            thirdWait.StepKey.Should().NotBe(secondWait.StepKey);
            await recovered.SendSignalAsync(runId, "approve", "third", ct);
            await recoveredExecution;
            await recovered.ExecuteAsync(runId, ct);
            (await recovered.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("third");
            activity.Requests.Should().HaveCount(3, "committed work in iteration one must replay without another provider call");

            var after = await recoveredStore.GetStepsAsync(runId, ct);
            foreach (var before in iterationOneSteps)
            {
                var current = after.Single(step => step.StepKey == before.StepKey);
                current.Id.Should().Be(before.Id);
                current.Revision.Should().Be(before.Revision);
                (await recoveredStore.GetStepGroupMembershipAsync(current.Id, ct)).Should().BeEquivalentTo(iterationOneMemberships[current.Id]);
            }
            using var reader = new WorkflowGroupReader(recoveredStore);
            var occurrences = await ReadGroupOccurrences(reader, runId, ct);
            var rootOccurrence = occurrences.Should().ContainSingle(item => item.GroupKey == "demo/$group/root").Which;
            var iterationOccurrences = occurrences.Where(item => item.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal)).ToArray();
            iterationOccurrences.Should().HaveCount(3);
            iterationOccurrences.Should().OnlyContain(item => item.ParentOccurrenceId == rootOccurrence.OccurrenceId);
            var firstIterationGroupId = iterationOneMemberships.Values.Select(item => item.GroupOccurrenceId).Distinct().Should().ContainSingle().Which;
            occurrences.Should().Contain(item => item.OccurrenceId == firstIterationGroupId);
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("other")]
    public async Task Grouped_repeat_inside_conditional_merge_preserves_control_dependencies(string input)
    {
        const string source = """
            workflow demo(input: string) -> string {
              group root {
                if decision input == "start" {
                  repeat loop max 1 state current: string = input {
                    activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: current;) -> string;
                  } continue inner break iter == 1 -> string;
                } else {
                  checkpoint alternative value input -> string;
                } merge loop, alternative -> string;
                yield decision;
              }
              return root;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var plan = admission.Definition.Definition.ReadPlan();
        var decision = plan.Nodes.OfType<ConditionalNode>().Should().ContainSingle().Subject;
        var repeat = decision.Then.OfType<RepeatNode>().Should().ContainSingle().Subject;
        var loopCondition = repeat.Body.OfType<ActivityNode>().Should().ContainSingle().Subject;
        var alternative = decision.Else.OfType<CheckpointNode>().Should().ContainSingle().Subject;
        var returned = plan.Nodes.OfType<ReturnNode>().Should().ContainSingle().Subject;

        repeat.ContinueWith.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Be(loopCondition.StructuralPath);
        decision.Merge.Should().NotBeNull();
        decision.Merge!.ThenValue.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Be(repeat.StructuralPath);
        decision.Merge.ElseValue.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Be(alternative.StructuralPath);
        returned.Value.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Be(decision.StructuralPath);
        admission.Definition.Groups.Groups.Should().ContainSingle(group => group.GroupKey == "demo/$group/root");
        var root = Path.Combine(Path.GetTempPath(), "fuwen-repeat-conditional", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())).CreateGroupedAsync("branch", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var run = await engine.StartAsync("branch", "1", JsonSerializer.SerializeToElement(input), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct)).GetString().Should().Be(input);
            var steps = await store.GetStepsAsync(run, ct);
            AssertEveryStepMatches(plan, steps);
            var graph = await engine.GetDependencyGraphAsync(run, ct);
            var mergeKey = decision.StructuralPath + "/$merge";
            graph.Should().Contain(new StepDependency(mergeKey, decision.StructuralPath));
            graph.Should().Contain(new StepDependency(returned.StructuralPath, mergeKey));
            if (input == "start")
            {
                var map = FuwenZhinuStepMapper.Map(plan);
                var loopKey = map.Steps.First(step => step.Kind == FuwenZhinuStepKind.LoopControl && step.DeclaredNodePath == repeat.StructuralPath).LoopNames.Single();
                graph.Should().Contain(new StepDependency("$loop/" + loopKey + "/1/condition", decision.StructuralPath));
                graph.Should().NotContain(new StepDependency("$loop/" + loopKey + "/1/condition", mergeKey));
                graph.Should().Contain(new StepDependency(mergeKey, loopKey));
                activity.Requests.Should().ContainSingle();
            }
            else activity.Requests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    private static void AssertEveryStepMatches(WorkflowPlan plan, IReadOnlyList<WorkflowStepRun> steps)
    {
        var map = FuwenZhinuStepMapper.Map(plan);
        steps.Should().NotBeEmpty();
        foreach (var step in steps)
        {
            FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
            if (match.Descriptor.ExactStepKey is not null || match.Descriptor.BodyStepName is not null || match.Descriptor.FanOutNodePath is not null)
                FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
        }
    }

    private static async Task<IReadOnlyList<(WorkflowStepRun Step, int Iteration, string MemberPath, ActivityGroupStepMembership? Membership)>>
        ReadIterationBodySteps(SqliteWorkflowStore store, WorkflowPlan plan, IReadOnlyList<WorkflowStepRun> steps, CancellationToken ct)
    {
        var map = FuwenZhinuStepMapper.Map(plan);
        var result = new List<(WorkflowStepRun, int, string, ActivityGroupStepMembership?)>();
        foreach (var step in steps)
        {
            if (!FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) || match.Descriptor.Kind == FuwenZhinuStepKind.LoopControl) continue;
            var iteration = match.Scope.FirstOrDefault(scope => scope.Kind == FuwenZhinuStepScopeKind.Loop)?.Iteration;
            if (iteration is null || match.Descriptor.DeclaredNodePath is not { } path) continue;
            result.Add((step, iteration.Value, path, await store.GetStepGroupMembershipAsync(step.Id, ct)));
        }
        return result;
    }

    private static async Task<WorkflowStepRun> WaitForNewWaitingStep(SqliteWorkflowStore store, Guid runId,
        IReadOnlyCollection<string> excludeKeys, CancellationToken ct)
    {
        while (true)
        {
            var candidate = (await store.GetStepsAsync(runId, ct))
                .Where(step => step.Status == StepStatus.Waiting && !excludeKeys.Contains(step.StepKey, StringComparer.Ordinal))
                .OrderBy(step => step.CreatedAt).FirstOrDefault();
            if (candidate is not null) return candidate;
            await Task.Delay(10, ct);
        }
    }
}
