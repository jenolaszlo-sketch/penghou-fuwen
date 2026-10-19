using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

public sealed class GroupedFanOutCandidateTests
{
    private const string Source = """
        macro EchoTwice(value: string) {
          activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
          activity second = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: first;) -> string;
          yield second;
        }
        workflow demo(input: list<string>[2]) -> list<string>[2] {
          group root {
            fanout process over input as item: string key item max 2 concurrency 2 {
              group row {
                use pipeline = EchoTwice(value: item);
                yield pipeline;
              }
            } yield row -> list<string>[2];
            yield process;
          }
          return root;
        }
        """;

    [Fact]
    public async Task Candidate_packages_execute_grouped_fanout_after_bundle_reload_and_expose_native_lineage()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var cancellationToken = timeout.Token;
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var descriptor = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(descriptor,
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]);

        var compiled = await new FuwenSourceCompiler(catalogue).CompileAsync(Source, cancellationToken: cancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(item => item.Message)));
        compiled.GroupedDefinition.Should().NotBeNull();
        var admissionService = new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
        var admission = await admissionService.AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: cancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Admission.Diagnostics.Select(item => item.Message)));

        var root = Path.Combine(Path.GetTempPath(), "fuwen-grouped-fanout-package-consumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definitionsPath = Path.Combine(root, "definitions");
            var definitions = new DirectoryWorkflowDefinitionStore(definitionsPath);
            (await definitions.StoreGroupedAsync(admission.Definition, cancellationToken))
                .Should().Be(WorkflowDefinitionWriteDisposition.Created);
            var persisted = await new DirectoryWorkflowDefinitionStore(definitionsPath)
                .ReadGroupedAsync(admission.Definition.Fingerprint, cancellationToken);
            persisted.Should().NotBeNull();
            persisted!.Fingerprint.Should().Be(admission.Definition.Fingerprint);
            var readmitted = await admissionService.AdmitGroupedAsync(persisted, cancellationToken: cancellationToken);
            readmitted.Succeeded.Should().BeTrue();

            var activity = new EchoActivity();
            var identity = new FuwenZhinuProviderRuntimeIdentity(
                readmitted.Admission.Receipt!.CatalogueSnapshotRevision,
                readmitted.Admission.Receipt.ResolvedDescriptorSetFingerprint);
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(definitionsPath), identity,
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(),
                        observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
                .CreateGroupedAsync("grouped-fanout", "1", readmitted, cancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "run.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[\"a\",\"b\"]");
            var runId = await engine.StartAsync("grouped-fanout", "1", input.RootElement.Clone(), cancellationToken: cancellationToken);
            await engine.ExecuteAsync(runId, cancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cancellationToken))
                .EnumerateArray().Select(value => value.GetString()).Should().Equal("a", "b");
            activity.Requests.Should().HaveCount(4);

            var plan = registration.Definition.ReadPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var steps = await store.GetStepsAsync(runId, cancellationToken);
            steps.Should().NotBeEmpty();
            foreach (var step in steps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                if (match.Descriptor.ExactStepKey is not null || match.Descriptor.BodyStepName is not null || match.Descriptor.FanOutNodePath is not null)
                    FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
            }

            using var reader = new WorkflowGroupReader(store);
            var snapshot = await reader.GetRunGroupSnapshotAsync(runId, cancellationToken);
            snapshot.Status.Should().Be(WorkflowGroupReadStatus.Success);
            snapshot.Value!.DefinitionCount.Should().Be(3);
            snapshot.Value.OccurrenceCount.Should().Be(5);
            var rootDefinitions = await reader.QueryGroupDefinitionsAsync(runId, snapshot.Value.SnapshotToken,
                cancellationToken: cancellationToken);
            rootDefinitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
            rootDefinitions.Value!.Items.Should().ContainSingle().Which.GroupKey.Should().Be("demo/$group/root");
            var childDefinitions = await reader.QueryGroupDefinitionsAsync(runId, snapshot.Value.SnapshotToken,
                "demo/$group/root", cancellationToken: cancellationToken);
            childDefinitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
            var rowDefinition = childDefinitions.Value!.Items.Should().ContainSingle(item =>
                item.GroupKey.EndsWith("/$group/row", StringComparison.Ordinal)).Which;
            rowDefinition.ParentGroupKey.Should().Be("demo/$group/root");

            var rootNodes = await reader.QueryGroupChildrenAsync(runId, snapshot.Value.SnapshotToken,
                cancellationToken: cancellationToken);
            rootNodes.Status.Should().Be(WorkflowGroupReadStatus.Success);
            var rootOccurrence = rootNodes.Value!.Items.Should().ContainSingle(node =>
                node.Kind == WorkflowGroupNodeKind.Occurrence && node.Occurrence != null && node.Occurrence.GroupKey == "demo/$group/root")
                .Which.Occurrence!;
            var rootChildren = await reader.QueryGroupChildrenAsync(runId, snapshot.Value.SnapshotToken,
                rootOccurrence.OccurrenceId, cancellationToken: cancellationToken);
            var itemOccurrences = rootChildren.Value!.Items
                .Where(node => node.Kind == WorkflowGroupNodeKind.Occurrence &&
                    node.Occurrence != null && node.Occurrence.GroupKey == "demo/$group/root/process/$body/$group/row")
                .Select(node => node.Occurrence!).ToArray();
            itemOccurrences.Should().HaveCount(2);
            itemOccurrences.Should().OnlyContain(item => item.ParentOccurrenceId == rootOccurrence.OccurrenceId);
            itemOccurrences.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();

            var itemControlMembers = new List<(Guid StepId, string StepKey, string GroupOccurrenceId)>();
            var workMembers = new List<(Guid StepId, string StepKey, string GroupOccurrenceId)>();
            var macroOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var itemOccurrence in itemOccurrences)
            {
                var childDefinitionsForItem = await reader.QueryGroupDefinitionsAsync(runId, snapshot.Value.SnapshotToken,
                    itemOccurrence.GroupKey, cancellationToken: cancellationToken);
                childDefinitionsForItem.Status.Should().Be(WorkflowGroupReadStatus.Success);
                var macroDefinition = childDefinitionsForItem.Value!.Items.Should().ContainSingle(item =>
                    item.GroupKey.EndsWith("/$group/pipeline", StringComparison.Ordinal)).Which;
                macroDefinition.ParentGroupKey.Should().Be(itemOccurrence.GroupKey);
                var macroKey = macroDefinition.GroupKey;

                var children = await reader.QueryGroupChildrenAsync(runId, snapshot.Value.SnapshotToken,
                    itemOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                var macroOccurrence = children.Value!.Items.Should().ContainSingle(node =>
                    node.Kind == WorkflowGroupNodeKind.Occurrence && node.Occurrence != null && node.Occurrence.GroupKey == macroKey)
                    .Which.Occurrence!;
                macroOccurrence.ParentOccurrenceId.Should().Be(itemOccurrence.OccurrenceId);
                macroOccurrenceIds.Add(macroOccurrence.OccurrenceId);

                var directItemMembers = await reader.QueryGroupMembersAsync(runId, snapshot.Value.SnapshotToken,
                    itemOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                directItemMembers.Status.Should().Be(WorkflowGroupReadStatus.Success);
                itemControlMembers.AddRange(directItemMembers.Value!.Items.Where(member => member.Step.Role == ActivityGroupMemberRole.Control)
                    .Select(member => (member.Step.StepId, member.Step.StepKey, member.Step.GroupOccurrenceId!)));
                directItemMembers.Value.Items.Where(member => member.Step.Role == ActivityGroupMemberRole.Work).Should().BeEmpty();

                var directMacroMembers = await reader.QueryGroupMembersAsync(runId, snapshot.Value.SnapshotToken,
                    macroOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                directMacroMembers.Status.Should().Be(WorkflowGroupReadStatus.Success);
                directMacroMembers.Value!.Items.Should().HaveCount(2).And.OnlyContain(member => member.Step.Role == ActivityGroupMemberRole.Work);
                workMembers.AddRange(directMacroMembers.Value.Items.Select(member =>
                    (member.Step.StepId, member.Step.StepKey, member.Step.GroupOccurrenceId!)));
            }
            itemControlMembers.Should().HaveCount(2);
            itemControlMembers.Select(member =>
                FuwenZhinuStepMapper.TryMatchStepKey(map, member.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.FanOutItemCompletion).Should().OnlyContain(matched => matched);
            workMembers.Should().HaveCount(4);
            workMembers.Select(member => member.StepId).Should().OnlyHaveUniqueItems();
            workMembers.Select(member => member.GroupOccurrenceId).Should().OnlyContain(id => macroOccurrenceIds.Contains(id));
            workMembers.Select(member => member.StepKey).Select(key =>
                FuwenZhinuStepMapper.TryMatchStepKey(map, key, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem)).Should().OnlyContain(matched => matched);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class EchoActivity : IActivityExecutor
    {
        private readonly ConcurrentQueue<ActivityExecutionRequest> requests = new();
        public IReadOnlyCollection<ActivityExecutionRequest> Requests => requests.ToArray();

        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            requests.Enqueue(request);
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(request.Arguments.Single().Value));
        }
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context provider should not be called.");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Inference executor should not be called.");
    }
}
