using FluentAssertions;
using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    private const string SelectionSource = """
        workflow demo(input: list<string>[2]) -> list<string>[2] {
          group root {
            fanout process over input selection epoch 1 replaces 0 max 3 as item: string key item max 2 concurrency 1 {
              group row {
                activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                yield echo;
              }
            } yield row -> list<string>[2];
            yield process;
          }
          return root;
        }
        """;

    [Fact]
    public async Task Explicit_selection_compiles_to_a_distinct_immutable_contract()
    {
        var admission = await AdmitGroupSource(SelectionSource);
        admission.Succeeded.Should().BeTrue();
        var plan = admission.Definition!.Definition.ReadPlan();
        plan.IrVersion.Should().Be(FuwenContracts.RunScopedDynamicsIrVersion);
        plan.UsesExpandedGroupScopes.Should().BeTrue();
        plan.Nodes.OfType<FanOutNode>().Single().Selection!.MaximumEpochs.Should().Be(3);
        plan = WorkflowDefinitionDocument.Create(plan).ReadPlan();
        plan.Nodes.OfType<FanOutNode>().Single().Selection!.MaximumEpochs.Should().Be(3);
    }

    private const string ChangingSelectionSource = """
        schema Choice "sample.choice@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" {
          epoch: int;
          previous: int;
          items: list<string>[2];
        }
        workflow demo(input: string) -> list<string>[2] {
          group root {
            activity decide = activity "sample.select@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" (value: input;) -> Choice;
            fanout process over decide.items selection epoch decide.epoch replaces decide.previous max 4 as item: string key item max 2 concurrency 1 {
              group row {
                group nested {
                  activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                  activity second = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: first;) -> string;
                  yield second;
                }
                yield nested;
              }
            } yield row -> list<string>[2];
            yield process;
          }
          return root;
        }
        """;

    private static ITrustedCatalogue SelectionCatalogue()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var choice = new NamedTypeReference(new DescriptorReference(DescriptorKind.Schema, "sample.choice", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('c', 64))));
        return new InMemoryTrustedCatalogue(new[] { ("sample.echo", 'a', (FuwenType)text), ("sample.select", 'b', (FuwenType)choice) }
            .Select(value => new TrustedCatalogueDescriptor(new DescriptorReference(DescriptorKind.Activity, value.Item1, "1",
                new ContentDigest("sha256", "descriptor/v1", new string(value.Item2, 64))),
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], value.Item3),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))).Append(
                new TrustedCatalogueDescriptor(choice.Schema, schemaDefinition: new ObjectSchemaDefinition(choice.Schema,
                    [new SchemaField("epoch", new PrimitiveType(FuwenPrimitiveKind.Integer)),
                     new SchemaField("previous", new PrimitiveType(FuwenPrimitiveKind.Integer)),
                     new SchemaField("items", new ListType(text, 2))]))));
    }

    private static async Task<GroupedWorkflowAdmissionResult> AdmitSelection(string source = ChangingSelectionSource)
    {
        var compiled = await new FuwenSourceCompiler(SelectionCatalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(SelectionCatalogue(),
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", []))).AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Admission.Diagnostics.Select(d => d.Message)));
        return admission;
    }

    private sealed class SelectionActivity(int epoch, int previous, params string[] items) : IActivityExecutor
    {
        public List<ActivityExecutionRequest> Requests { get; } = [];
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(request.Activity.Name == "sample.select"
                ? RuntimeValue.FromJson(JsonSerializer.SerializeToElement(new { epoch, previous, items }))
                : request.Arguments.Single().Value));
        }
    }

    [Theory]
    [InlineData(0, 0, "a")]
    [InlineData(5, 1, "a")]
    [InlineData(2, 0, "a")]
    [InlineData(1, 1, "a")]
    [InlineData(2, -1, "a")]
    [InlineData(1, 0, "a", "a")]
    public async Task Explicit_selection_invalid_epochs_and_duplicate_keys_dispatch_no_body_work(int epoch, int previous, params string[] items)
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitSelection();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-selection-invalid", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new SelectionActivity(epoch, previous, items);
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("selection", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var id = await engine.StartAsync("selection", "1", JsonSerializer.SerializeToElement("input"), cancellationToken: ct);
            await engine.ExecuteAsync(id, ct);
            (await store.GetRunAsync(id, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            activity.Requests.Should().ContainSingle(request => request.Activity.Name == "sample.select");
            (await store.ReadGroupSnapshotAsync(id, new(), ct)).Value!.Expansions.Should().BeEmpty();
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_selection_unqualified_shapes_and_epoch_budgets_reject_before_storage(bool downstream)
    {
        var source = downstream
            ? ChangingSelectionSource.Replace("yield process;", "checkpoint extra value process -> list<string>[2]; yield extra;", StringComparison.Ordinal)
            : ChangingSelectionSource.Replace("max 4 as", "max 1024 as", StringComparison.Ordinal);
        var admission = await AdmitSelection(source);
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-selection-preflight", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new SelectionActivity(1, 0, "a");
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            var action = async () => await factory.CreateGroupedAsync("selection", "1", admission, TestContext.Current.CancellationToken);
            await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
            Directory.EnumerateFiles(directory).Should().BeEmpty();
            activity.Requests.Should().BeEmpty();
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public async Task Explicit_selection_reused_or_missing_predecessor_epochs_cannot_dispatch_replacement_work(int epoch, int previous)
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitSelection();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-selection-history-fence", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            Guid id;
            var original = new SelectionActivity(1, 0, "a");
            var first = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(original, new UnusedContext(), new UnusedInference())).CreateGroupedAsync("selection", "1", admission, ct);
            await using (var engine = new WorkflowEngine(store, first.Register(new WorkflowRegistry())))
            {
                id = await engine.StartAsync("selection", "1", JsonSerializer.SerializeToElement("input"), cancellationToken: ct);
                await engine.ExecuteAsync(id, ct);
                await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct);
                await engine.RestartStepAsync(id, "demo/$group/root/decide", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
                await engine.RestartStepAsync(id, "demo/$group/root/process/$selection", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
            }
            var replacement = new SelectionActivity(epoch, previous, "b");
            var next = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(replacement, new UnusedContext(), new UnusedInference())).CreateGroupedAsync("selection", "1", admission, ct);
            await using (var engine = new WorkflowEngine(store, next.Register(new WorkflowRegistry())))
                await engine.ExecuteAsync(id, ct);
            (await store.GetRunAsync(id, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            replacement.Requests.Should().ContainSingle(request => request.Activity.Name == "sample.select");
            (await store.GetStepsAsync(id, ct)).Where(step => step.StepKey.Contains(":body:", StringComparison.Ordinal))
                .Should().HaveCount(2).And.OnlyContain(step => step.Status == StepStatus.Completed);
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_selection_replacement_preserves_history_and_same_value_reappears_with_fresh_identity(bool legacyV5)
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitSelection();
        if (legacyV5)
        {
            var plan = admission.Definition!.Definition.ReadPlan() with { IrVersion = FuwenContracts.SelectionIrVersion, ExpandedGroupScopes = false };
            var bundle = GroupedWorkflowDefinitionDocument.Create(WorkflowDefinitionDocument.Create(plan), admission.Definition.Groups);
            admission = await new WorkflowAdmissionService(new WorkflowCompiler(SelectionCatalogue(),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", []))).AdmitGroupedAsync(bundle, cancellationToken: ct);
            admission.Succeeded.Should().BeTrue();
        }
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-selection", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            Guid runId = default;
            WorkflowGroupSnapshotData? original = null;
            var invocationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var epoch in new[] { 1, 2, 3, 4 })
            {
                var items = epoch switch { 1 => new[] { "a", "b" }, 2 => ["b"], 3 => ["b", "a"], _ => Array.Empty<string>() };
                var activity = new SelectionActivity(epoch, epoch - 1, items);
                var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                    .CreateGroupedAsync("selection", "1", admission, ct);
                await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
                if (epoch == 1)
                    runId = await engine.StartAsync("selection", "1", JsonSerializer.SerializeToElement("input"), cancellationToken: ct);
                else
                {
                    await engine.RestartStepAsync(runId, "demo/$group/root/decide", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
                    await engine.RestartStepAsync(runId, "demo/$group/root/process/$selection", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
                }
                await engine.ExecuteAsync(runId, ct);
                (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct)).EnumerateArray().Select(value => value.GetString()).Should().Equal(items);
                activity.Requests.Should().HaveCount(1 + items.Length * 2);
                foreach (var request in activity.Requests.Where(request => request.Activity.Name == "sample.echo"))
                    invocationIds.Add(request.Invocation.RuntimePath).Should().BeTrue();
                var snapshot = (await store.ReadGroupSnapshotAsync(runId, new(), ct)).Value!;
                snapshot.Expansions.Should().HaveCount(epoch);
                snapshot.Expansions.Count(value => value.IsCurrent).Should().Be(1);
                if (original is not null)
                {
                    foreach (var step in original.Steps.Where(step => step.Role == ActivityGroupMemberRole.Work && step.StepKey != "demo/$group/root/decide"))
                        snapshot.Steps.Single(value => value.StepId == step.StepId).Should().Be(step);
                    foreach (var occurrence in original.Occurrences)
                        snapshot.Occurrences.Single(value => value.OccurrenceId == occurrence.OccurrenceId).Should().Be(occurrence);
                }
                original ??= snapshot;
                var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
                map.SchemaVersion.Should().Be(FuwenZhinuStepMapper.SelectionSchemaVersion);
                foreach (var step in await store.GetStepsAsync(runId, ct))
                {
                    FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                    FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
                }
                using var reader = new WorkflowGroupReader(store);
                var header = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
                var root = snapshot.Occurrences.Single(value => value.GroupKey == "demo/$group/root");
                var state = (await reader.GetGroupStateAsync(runId, header.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
                state.Status.Should().Be(StepStatus.Completed);
                state.Work.Known.Should().Be(1 + items.Length * 2);
                state.Work.Completed.Should().Be(state.Work.Known);
            }
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task Explicit_selection_process_loss_retires_partial_history_and_recovers_durable_replacement(string blockedItem)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(70));
        var ct = timeout.Token;
        var admission = await AdmitSelection();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-selection-process", Guid.NewGuid().ToString("N"));
        var preserve = Environment.GetEnvironmentVariable("ZG_SQLITE_STRESS") == "1";
        System.Diagnostics.Process? worker = null;
        Task<string>? workerError = null;
        void Diagnostic(string message)
        {
            if (preserve) File.AppendAllText(Path.Combine(directory, "stress-diagnostics.log"), $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}");
        }
        try
        {
            var definitions = new DirectoryWorkflowDefinitionStore(directory);
            var registration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(new SelectionActivity(2, 1, "c"), new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("group-process", "1", admission, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "choice.json"), "{\"epoch\":1,\"previous\":0,\"items\":[\"a\",\"b\"]}", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "block-item"), blockedItem, ct);

            async Task ReachBarrier(string mode)
            {
                worker = StartGroupWorker(directory, admission.Definition!.Fingerprint, mode);
                workerError = worker.StandardError.ReadToEndAsync(CancellationToken.None);
                Diagnostic($"started mode={mode} pid={worker.Id}");
                var error = workerError;
                while (!File.Exists(Path.Combine(directory, "barrier")))
                {
                    if (worker.HasExited) throw new InvalidOperationException(await error);
                    await Task.Delay(20, ct);
                }
            }
            async Task KillWorker()
            {
                worker!.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(ct);
                Diagnostic($"terminated pid={worker.Id} exit={worker.ExitCode} stderr={await workerError!}");
                worker.Dispose(); worker = null;
                File.Delete(Path.Combine(directory, "barrier"));
                File.Delete(Path.Combine(directory, "fanout-second-entered"));
            }

            await ReachBarrier("selection-start");
            var runId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run-id"), ct));
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            var before = (await store.ReadGroupSnapshotAsync(runId, new(), ct)).Value!;
            var oldCompleted = before.Steps.Where(value => value.Status == StepStatus.Completed &&
                value.Role == ActivityGroupMemberRole.Work && value.StepKey != "demo/$group/root/decide").ToArray();
            oldCompleted.Should().HaveCount(blockedItem == "a" ? 1 : 3);
            await KillWorker();
            await using (var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry())))
            {
                await engine.RestartStepAsync(runId, "demo/$group/root/decide", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
                await engine.RestartStepAsync(runId, "demo/$group/root/process/$selection", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "choice.json"), "{\"epoch\":2,\"previous\":1,\"items\":[\"c\"]}", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "block-item"), "c", ct);
            await ReachBarrier("selection-partial");
            var partial = (await store.ReadGroupSnapshotAsync(runId, new(), ct)).Value!;
            partial.Expansions.Should().HaveCount(2);
            partial.Expansions.Single(value => value.ExpansionId == before.Expansions.Single().ExpansionId).ReplacedByExpansionId.Should().NotBeNull();
            foreach (var step in oldCompleted)
                partial.Steps.Single(value => value.StepId == step.StepId).Should().Be(step);
            foreach (var step in before.Steps.Where(value => value.Status == StepStatus.Running))
                partial.Steps.Single(value => value.StepId == step.StepId).Status.Should().Be(StepStatus.Cancelled);
            await KillWorker();

            // Neither selector nor selected membership may be recomputed on recovery.
            await File.WriteAllTextAsync(Path.Combine(directory, "choice.json"), "invalid selector result", ct);
            worker = StartGroupWorker(directory, admission.Definition!.Fingerprint, "selection-resume");
            workerError = worker.StandardError.ReadToEndAsync(CancellationToken.None);
            var recoveryError = workerError;
            await worker.WaitForExitAsync(ct);
            worker.ExitCode.Should().Be(0, await recoveryError);
            worker.Dispose(); worker = null;
            (await File.ReadAllTextAsync(Path.Combine(directory, "result"), ct)).Should().Be("[\"c\"]");
            (await File.ReadAllLinesAsync(Path.Combine(directory, "selector-calls"), ct)).Should().HaveCount(2);
            var effects = await File.ReadAllLinesAsync(Path.Combine(directory, "effects"), ct);
            effects.Should().HaveCount(oldCompleted.Length + 2);
            effects.Distinct(StringComparer.Ordinal).Should().HaveCount(effects.Length);
            var after = (await store.ReadGroupSnapshotAsync(runId, new(), ct)).Value!;
            foreach (var occurrence in partial.Occurrences)
                after.Occurrences.Single(value => value.OccurrenceId == occurrence.OccurrenceId).Should().Be(occurrence);
            foreach (var step in partial.Steps.Where(value => value.Status == StepStatus.Completed && value.Role == ActivityGroupMemberRole.Work))
                after.Steps.Single(value => value.StepId == step.StepId).Should().Be(step);
            using var reader = new WorkflowGroupReader(store);
            var header = (await reader.GetRunGroupSnapshotAsync(runId, ct)).Value!;
            var root = after.Occurrences.Single(value => value.GroupKey == "demo/$group/root");
            var state = (await reader.GetGroupStateAsync(runId, header.SnapshotToken, root.OccurrenceId, ct)).Value!.State;
            state.Status.Should().Be(StepStatus.Completed);
            state.Work.Known.Should().Be(3);
            state.Work.Completed.Should().Be(3);
        }
        catch (Exception exception)
        {
            Diagnostic(exception.ToString());
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is Microsoft.Data.Sqlite.SqliteException sqlite)
                    Diagnostic($"sqlite primary={sqlite.SqliteErrorCode} extended={sqlite.SqliteExtendedErrorCode}");
            }
            throw;
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(CancellationToken.None); }
                Diagnostic($"cleanup pid={worker.Id} exit={worker.ExitCode} stderr={(workerError is null ? "" : await workerError)}");
                worker.Dispose();
            }
            if (preserve)
            {
                // Retain original files untouched after all workers exit. Integrity checks run on copies.
                Diagnostic($"retained runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} pooling=false directory={directory}");
                var files = Directory.GetFiles(directory).Select(path => new
                {
                    name = Path.GetFileName(path),
                    size = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                }).ToArray();
                File.WriteAllText(Path.Combine(directory, "retained-files.json"), System.Text.Json.JsonSerializer.Serialize(files));
            }
            else DeleteDirectory(directory);
        }
    }
}
