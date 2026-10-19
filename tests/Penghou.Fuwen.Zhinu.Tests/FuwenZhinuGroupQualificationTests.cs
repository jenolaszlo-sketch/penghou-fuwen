using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    private const string EchoDescriptor = "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Macro_vocabulary_changes_executable_work_not_runtime_identity_rules()
    {
        var cases = new (string Name, string Body, string Input, int Calls)[]
        {
            ("SupervisedCoding", $$"""
                activity inspect = activity "{{EchoDescriptor}}" (value: value;) -> string;
                checkpoint plan value inspect -> string;
                activity implement = activity "{{EchoDescriptor}}" (value: plan;) -> string;
                checkpoint candidate value implement -> string;
                activity verify = activity "{{EchoDescriptor}}" (value: candidate;) -> string;
                yield verify;
                """, "input", 3),
            ("TDD", $$"""
                repeat cycle max 2 state current: string = value {
                  group iteration {
                    activity red = activity "{{EchoDescriptor}}" (value: current;) -> string;
                    checkpoint failingTest value red -> string;
                    activity green = activity "{{EchoDescriptor}}" (value: failingTest;) -> string;
                    checkpoint implementation value green -> string;
                    yield implementation;
                  }
                } continue iteration break iter == 2 -> string;
                yield cycle;
                """, "input", 4),
            ("BDDImplementation", $$"""
                activity scenarioGiven = activity "{{EchoDescriptor}}" (value: value;) -> string;
                if decide value == "go" {
                  activity eventWhen = activity "{{EchoDescriptor}}" (value: value;) -> string;
                } else {
                  activity alternateWhen = activity "{{EchoDescriptor}}" (value: value;) -> string;
                  activity eventEvidence = activity "{{EchoDescriptor}}" (value: alternateWhen;) -> string;
                }
                checkpoint outcomeThen value scenarioGiven -> string;
                yield outcomeThen;
                """, "go", 2),
            ("BDDImplementation", $$"""
                activity scenarioGiven = activity "{{EchoDescriptor}}" (value: value;) -> string;
                if decide value == "go" {
                  activity eventWhen = activity "{{EchoDescriptor}}" (value: value;) -> string;
                } else {
                  activity alternateWhen = activity "{{EchoDescriptor}}" (value: value;) -> string;
                  activity eventEvidence = activity "{{EchoDescriptor}}" (value: alternateWhen;) -> string;
                }
                checkpoint outcomeThen value scenarioGiven -> string;
                yield outcomeThen;
                """, "else", 3),
            // Deliberately arbitrary: no runtime behavior is selected by this identifier.
            ("AlienProcess42", $$"""
                activity decode = activity "{{EchoDescriptor}}" (value: value;) -> string;
                checkpoint decoded value decode -> string;
                yield decoded;
                """, "input", 1)
        };

        foreach (var item in cases)
        {
            var source = $$"""
                macro {{item.Name}}(value: string) {
                  {{item.Body}}
                }
                workflow demo(input: string) -> string {
                  group stage {
                    use result = {{item.Name}}(value: input);
                    yield result;
                  }
                  return stage;
                }
                """;
            var compiled = await new FuwenSourceCompiler(GroupCatalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
            compiled.Succeeded.Should().BeTrue(item.Name + ": " + string.Join("; ", compiled.Diagnostics.Select(diagnostic =>
                diagnostic.Code + " " + diagnostic.Message + " " + diagnostic.Actual)));
            compiled.GroupedDefinition.Should().NotBeNull(item.Name);
            var admission = await GroupAdmissionService().AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: TestContext.Current.CancellationToken);
            admission.Succeeded.Should().BeTrue(item.Name + ": " + string.Join("; ", admission.Admission.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var root = Path.Combine(Path.GetTempPath(), "fuwen-group-vocabulary", Guid.NewGuid().ToString("N"));
            try
            {
                var activity = new RecordingActivity();
                var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                    .CreateGroupedAsync("qualification-" + item.Name, "1", admission, TestContext.Current.CancellationToken);
                var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
                await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
                var run = await engine.StartAsync("qualification-" + item.Name, "1", JsonSerializer.SerializeToElement(item.Input),
                    cancellationToken: TestContext.Current.CancellationToken);
                await engine.ExecuteAsync(run, TestContext.Current.CancellationToken);
                var result = await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: TestContext.Current.CancellationToken);
                result.GetString().Should().Be(item.Input);

                activity.Requests.Should().HaveCount(item.Calls);
                var macro = admission.Definition.Groups.Groups.Should().ContainSingle(group => group.Kind == WorkflowGroupKind.Macro).Which;
                macro.Provenance.Should().NotBeNull();
                macro.Provenance!.MacroName.Should().Be(item.Name);
                macro.Provenance.DefinitionFingerprint.Should().NotBeNullOrWhiteSpace();
                macro.Provenance.InvocationKey.Should().Be("demo/$group/stage/$group/result");
                var occurrences = await ReadGroupOccurrences(new WorkflowGroupReader(store), run, TestContext.Current.CancellationToken);
                macro.ParentGroupKey.Should().Be("demo/$group/stage");
                occurrences.Should().Contain(group => group.GroupKey == macro.ParentGroupKey);
                var stageOccurrence = occurrences.Single(group => group.GroupKey == macro.ParentGroupKey).OccurrenceId;
                occurrences.Single(group => group.GroupKey == macro.GroupKey).ParentOccurrenceId.Should().Be(stageOccurrence);
                if (item.Name == "TDD")
                {
                    var iterationDefinition = admission.Definition.Groups.Groups.Single(group => group.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal));
                    var iterationOccurrences = occurrences.Where(group => group.GroupKey == iterationDefinition.GroupKey).ToArray();
                    iterationOccurrences.Should().HaveCount(2);
                    iterationOccurrences.Should().OnlyContain(group => group.ParentOccurrenceId == occurrences.Single(parent => parent.GroupKey == macro.GroupKey).OccurrenceId);
                }

                var plan = registration.Definition.ReadPlan();
                var returnPath = plan.Nodes.OfType<ReturnNode>().Single().StructuralPath;
                var map = FuwenZhinuStepMapper.Map(plan);
                foreach (var step in await store.GetStepsAsync(run, TestContext.Current.CancellationToken))
                {
                    FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out _).Should().BeTrue(step.StepKey);
                    if (step.StepKey == returnPath)
                        (await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken)).Should().BeNull();
                    else
                    {
                        var membership = await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken);
                        membership.Should().NotBeNull(step.StepKey);
                        occurrences.Should().Contain(group => group.OccurrenceId == membership!.GroupOccurrenceId);
                    }
                }
            }
            finally { DeleteDirectory(root); }
        }
    }

    [Fact]
    public async Task Domain_approval_is_a_real_wait_boundary_inside_a_macro_group()
    {
        const string source = $$"""
            macro DomainApproval(value: string) {
              activity prepare = activity "{{EchoDescriptor}}" (value: value;) -> string;
              wait approval signal approve type string;
              checkpoint accepted value approval -> string;
              yield accepted;
            }
            workflow demo(input: string) -> string {
              group approvalCase {
                use review = DomainApproval(value: input);
                yield review;
              }
              return approvalCase;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-domain-approval", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("domain-approval", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var run = await engine.StartAsync("domain-approval", "1", JsonSerializer.SerializeToElement("proposal"),
                cancellationToken: TestContext.Current.CancellationToken);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var execution = engine.ExecuteAsync(run, interruption.Token);
            WorkflowStepRun waiting;
            while (true)
            {
                var candidate = (await store.GetStepsAsync(run, TestContext.Current.CancellationToken)).SingleOrDefault(step => step.Status == StepStatus.Waiting);
                if (candidate is not null) { waiting = candidate; break; }
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            var originalMembership = await store.GetStepGroupMembershipAsync(waiting.Id, TestContext.Current.CancellationToken);
            originalMembership.Should().NotBeNull();
            activity.Requests.Should().ContainSingle();
            await engine.SendSignalAsync(run, "approve", "approved", TestContext.Current.CancellationToken);
            await execution;
            var output = await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: TestContext.Current.CancellationToken);
            output.GetString().Should().Be("approved");
            (await store.GetStepGroupMembershipAsync(waiting.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(originalMembership);
            var occurrences = await ReadGroupOccurrences(new WorkflowGroupReader(store), run, TestContext.Current.CancellationToken);
            occurrences.Should().HaveCount(2);
            occurrences.Should().Contain(group => group.GroupKey == "demo/$group/approvalCase");
            occurrences.Should().Contain(group => group.GroupKey == "demo/$group/approvalCase/$group/review");
        }
        finally { DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Re_registration_rejects_changed_execution_or_manifest_pins_without_mutating_parked_hierarchy(bool changeManifestOnly)
    {
        const string source = $$"""
            macro Review(value: string) {
              activity prepare = activity "{{EchoDescriptor}}" (value: value;) -> string;
              wait approval signal approve type string;
              checkpoint accepted value approval -> string;
              yield accepted;
            }
            workflow demo(input: string) -> string {
              group stage { use review = Review(value: input); yield review; }
              return stage;
            }
            """;
        var original = await AdmitGroupSource(source);
        var originalBundleBytes = original.Definition.CanonicalBytes.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-pins", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var ports = new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference());
            var definitions = new DirectoryWorkflowDefinitionStore(root);
            var originalRegistration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(original.Admission), ports)
                .CreateGroupedAsync("group-pin", "1", original, TestContext.Current.CancellationToken);
            var database = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            Guid run;
            WorkflowGroupOccurrenceFact[] originalOccurrences;
            ActivityGroupStepMembership[] originalMemberships;
            WorkflowStatus parkedStatus;
            await using (var engine = new WorkflowEngine(database, originalRegistration.Register(new WorkflowRegistry())))
            {
                run = await engine.StartAsync("group-pin", "1", JsonSerializer.SerializeToElement("proposal"),
                    cancellationToken: TestContext.Current.CancellationToken);
                using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                var execution = engine.ExecuteAsync(run, interruption.Token);
                while (!(await database.GetStepsAsync(run, TestContext.Current.CancellationToken)).Any(step => step.Status == StepStatus.Waiting))
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                await interruption.CancelAsync();
                try { await execution; }
                catch (OperationCanceledException) { }
                parkedStatus = (await engine.GetRunAsync(run, TestContext.Current.CancellationToken))!.Status;
                using var reader = new WorkflowGroupReader(database);
                originalOccurrences = (await ReadGroupOccurrences(reader, run, TestContext.Current.CancellationToken)).ToArray();
                var persisted = await database.GetStepsAsync(run, TestContext.Current.CancellationToken);
                originalMemberships = [];
                foreach (var step in persisted)
                {
                    var membership = await database.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken);
                    membership.Should().NotBeNull(step.StepKey);
                    originalMemberships = [.. originalMemberships, membership!];
                }
            }

            GroupedWorkflowAdmissionResult changed;
            if (changeManifestOnly)
            {
                var changedGroups = WorkflowGroupManifest.Create(
                    original.Definition.Groups.Groups.Select(group => group with { DisplayName = group.DisplayName + " revised" }),
                    original.Definition.Groups.Members);
                var changedBundle = GroupedWorkflowDefinitionDocument.Create(original.Definition.Definition, changedGroups);
                changedBundle.Definition.ExecutionFingerprint.Should().Be(original.Definition.Definition.ExecutionFingerprint);
                changedBundle.Groups.Fingerprint.Should().NotBe(original.Definition.Groups.Fingerprint);
                changed = await GroupAdmissionService().AdmitGroupedAsync(changedBundle, cancellationToken: TestContext.Current.CancellationToken);
                changed.Succeeded.Should().BeTrue();
            }
            else
            {
                var altered = source.Replace("checkpoint accepted value approval -> string;", "checkpoint accepted value prepare -> string;", StringComparison.Ordinal);
                changed = await AdmitGroupSource(altered);
                changed.Definition.Definition.ExecutionFingerprint.Should().NotBe(original.Definition.Definition.ExecutionFingerprint);
            }

            var changedRegistration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(changed.Admission), ports)
                .CreateGroupedAsync("group-pin", "1", changed, TestContext.Current.CancellationToken);
            await using (var reconfiguredEngine = new WorkflowEngine(database, changedRegistration.Register(new WorkflowRegistry())))
            {
                var resume = async () => await reconfiguredEngine.ExecuteAsync(run, TestContext.Current.CancellationToken);
                var rejection = await resume.Should().ThrowAsync<Exception>();
                var rejectionText = rejection.Which.Message.ToLowerInvariant();
                (rejectionText.Contains("group", StringComparison.Ordinal) || rejectionText.Contains("definition", StringComparison.Ordinal) ||
                    rejectionText.Contains("fingerprint", StringComparison.Ordinal) || rejectionText.Contains("manifest", StringComparison.Ordinal))
                    .Should().BeTrue(rejection.Which.Message);
                (await reconfiguredEngine.GetRunAsync(run, TestContext.Current.CancellationToken))!.Status.Should().Be(parkedStatus);
            }

            using var finalReader = new WorkflowGroupReader(database);
            var finalOccurrences = await ReadGroupOccurrences(finalReader, run, TestContext.Current.CancellationToken);
            finalOccurrences.Select(group => (group.OccurrenceId, group.ParentOccurrenceId, group.GroupKey))
                .Should().BeEquivalentTo(originalOccurrences.Select(group => (group.OccurrenceId, group.ParentOccurrenceId, group.GroupKey)));
            foreach (var membership in originalMemberships)
                (await database.GetStepGroupMembershipAsync(membership.StepId, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(membership);
            var pinnedOriginal = await definitions.ReadGroupedAsync(original.Definition.Fingerprint, TestContext.Current.CancellationToken);
            pinnedOriginal.Should().NotBeNull();
            pinnedOriginal!.CanonicalBytes.ToArray().Should().Equal(originalBundleBytes,
                "registering a changed execution or hierarchy companion must not rewrite the old immutable pin");
            activity.Requests.Should().ContainSingle("pin rejection happens before provider re-execution");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Nested_macro_inside_grouped_repeat_recovers_from_pinned_bytes_in_a_fresh_process()
    {
        const string source = $$"""
            macro Normalize(value: string) {
              activity normalize = activity "{{EchoDescriptor}}" (value: value;) -> string;
              checkpoint normalized value normalize -> string;
              yield normalized;
            }
            macro Review(value: string) {
              use prepared = Normalize(value: value);
              activity review = activity "{{EchoDescriptor}}" (value: prepared;) -> string;
              wait approval signal approve type string;
              checkpoint accepted value approval -> string;
              yield accepted;
            }
            workflow demo(input: string) -> string {
              group root {
                repeat loop max 2 state current: string = input {
                  group iteration {
                    use result = Review(value: current);
                    yield result;
                  }
                } continue iteration break iter == 2 -> string;
                yield loop;
              }
              return root;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-nested-process", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        System.Diagnostics.Process? worker = null;
        try
        {
            await new DirectoryWorkflowDefinitionStore(root).StoreGroupedAsync(admission.Definition, ct);
            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "nested-start");
            var errors = worker.StandardError.ReadToEndAsync(ct);
            while (!File.Exists(Path.Combine(root, "barrier")))
            {
                if (worker.HasExited) throw new InvalidOperationException("Worker exited before wait barrier: " + await errors);
                await Task.Delay(20, ct);
            }
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(ct);
            worker.Dispose();
            worker = null;

            var run = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "run-id"), ct));
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            using var reader = new WorkflowGroupReader(store);
            var beforeGroups = await ReadGroupOccurrences(reader, run, ct);
            beforeGroups.Should().HaveCount(4); // root, first iteration, Review, Normalize
            admission.Definition.Groups.Groups.Select(group => group.Kind).Should().Contain(WorkflowGroupKind.Logical)
                .And.Contain(WorkflowGroupKind.Macro);
            var iterationKey = admission.Definition.Groups.Groups.Single(group => group.GroupKey.EndsWith("/$group/iteration", StringComparison.Ordinal)).GroupKey;
            var reviewKey = admission.Definition.Groups.Groups.Single(group => group.Provenance?.MacroName == "Review").GroupKey;
            var normalizeKey = admission.Definition.Groups.Groups.Single(group => group.Provenance?.MacroName == "Normalize").GroupKey;
            var beforeSteps = await store.GetStepsAsync(run, ct);
            var committed = beforeSteps.Where(step => step.Status == StepStatus.Completed).ToArray();
            var stepMap = FuwenZhinuStepMapper.Map(admission.Definition.Definition.ReadPlan());
            var completedBody = new List<WorkflowStepRun>();
            foreach (var step in committed)
                if (FuwenZhinuStepMapper.TryMatchStepKey(stepMap, step.StepKey, out var match) &&
                    match.Descriptor.Kind is FuwenZhinuStepKind.Activity or FuwenZhinuStepKind.Checkpoint)
                    completedBody.Add(step);
            completedBody.Should().HaveCount(3); // Normalize activity/checkpoint + Review activity.
            var completedLoopControl = committed.Any(step => FuwenZhinuStepMapper.TryMatchStepKey(stepMap, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.LoopControl);
            completedLoopControl.Should().BeTrue();
            beforeSteps.Should().ContainSingle(step => step.Status == StepStatus.Waiting);
            var memberships = new Dictionary<Guid, ActivityGroupStepMembership>();
            foreach (var step in beforeSteps)
            {
                var membership = await store.GetStepGroupMembershipAsync(step.Id, ct);
                membership.Should().NotBeNull(step.StepKey);
                memberships.Add(step.Id, membership!);
            }
            var effectsBeforeRecovery = await File.ReadAllLinesAsync(Path.Combine(root, "effects"), ct);
            effectsBeforeRecovery.Should().HaveCount(2);

            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "nested-resume");
            errors = worker.StandardError.ReadToEndAsync(ct);
            await worker.WaitForExitAsync(ct);
            worker.ExitCode.Should().Be(0, await errors);

            var afterGroups = await ReadGroupOccurrences(reader, run, ct);
            afterGroups.Select(group => (group.OccurrenceId, group.ParentOccurrenceId, group.GroupKey))
                .Should().Contain(beforeGroups.Select(group => (group.OccurrenceId, group.ParentOccurrenceId, group.GroupKey)));
            foreach (var membership in memberships.Values)
                (await store.GetStepGroupMembershipAsync(membership.StepId, ct)).Should().BeEquivalentTo(membership);
            var finalSteps = await store.GetStepsAsync(run, ct);
            foreach (var step in completedBody)
            {
                var after = finalSteps.Single(candidate => candidate.StepKey == step.StepKey);
                after.Id.Should().Be(step.Id);
                after.Revision.Should().Be(step.Revision);
            }
            (await File.ReadAllLinesAsync(Path.Combine(root, "effects"), ct)).Should().HaveCount(4, "the first iteration's committed activity work is replayed, not invoked again");
            (await File.ReadAllTextAsync(Path.Combine(root, "result"), ct)).Should().Be("accepted");
            afterGroups.Should().HaveCount(7);
            var rootOccurrence = afterGroups.Single(group => group.GroupKey == "demo/$group/root");
            var iterations = afterGroups.Where(group => group.GroupKey == iterationKey).ToArray();
            iterations.Should().HaveCount(2);
            iterations.Should().OnlyContain(group => group.ParentOccurrenceId == rootOccurrence.OccurrenceId);
            foreach (var iteration in iterations)
            {
                var review = afterGroups.Single(group => group.GroupKey == reviewKey
                    && group.ParentOccurrenceId == iteration.OccurrenceId);
                afterGroups.Should().Contain(group => group.GroupKey == normalizeKey
                    && group.ParentOccurrenceId == review.OccurrenceId);
            }
            var mapper = FuwenZhinuStepMapper.Map(admission.Definition.Definition.ReadPlan());
            foreach (var step in finalSteps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(mapper, step.StepKey, out _).Should().BeTrue(step.StepKey);
                var membership = await store.GetStepGroupMembershipAsync(step.Id, ct);
                if (membership is not null)
                    afterGroups.Should().Contain(group => group.OccurrenceId == membership.GroupOccurrenceId, step.StepKey);
            }
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
}
