using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    private const string EchoMacro = """
        macro MACRO(value: string) {
          activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
          checkpoint saved value inner -> string;
          yield saved;
        }
        """;

    private static WorkflowAdmissionService GroupAdmissionService()
    {
        return new(new WorkflowCompiler(GroupCatalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
    }

    private static ITrustedCatalogue GroupCatalogue()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
                new ContentDigest("sha256", "descriptor/v1", new string('a', 64))),
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]);
    }

    private static async Task<GroupedWorkflowAdmissionResult> AdmitGroupSource(string source)
    {
        var compiled = await new FuwenSourceCompiler(GroupCatalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        compiled.GroupedDefinition.Should().NotBeNull();
        var admission = await GroupAdmissionService().AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Admission.Diagnostics.Select(d => d.Message)));
        return admission;
    }

    [Theory]
    [InlineData("SupervisedCoding")]
    [InlineData("TDD")]
    [InlineData("AlienProcess42")]
    public async Task Expanded_macros_persist_distinct_parented_occurrences_and_map_every_step(string macroName)
    {
        var source = EchoMacro.Replace("MACRO", macroName, StringComparison.Ordinal) + $$"""

            workflow demo(input: string) -> string {
              group stage {
                use first = {{macroName}}(value: input);
                use second = {{macroName}}(value: first);
                yield second;
              }
              return stage;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-groups", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var runId = await engine.StartAsync("grouped", "1", JsonSerializer.SerializeToElement("hello"), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken)).GetString().Should().Be("hello");
            activity.Requests.Should().HaveCount(2);
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            foreach (var step in steps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out _).Should().BeTrue(step.StepKey);
                var membership = await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken);
                if (step.StepKey == "demo/return_result") membership.Should().BeNull();
                else
                {
                    membership.Should().NotBeNull(step.StepKey);
                    membership!.Role.Should().Be(step.StepKey.EndsWith("/saved", StringComparison.Ordinal) ? ActivityGroupMemberRole.Control : ActivityGroupMemberRole.Work);
                }
            }
            using var reader = new WorkflowGroupReader(store);
            var snapshot = await ReadGroupOccurrences(reader, runId, TestContext.Current.CancellationToken);
            snapshot.Should().HaveCount(3);
            var parent = snapshot.Single(o => o.GroupKey == "demo/$group/stage");
            snapshot.Where(o => o.GroupKey != parent.GroupKey).Should().OnlyContain(o => o.ParentOccurrenceId == parent.OccurrenceId);
            snapshot.Select(o => o.OccurrenceId).Should().OnlyHaveUniqueItems();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Pinned_expansion_reopens_without_source_and_signal_wait_preserves_membership()
    {
        const string source = """
            workflow demo(input: string) -> string {
              group stage {
                activity done = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
                wait approval signal approve type string;
                yield approval;
              }
              return stage;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-groups", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        try
        {
            var activity = new RecordingActivity();
            var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
            var definitions = new DirectoryWorkflowDefinitionStore(root);
            var registration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(admission.Admission), ports)
                .CreateGroupedAsync("grouped", "1", admission, ct);
            var options = new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false };
            var store = new SqliteWorkflowStore(options);
            Guid runId;
            WorkflowStepRun before;
            ActivityGroupStepMembership membership;
            await using (var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry())))
            {
                runId = await engine.StartAsync("grouped", "1", JsonSerializer.SerializeToElement("hello"), cancellationToken: ct);
                using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var execution = engine.ExecuteAsync(runId, interrupted.Token);
                while (!(await store.GetStepsAsync(runId, ct)).Any(s => s.StepKey.EndsWith("/approval", StringComparison.Ordinal) && s.Status == StepStatus.Waiting))
                    await Task.Delay(20, ct);
                before = (await store.GetStepsAsync(runId, ct)).Single(s => s.StepKey.EndsWith("/approval", StringComparison.Ordinal));
                membership = (await store.GetStepGroupMembershipAsync(before.Id, ct))!;
                membership.Should().NotBeNull();
                await interrupted.CancelAsync();
                await execution;
            }
            // A fresh store and admission use only pinned bytes, never FuwenSourceCompiler.
            var reopenedDefinitions = new DirectoryWorkflowDefinitionStore(root);
            var pinned = await reopenedDefinitions.ReadGroupedAsync(admission.Definition.Fingerprint, ct);
            var readmission = await GroupAdmissionService().AdmitGroupedAsync(pinned!, cancellationToken: ct);
            var reopened = await new FuwenZhinuWorkflowFactory(reopenedDefinitions, IdentityFor(readmission.Admission), ports)
                .CreateGroupedAsync("grouped", "1", readmission, ct);
            var reopenedStore = new SqliteWorkflowStore(options);
            using (var reader = new WorkflowGroupReader(reopenedStore))
                (await ReadGroupOccurrences(reader, runId, ct)).Should().ContainSingle(o => o.OccurrenceId == membership.GroupOccurrenceId);
            await using var resumed = new WorkflowEngine(reopenedStore, reopened.Register(new WorkflowRegistry()));
            await resumed.SendSignalAsync(runId, "approve", "accepted", ct);
            await resumed.ExecuteAsync(runId, ct);
            (await resumed.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).GetString().Should().Be("accepted");
            var after = (await reopenedStore.GetStepsAsync(runId, ct)).Single(s => s.StepKey == before.StepKey);
            after.Id.Should().Be(before.Id);
            after.Revision.Should().Be(before.Revision);
            (await reopenedStore.GetStepGroupMembershipAsync(after.Id, ct)).Should().BeEquivalentTo(membership);
            activity.Requests.Should().ContainSingle("the completed provider step is replayed from durable output");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Process_loss_at_a_grouped_wait_preserves_history_without_repeating_committed_effects()
    {
        const string source = """
            macro Review(value: string) {
              activity done = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              wait approval signal approve type string;
              yield approval;
            }
            workflow demo(input: string) -> string { group stage { use review = Review(value: input); yield review; } return stage; }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-process", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        var ct = timeout.Token;
        System.Diagnostics.Process? worker = null;
        try
        {
            await new DirectoryWorkflowDefinitionStore(root).StoreGroupedAsync(admission.Definition, ct);
            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "start");
            var error = worker.StandardError.ReadToEndAsync(ct);
            while (!File.Exists(Path.Combine(root, "barrier")))
            {
                if (worker.HasExited) throw new InvalidOperationException("Worker exited before barrier: " + await error);
                await Task.Delay(20, ct);
            }
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(ct);
            worker.Dispose();
            worker = null;
            var runId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "run-id"), ct));
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            using var reader = new WorkflowGroupReader(store);
            // Inspect independently before starting a recovery worker.
            var before = await ReadGroupOccurrences(reader, runId, ct);
            before.Should().HaveCount(2);
            var steps = await store.GetStepsAsync(runId, ct);
            steps.Should().Contain(s => s.StepKey.EndsWith("/done", StringComparison.Ordinal) && s.Status == StepStatus.Completed);
            steps.Should().Contain(s => s.StepKey.EndsWith("/approval", StringComparison.Ordinal) && s.Status == StepStatus.Waiting);
            var memberships = new List<ActivityGroupStepMembership>();
            foreach (var step in steps) memberships.Add((await store.GetStepGroupMembershipAsync(step.Id, ct))!);
            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "resume");
            error = worker.StandardError.ReadToEndAsync(ct);
            await worker.WaitForExitAsync(ct);
            worker.ExitCode.Should().Be(0, await error);
            var after = await ReadGroupOccurrences(reader, runId, ct);
            after.Select(o => (o.OccurrenceId, o.ParentOccurrenceId)).Should()
                .BeEquivalentTo(before.Select(o => (o.OccurrenceId, o.ParentOccurrenceId)));
            foreach (var membership in memberships)
                (await store.GetStepGroupMembershipAsync(membership.StepId, ct)).Should().BeEquivalentTo(membership);
            (await File.ReadAllLinesAsync(Path.Combine(root, "effects"), ct)).Should().ContainSingle();
            (await File.ReadAllTextAsync(Path.Combine(root, "result"), ct)).Should().Be("accepted");
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(CancellationToken.None); }
                worker.Dispose();
            }
            DeleteDirectory(root);
        }
    }

    private static async Task<IReadOnlyList<WorkflowGroupOccurrenceFact>> ReadGroupOccurrences(WorkflowGroupReader reader, Guid runId, CancellationToken ct)
    {
        var header = await reader.GetRunGroupSnapshotAsync(runId, ct);
        header.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var result = new List<WorkflowGroupOccurrenceFact>();
        var parents = new Queue<string?>();
        parents.Enqueue(null);
        while (parents.TryDequeue(out var parent))
        {
            var children = await reader.QueryGroupChildrenAsync(runId, header.Value!.SnapshotToken, parent, cancellationToken: ct);
            children.Status.Should().Be(WorkflowGroupReadStatus.Success);
            children.Value!.HasMore.Should().BeFalse("this fixture has fewer than 100 immediate children");
            foreach (var child in children.Value.Items)
                if (child.Occurrence is { } occurrence) { result.Add(occurrence); parents.Enqueue(occurrence.OccurrenceId); }
        }
        result.Should().HaveCount(header.Value!.OccurrenceCount);
        return result;
    }

    private static System.Diagnostics.Process StartGroupWorker(string directory, string fingerprint, string mode)
    {
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET 8", StringComparison.Ordinal) ? "net8.0" : "net10.0";
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var assembly = Path.Combine(repository, "tests", "Penghou.Fuwen.GroupProcessProbe", "bin", configuration, framework, "Penghou.Fuwen.GroupProcessProbe.dll");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { assembly, directory, fingerprint, mode }) start.ArgumentList.Add(argument);
        return System.Diagnostics.Process.Start(start)!;
    }

    [Fact]
    public async Task Grouped_repeat_rejects_before_persistence_or_provider_calls()
    {
        const string source = """
            workflow demo(input: string) -> string {
              group stage {
                repeat loop max 2 state current: string = input {
                  activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: current;) -> string;
                } continue echo break iter == 1 -> string;
                yield loop;
              }
              return stage;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-groups", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            var action = async () => await factory.CreateGroupedAsync("grouped", "1", admission, TestContext.Current.CancellationToken);
            await action.Should().ThrowAsync<FuwenZhinuAdmissionException>().WithMessage("*static group profile*");
            Directory.EnumerateFiles(root).Should().BeEmpty();
            activity.Requests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_registration_rejects_a_store_without_companion_support_and_plain_registration_rejects_v4()
    {
        var admission = await AdmitGroupSource(EchoMacro.Replace("MACRO", "Echo", StringComparison.Ordinal) +
            "workflow demo(input: string) -> string { use call = Echo(value: input); return call; }");
        var factory = new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(), IdentityFor(admission.Admission));
        var unsupported = async () => await factory.CreateGroupedAsync("grouped", "1", admission, TestContext.Current.CancellationToken);
        await unsupported.Should().ThrowAsync<FuwenZhinuAdmissionException>().WithMessage("*grouped-definition store*");
        var missing = async () => await factory.CreateAsync("grouped", "1", admission.Admission, TestContext.Current.CancellationToken);
        await missing.Should().ThrowAsync<FuwenZhinuAdmissionException>().WithMessage("*hierarchy companion*");
    }
}
