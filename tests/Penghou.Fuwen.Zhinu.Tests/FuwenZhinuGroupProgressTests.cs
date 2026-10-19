using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grouped_fanout_progress_counts_durable_work_without_interpreting_provider_envelopes(bool providerFails)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-group-progress", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity(providerFails ? "a" : null);
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("fanout-progress", "1", admission, ct);
            var database = Path.Combine(directory, "run.db");
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = database, Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var runId = await engine.StartAsync("fanout-progress", "1", JsonSerializer.SerializeToElement(new[] { "a", "b" }), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            (await store.GetRunAsync(runId, ct))!.Status.Should().Be(providerFails ? WorkflowStatus.Failed : WorkflowStatus.Completed);

            // Reopen independently: query facts must not depend on interpreter caches.
            var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = database, Pooling = false });
            using var reader = new WorkflowGroupReader(reopened);
            var groups = await ReadGroupOccurrences(reader, runId, ct);
            var root = groups.Single(group => group.ParentOccurrenceId is null);
            var header = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
            var result = await ((IWorkflowGroupStateReader)reader).GetGroupStateAsync(runId, header.SnapshotToken, root.OccurrenceId, ct);
            result.Status.Should().Be(WorkflowGroupReadStatus.Success);
            var state = result.Value!.State;
            state.Coverage.Should().Be(WorkflowGroupCoverage.Complete);
            state.Work.Known.Should().Be(4);
            state.Work.Completed.Should().Be(4, "definitive provider failures are committed envelopes, not native failed steps");
            state.Work.Failed.Should().Be(0);
            state.Control.Known.Should().BeGreaterThanOrEqualTo(4);
            state.RunStatus.Should().Be(providerFails ? WorkflowStatus.Failed : WorkflowStatus.Completed);
            if (providerFails)
            {
                state.Reasons.Should().Contain(WorkflowGroupStateReason.RunFailed);
                state.IsExpansionClosed.Should().BeFalse();
                state.TotalCount.Should().BeNull();
            }
            else
            {
                state.Status.Should().Be(StepStatus.Completed);
                state.TotalCount.Should().Be(4);
                state.IsExpansionClosed.Should().BeTrue();
            }
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Grouped_fanout_progress_keeps_a_settled_sibling_complete_during_cancellation()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-group-progress-cancel", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new ProgressBarrierActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(),
                    observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
                .CreateGroupedAsync("fanout-progress-cancel", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            var runId = await engine.StartAsync("fanout-progress-cancel", "1", JsonSerializer.SerializeToElement(new[] { "a", "b" }), cancellationToken: ct);
            var execution = engine.ExecuteAsync(runId, ct);
            await activity.Blocked.Task.WaitAsync(ct);
            var token = RuntimeNodeIdentity.GetKeyPathSegment(new StringRuntimeKey("b"));
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            WorkflowStepRun? completion = null;
            while (completion is null)
            {
                completion = (await store.GetStepsAsync(runId, ct)).FirstOrDefault(step => step.Status == StepStatus.Completed &&
                    FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                    match.Descriptor.Kind == FuwenZhinuStepKind.FanOutItemCompletion && match.Scope.Any(scope => scope.ItemToken == token));
                if (completion is null) await Task.Delay(5, ct);
            }
            var membership = await store.GetStepGroupMembershipAsync(completion.Id, ct);
            using var reader = new WorkflowGroupReader(store);
            var groups = await ReadGroupOccurrences(reader, runId, ct);
            var root = groups.Single(group => group.ParentOccurrenceId is null);
            var before = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
            var beforeRoot = (await reader.GetGroupStateAsync(runId, before.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
            beforeRoot.Status.Should().Be(StepStatus.Running);
            beforeRoot.Work.Completed.Should().Be(3);
            beforeRoot.Work.Running.Should().Be(1);
            beforeRoot.TotalCount.Should().BeNull();
            var sibling = (await reader.GetGroupStateAsync(runId, before.SnapshotToken, membership!.GroupOccurrenceId, ct)).Value!.State;
            sibling.Status.Should().Be(StepStatus.Completed);
            sibling.IsExpansionClosed.Should().BeTrue();
            sibling.TotalCount.Should().Be(2);

            await engine.CancelAsync(runId, ct);
            await execution;
            activity.CancellationObserved.Should().BeTrue();
            var after = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
            var afterRoot = (await reader.GetGroupStateAsync(runId, after.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
            afterRoot.RunStatus.Should().Be(WorkflowStatus.Cancelled);
            afterRoot.Status.Should().Be(StepStatus.Cancelled);
            afterRoot.Work.Completed.Should().Be(3);
            afterRoot.Work.Cancelled.Should().Be(1);
            afterRoot.TotalCount.Should().BeNull();
            var afterSibling = (await reader.GetGroupStateAsync(runId, after.SnapshotToken, membership.GroupOccurrenceId, ct)).Value!.State;
            afterSibling.Status.Should().Be(StepStatus.Completed);
            afterSibling.Work.Completed.Should().Be(2);
            // A previously returned token remains one coherent captured boundary.
            var retained = (await reader.GetGroupStateAsync(runId, before.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
            retained.Should().BeEquivalentTo(beforeRoot);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Grouped_repeat_progress_counts_iterations_once_and_separates_control_steps()
    {
        const string source = """
            macro Echo(value: string) {
              activity work = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              checkpoint saved value work -> string;
              yield saved;
            }
            workflow demo(input: string) -> string {
              group root {
                repeat loop max 3 state current: string = input {
                  use call = Echo(value: current);
                } continue call break iter == 3 -> string;
                yield loop;
              }
              return root;
            }
            """;
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitGroupSource(source);
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-repeat-progress", Guid.NewGuid().ToString("N"));
        try
        {
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(new RecordingActivity(), new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("repeat-progress", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var runId = await engine.StartAsync("repeat-progress", "1", JsonSerializer.SerializeToElement("input"), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            using var reader = new WorkflowGroupReader(store);
            var groups = await ReadGroupOccurrences(reader, runId, ct);
            var header = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
            var root = groups.Single(group => group.ParentOccurrenceId is null);
            var state = (await reader.GetGroupStateAsync(runId, header.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
            state.Status.Should().Be(StepStatus.Completed);
            state.TotalCount.Should().Be(3);
            state.Work.Completed.Should().Be(3);
            state.Control.Completed.Should().BeGreaterThan(3);
            foreach (var invocation in groups.Where(group => group.GroupKey.EndsWith("/$group/call", StringComparison.Ordinal)))
            {
                var local = (await reader.GetGroupStateAsync(runId, header.SnapshotToken, invocation.OccurrenceId, ct)).Value!.State;
                local.Work.Completed.Should().Be(1);
                local.Control.Completed.Should().Be(1);
                local.TotalCount.Should().Be(1);
            }
        }
        finally { DeleteDirectory(directory); }
    }

    private sealed class ProgressBarrierActivity : IActivityExecutor
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var value = RuntimeValueJson.ToJsonElement(request.Arguments.Single().Value).GetString();
            if (value == "a" && request.Invocation.StructuralPath.EndsWith("/second", StringComparison.Ordinal))
            {
                Blocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return ActivityExecutionResult.Succeeded(request.Arguments.Single().Value);
        }
    }
}
