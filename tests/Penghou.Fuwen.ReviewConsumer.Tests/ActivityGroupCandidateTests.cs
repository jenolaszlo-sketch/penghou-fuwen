using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Fuwen.Zhinu;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

public sealed class ActivityGroupCandidateTests
{
    private const string Source = """
        macro Echo(value: string) {
          activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
          checkpoint saved value inner -> string;
          yield saved;
        }
        workflow demo(input: string) -> string {
          group stage {
            use call = Echo(value: input);
            yield call;
          }
          return stage;
        }
        """;

    [Fact]
    public async Task Published_packages_compile_admit_register_execute_and_query_a_grouped_workflow()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var descriptor = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(descriptor,
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]);

        var compiler = new FuwenSourceCompiler(catalogue);
        var compiled = await compiler.CompileAsync(Source, cancellationToken: cancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(item => item.Message)));
        compiled.GroupedDefinition.Should().NotBeNull();

        var admissionService = new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
        var admission = await admissionService.AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: cancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Admission.Diagnostics.Select(item => item.Message)));

        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-package-consumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definitions = new DirectoryWorkflowDefinitionStore(Path.Combine(root, "definitions"));
            (await definitions.StoreGroupedAsync(admission.Definition, cancellationToken)).Should().Be(WorkflowDefinitionWriteDisposition.Created);
            var persisted = await definitions.ReadGroupedAsync(admission.Definition.Fingerprint, cancellationToken);
            persisted.Should().NotBeNull();
            persisted!.Fingerprint.Should().Be(admission.Definition.Fingerprint);
            var readmitted = await admissionService.AdmitGroupedAsync(persisted, cancellationToken: cancellationToken);
            readmitted.Succeeded.Should().BeTrue();

            var activity = new EchoActivity();
            var identity = new FuwenZhinuProviderRuntimeIdentity(
                readmitted.Admission.Receipt!.CatalogueSnapshotRevision,
                readmitted.Admission.Receipt.ResolvedDescriptorSetFingerprint);
            var registration = await new FuwenZhinuWorkflowFactory(definitions, identity,
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped", "1", readmitted, cancellationToken);

            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "run.db"),
                Pooling = false,
            });
            await using (var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry())))
            {
                var runId = await engine.StartAsync("grouped", "1", JsonSerializer.SerializeToElement("hello"), cancellationToken: cancellationToken);
                await engine.ExecuteAsync(runId, cancellationToken);
                var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cancellationToken);
                output.GetString().Should().Be("hello");
                ((JsonRuntimeValue)activity.Requests.Single().Arguments.Single().Value).Value.GetString().Should().Be("hello");

                var steps = await store.GetStepsAsync(runId, cancellationToken);
                var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
                var memberships = new List<(Guid StepId, string OccurrenceId)>();
                steps.Should().NotBeEmpty();
                foreach (var step in steps)
                {
                    FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                    match.Descriptor.Origin.Should().Be(FuwenZhinuStepOrigin.Declared, step.StepKey);
                    match.Scope.Should().BeEmpty(step.StepKey);
                    match.Descriptor.ExactStepKey.Should().Be(step.StepKey);

                    var membership = await store.GetStepGroupMembershipAsync(step.Id, cancellationToken);
                    if (step.StepKey == "demo/return_result")
                    {
                        membership.Should().BeNull();
                    }
                    else
                    {
                        membership.Should().NotBeNull(step.StepKey);
                        membership!.Role.Should().Be(step.StepKey.EndsWith("/saved", StringComparison.Ordinal)
                            ? ActivityGroupMemberRole.Control
                            : ActivityGroupMemberRole.Work);
                        memberships.Add((step.Id, membership.GroupOccurrenceId));
                    }
                }

                using var reader = new WorkflowGroupReader(store);
                var header = await reader.GetRunGroupSnapshotAsync(runId, cancellationToken);
                header.Status.Should().Be(WorkflowGroupReadStatus.Success);
                header.Value!.DefinitionCount.Should().Be(2);
                header.Value.OccurrenceCount.Should().Be(2);

                var rootDefinitions = await reader.QueryGroupDefinitionsAsync(runId, header.Value.SnapshotToken,
                    cancellationToken: cancellationToken);
                rootDefinitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
                var stageDefinition = rootDefinitions.Value!.Items.Should().ContainSingle().Which;
                stageDefinition.GroupKey.Should().Be("demo/$group/stage");
                stageDefinition.ParentGroupKey.Should().BeNull();
                var stageDefinitions = await reader.QueryGroupDefinitionsAsync(runId, header.Value.SnapshotToken,
                    stageDefinition.GroupKey, cancellationToken: cancellationToken);
                stageDefinitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
                var callDefinition = stageDefinitions.Value!.Items.Should().ContainSingle().Which;
                callDefinition.GroupKey.Should().Be("demo/$group/stage/$group/call");
                callDefinition.ParentGroupKey.Should().Be(stageDefinition.GroupKey);

                var roots = await reader.QueryGroupChildrenAsync(runId, header.Value.SnapshotToken,
                    cancellationToken: cancellationToken);
                roots.Status.Should().Be(WorkflowGroupReadStatus.Success);
                roots.Value!.HasMore.Should().BeFalse();
                var rootOccurrence = roots.Value.Items.Should().ContainSingle(node =>
                    node.Kind == WorkflowGroupNodeKind.Occurrence && node.Occurrence != null && node.Occurrence.GroupKey == "demo/$group/stage").Which.Occurrence!;
                roots.Value.Items.Should().ContainSingle(node =>
                    node.Kind == WorkflowGroupNodeKind.UngroupedStep && node.Step != null && node.Step.StepKey == "demo/return_result");

                var stageChildren = await reader.QueryGroupChildrenAsync(runId, header.Value.SnapshotToken,
                    rootOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                stageChildren.Status.Should().Be(WorkflowGroupReadStatus.Success);
                stageChildren.Value!.Items.Should().ContainSingle(node =>
                    node.Kind == WorkflowGroupNodeKind.Occurrence && node.Occurrence != null && node.Occurrence.GroupKey == "demo/$group/stage/$group/call");
                var callOccurrence = stageChildren.Value.Items.Single().Occurrence!;
                callOccurrence.ParentOccurrenceId.Should().Be(rootOccurrence.OccurrenceId);

                var stageMembers = await reader.QueryGroupMembersAsync(runId, header.Value.SnapshotToken,
                    rootOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                stageMembers.Status.Should().Be(WorkflowGroupReadStatus.Success);
                stageMembers.Value!.Items.Should().BeEmpty("membership is direct and must not flatten descendants");

                var callMembers = await reader.QueryGroupMembersAsync(runId, header.Value.SnapshotToken,
                    callOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                callMembers.Status.Should().Be(WorkflowGroupReadStatus.Success);
                callMembers.Value!.Items.Should().HaveCount(2);
                callMembers.Value.Items.Select(member => member.Step.StepKey).Should().BeEquivalentTo(
                    "demo/$group/stage/$group/call/inner", "demo/$group/stage/$group/call/saved");
                callMembers.Value.Items.Should().OnlyContain(member => member.Step.GroupOccurrenceId == callOccurrence.OccurrenceId);
                callMembers.Value.Items.Select(member => member.Step.StepId).Should().BeEquivalentTo(memberships.Select(item => item.StepId));
                memberships.Should().OnlyContain(membership => membership.OccurrenceId == callOccurrence.OccurrenceId);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Published_packages_preserve_grouped_repeat_occurrences_and_map_every_step()
    {
        const string repeatSource = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              checkpoint saved value inner -> string;
              yield saved;
            }
            workflow demo(input: string) -> string {
              group root {
                repeat loop max 2 state current: string = input {
                  group iteration {
                    use call = Echo(value: current);
                    yield call;
                  }
                } continue iteration break iter == 2 -> string;
                yield loop;
              }
              return root;
            }
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var descriptor = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(descriptor,
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]);
        var compiler = new FuwenSourceCompiler(catalogue);
        var compiled = await compiler.CompileAsync(repeatSource, cancellationToken: cancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(item => item.Message)));
        compiled.GroupedDefinition.Should().NotBeNull();

        var admissionService = new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
        var admission = await admissionService.AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: cancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Admission.Diagnostics.Select(item => item.Message)));

        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-repeat-package-consumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definitionsPath = Path.Combine(root, "definitions");
            var definitions = new DirectoryWorkflowDefinitionStore(definitionsPath);
            (await definitions.StoreGroupedAsync(admission.Definition, cancellationToken))
                .Should().Be(WorkflowDefinitionWriteDisposition.Created);
            var reopened = new DirectoryWorkflowDefinitionStore(definitionsPath);
            var persisted = await reopened.ReadGroupedAsync(admission.Definition.Fingerprint, cancellationToken);
            persisted.Should().NotBeNull();
            persisted!.Fingerprint.Should().Be(admission.Definition.Fingerprint);
            var readmitted = await admissionService.AdmitGroupedAsync(persisted, cancellationToken: cancellationToken);
            readmitted.Succeeded.Should().BeTrue();

            var activity = new EchoActivity();
            var identity = new FuwenZhinuProviderRuntimeIdentity(
                readmitted.Admission.Receipt!.CatalogueSnapshotRevision,
                readmitted.Admission.Receipt.ResolvedDescriptorSetFingerprint);
            var registration = await new FuwenZhinuWorkflowFactory(reopened, identity,
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-repeat", "1", readmitted, cancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "run.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var runId = await engine.StartAsync("grouped-repeat", "1", JsonSerializer.SerializeToElement("hello"),
                cancellationToken: cancellationToken);
            await engine.ExecuteAsync(runId, cancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: cancellationToken))
                .GetString().Should().Be("hello");
            activity.Requests.Should().HaveCount(2);

            var steps = await store.GetStepsAsync(runId, cancellationToken);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            steps.Should().NotBeEmpty();
            foreach (var step in steps)
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out _).Should().BeTrue(step.StepKey);

            using var reader = new WorkflowGroupReader(store);
            var header = await reader.GetRunGroupSnapshotAsync(runId, cancellationToken);
            header.Status.Should().Be(WorkflowGroupReadStatus.Success);
            header.Value!.OccurrenceCount.Should().Be(5);
            var roots = await reader.QueryGroupChildrenAsync(runId, header.Value.SnapshotToken,
                cancellationToken: cancellationToken);
            var rootOccurrence = roots.Value!.Items.Should().ContainSingle(item =>
                item.Kind == WorkflowGroupNodeKind.Occurrence && item.Occurrence != null && item.Occurrence.GroupKey == "demo/$group/root")
                .Which.Occurrence!;

            var rootChildren = await reader.QueryGroupChildrenAsync(runId, header.Value.SnapshotToken,
                rootOccurrence.OccurrenceId, cancellationToken: cancellationToken);
            var iterations = rootChildren.Value!.Items
                .Where(item => item.Kind == WorkflowGroupNodeKind.Occurrence &&
                    item.Occurrence?.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal) == true)
                .Select(item => item.Occurrence!).ToArray();
            iterations.Should().HaveCount(2);
            iterations.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();
            iterations.Should().OnlyContain(item => item.ParentOccurrenceId == rootOccurrence.OccurrenceId);

            var observedIterations = new HashSet<int>();
            foreach (var iterationOccurrence in iterations)
            {
                var iterationChildren = await reader.QueryGroupChildrenAsync(runId, header.Value.SnapshotToken,
                    iterationOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                var callOccurrence = iterationChildren.Value!.Items.Should().ContainSingle(item =>
                    item.Kind == WorkflowGroupNodeKind.Occurrence && item.Occurrence != null && item.Occurrence.GroupKey.EndsWith("/$group/call", StringComparison.Ordinal))
                    .Which.Occurrence!;
                callOccurrence.ParentOccurrenceId.Should().Be(iterationOccurrence.OccurrenceId);
                var callMembers = await reader.QueryGroupMembersAsync(runId, header.Value.SnapshotToken,
                    callOccurrence.OccurrenceId, cancellationToken: cancellationToken);
                callMembers.Value!.Items.Should().HaveCount(2);
                foreach (var member in callMembers.Value.Items)
                {
                    member.Step.GroupOccurrenceId.Should().Be(callOccurrence.OccurrenceId);
                    FuwenZhinuStepMapper.TryMatchStepKey(map, member.Step.StepKey, out var match).Should().BeTrue(member.Step.StepKey);
                    var loopScope = match.Scope.Should().ContainSingle(scope => scope.Kind == FuwenZhinuStepScopeKind.Loop).Which;
                    loopScope.Iteration.Should().NotBeNull();
                    loopScope.Iteration!.Value.Should().BeInRange(1, 2);
                    observedIterations.Add(loopScope.Iteration!.Value);
                }
            }
            observedIterations.Should().BeEquivalentTo([1, 2]);
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
        public List<ActivityExecutionRequest> Requests { get; } = [];

        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
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
