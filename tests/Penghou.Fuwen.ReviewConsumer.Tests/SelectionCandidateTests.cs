using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

// This project resolves archives only. No Fuwen or Zhinu source project reference.
public sealed class SelectionCandidateTests
{
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

    [Fact]
    public async Task Explicit_selection_replacement_preserves_history_and_same_value_reappears_with_fresh_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitSelection();
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


    private static FuwenZhinuProviderRuntimeIdentity IdentityFor(WorkflowAdmissionResult admission) =>
        new(admission.Receipt!.CatalogueSnapshotRevision, admission.Receipt.ResolvedDescriptorSetFingerprint);

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
