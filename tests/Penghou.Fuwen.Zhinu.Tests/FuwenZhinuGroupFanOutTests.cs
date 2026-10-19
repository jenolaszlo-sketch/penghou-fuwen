using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    private const string GroupedFanOutBudgetSource = """
        schema Item "sample.item@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" {
          key: string;
          value: string;
        }
        workflow demo(input: list<Item>[2]) -> list<string>[2] {
          group root {
            fanout process over input as item: Item key item.key max 2 concurrency 1 {
              group row {
                activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item.value;) -> string;
                yield echo;
              }
            } yield row -> list<string>[2];
            yield process;
          }
          return root;
        }
        """;

    private const string GroupedFanOutSource = """
        macro Pipeline(value: string) {
          activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
          activity second = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: first;) -> string;
          yield second;
        }
        workflow demo(input: list<string>[2]) -> list<string>[2] {
          group root {
            fanout process over input as item: string key item max 2 concurrency 2 {
              group row {
                group nested {
                  use pipeline = Pipeline(value: item);
                  yield pipeline;
                }
                yield nested;
              }
            } yield row -> list<string>[2];
            yield process;
          }
          return root;
        }
        """;

    [Fact]
    public async Task Grouped_fanout_creates_item_scoped_occurrence_trees_and_durable_operation_steps()
    {
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(),
                        observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
                .CreateGroupedAsync("grouped-fanout", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("[\"b\",\"a\"]");
            var runId = await engine.StartAsync("grouped-fanout", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken))
                .EnumerateArray().Select(value => value.GetString()).Should().Equal("b", "a");
            activity.Requests.Should().HaveCount(4);

            var plan = registration.Definition.ReadPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Should().NotBeEmpty();
            var matches = new Dictionary<Guid, FuwenZhinuStepMatch>();
            foreach (var step in steps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                matches.Add(step.Id, match);
                if (match.Descriptor.ExactStepKey is not null || match.Descriptor.BodyStepName is not null || match.Descriptor.FanOutNodePath is not null)
                    FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
            }

            var bodySteps = steps.Where(step => matches[step.Id].Descriptor.Kind == FuwenZhinuStepKind.Activity &&
                    matches[step.Id].Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem))
                .ToArray();
            bodySteps.Should().HaveCount(4);
            var perItem = bodySteps.GroupBy(step => matches[step.Id].Scope.Single(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem).ItemToken)
                .ToDictionary(group => group.Key!, group => group.ToArray(), StringComparer.Ordinal);
            perItem.Should().HaveCount(2);
            foreach (var item in perItem.Values)
            {
                item.Select(step => matches[step.Id].Descriptor.DeclaredNodePath!.Split('/').Last())
                    .Should().Equal("first", "second");
                foreach (var step in item)
                {
                    var membership = await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken);
                    membership.Should().NotBeNull();
                    membership!.Role.Should().Be(ActivityGroupMemberRole.Work);
                }
            }

            using var reader = new WorkflowGroupReader(store);
            var occurrences = await ReadGroupOccurrences(reader, runId, TestContext.Current.CancellationToken);
            var rootGroup = occurrences.Should().ContainSingle(value => value.GroupKey == "demo/$group/root").Which;
            var itemGroups = occurrences.Where(value => value.GroupKey.EndsWith("/$group/row", StringComparison.Ordinal)).ToArray();
            var nestedGroups = occurrences.Where(value => value.GroupKey.EndsWith("/$group/nested", StringComparison.Ordinal)).ToArray();
            var macroGroups = occurrences.Where(value => value.GroupKey.EndsWith("/$group/pipeline", StringComparison.Ordinal)).ToArray();
            itemGroups.Should().HaveCount(2).And.OnlyContain(value => value.ParentOccurrenceId == rootGroup.OccurrenceId);
            itemGroups.Select(value => value.OccurrenceId).Should().OnlyHaveUniqueItems();
            nestedGroups.Should().HaveCount(2);
            macroGroups.Should().HaveCount(2);
            foreach (var nested in nestedGroups)
                itemGroups.Should().ContainSingle(value => value.OccurrenceId == nested.ParentOccurrenceId);
            foreach (var macro in macroGroups)
                nestedGroups.Should().ContainSingle(value => value.OccurrenceId == macro.ParentOccurrenceId);
            foreach (var membership in bodySteps.Select(async step => await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken)))
            {
                var value = await membership;
                macroGroups.Should().ContainSingle(group => group.OccurrenceId == value!.GroupOccurrenceId);
            }

            var controlKinds = new[] { FuwenZhinuStepKind.FanOutSelection, FuwenZhinuStepKind.FanOutItemCompletion, FuwenZhinuStepKind.FanOutAggregate };
            foreach (var control in steps.Where(step => controlKinds.Contains(matches[step.Id].Descriptor.Kind)))
            {
                var membership = await store.GetStepGroupMembershipAsync(control.Id, TestContext.Current.CancellationToken);
                membership.Should().NotBeNull(control.StepKey);
                membership!.Role.Should().Be(ActivityGroupMemberRole.Control);
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_control_only_conditional_branches_keep_item_lineage()
    {
        const string source = """
            workflow demo(input: list<string>[2]) -> list<string>[2] {
              group root {
                fanout process over input as item: string key item max 2 {
                  group row {
                    if decision item == "a" {
                      activity left = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                    } else {
                      activity right = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                    }
                    activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                    yield echo;
                  }
                } yield row -> list<string>[2];
                yield process;
              }
              return root;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-control", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-fanout-control", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[\"a\",\"b\"]");
            var runId = await engine.StartAsync("grouped-fanout-control", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken))
                .EnumerateArray().Select(value => value.GetString()).Should().Equal("a", "b");
            activity.Requests.Should().HaveCount(4, "each item selects one branch operation and executes its output operation");

            var plan = registration.Definition.ReadPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            foreach (var step in steps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                if (match.Descriptor.ExactStepKey is not null || match.Descriptor.BodyStepName is not null || match.Descriptor.FanOutNodePath is not null)
                    FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
            }
            var branchSteps = steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                (match.Descriptor.DeclaredNodePath!.EndsWith("/left", StringComparison.Ordinal) ||
                 match.Descriptor.DeclaredNodePath.EndsWith("/right", StringComparison.Ordinal))).ToArray();
            branchSteps.Should().HaveCount(2);
            foreach (var step in branchSteps)
                (await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken))!.Role
                    .Should().Be(ActivityGroupMemberRole.Work);

            using var reader = new WorkflowGroupReader(store);
            var groups = await ReadGroupOccurrences(reader, runId, TestContext.Current.CancellationToken);
            var rootGroup = groups.Should().ContainSingle(value => value.GroupKey == "demo/$group/root").Which;
            groups.Where(value => value.GroupKey.EndsWith("/$group/row", StringComparison.Ordinal))
                .Should().HaveCount(2).And.OnlyContain(value => value.ParentOccurrenceId == rootGroup.OccurrenceId);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_context_and_one_call_inference_replay_with_item_scope()
    {
        const string source = """
            workflow demo(input: list<string>[2]) -> list<string>[2] {
              group root {
                fanout process over input as item: string key item max 2 concurrency 2 {
                  group row {
                    context cx = context "sample.context@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" (request: item;) -> string;
                    infer answer = infer "sample.profile@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" using "sample.prompt@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" (request: cx;) with cx -> string;
                    yield answer;
                  }
                } yield row -> list<string>[2];
                yield process;
              }
              return root;
            }
            """;
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        TrustedCatalogueDescriptor Callable(DescriptorReference descriptor) => new(descriptor,
            callableContract: new CallableContract(new CallableSignature([new CallableParameter("request", text)], text),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
        var contextDescriptor = new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('b'));
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('c'));
        var prompt = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('d'));
        var catalogue = new InMemoryTrustedCatalogue([Callable(contextDescriptor), Callable(profile), new TrustedCatalogueDescriptor(prompt)]);
        var compiled = await new FuwenSourceCompiler(catalogue).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var admitted = await new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: TestContext.Current.CancellationToken);
        admitted.Succeeded.Should().BeTrue(string.Join("; ", admitted.Admission.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var context = new EchoFanOutContext();
        var inference = new EchoFanOutInference();
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-inference", Guid.NewGuid().ToString("N"));
        try
        {
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admitted.Admission),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), context, CurrentInferenceFixture.WithPreflight(inference),
                    observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 1)))
                .CreateGroupedAsync("grouped-fanout-inference", "1", admitted, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[\"b\",\"a\"]");
            var runId = await engine.StartAsync("grouped-fanout-inference", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken))
                .EnumerateArray().Select(value => value.GetString()).Should().Equal("b.ctx.inf", "a.ctx.inf");
            context.Calls.Should().Be(2);
            inference.Calls.Should().Be(2);
            inference.Requests.Should().HaveCount(2).And.OnlyContain(request => request.ContextInputs.Count == 1);

            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            var scoped = steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                (match.Descriptor.Kind is FuwenZhinuStepKind.Context or FuwenZhinuStepKind.Inference) &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem)).ToArray();
            scoped.Should().HaveCount(4);
            foreach (var step in scoped)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue();
                FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
                (await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken))!.Role
                    .Should().Be(ActivityGroupMemberRole.Work);
            }
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            context.Calls.Should().Be(2);
            inference.Calls.Should().Be(2);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_selection_budget_rejects_large_item_before_selection_step_or_provider_work()
    {
        var admission = await AdmitGroupedFanOutBudgetSource();
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-selection-budget", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new CountingBudgetActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-fanout-selection-budget", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var huge = new string('v', 1_048_600);
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { key = "short", value = huge } }));
            var runId = await engine.StartAsync("grouped-fanout-selection-budget", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
            activity.Calls.Should().Be(0);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.FanOutSelection).Should().BeEmpty("the full selected item vector must pass its byte budget before the selection step is written");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_oversized_provider_outcome_does_not_complete_item_or_aggregate()
    {
        var admission = await AdmitGroupedFanOutBudgetSource();
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-outcome-budget", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new OversizedBudgetActivity(new string('o', 1_048_600));
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-fanout-outcome-budget", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[{\"key\":\"short\",\"value\":\"small\"}]");
            var runId = await engine.StartAsync("grouped-fanout-outcome-budget", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
            activity.Calls.Should().BeGreaterThan(0);
            var plan = registration.Definition.ReadPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity).Should().NotBeEmpty();
            steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                (match.Descriptor.Kind is FuwenZhinuStepKind.FanOutItemCompletion or FuwenZhinuStepKind.FanOutAggregate) &&
                step.Status == StepStatus.Completed).Should().BeEmpty();
            var returnKey = plan.Nodes.OfType<ReturnNode>().Should().ContainSingle().Which.StructuralPath;
            steps.Should().NotContain(step => step.StepKey == returnKey && step.Status == StepStatus.Completed);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task<GroupedWorkflowAdmissionResult> AdmitGroupedFanOutBudgetSource()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var descriptor = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var schemaDescriptor = new DescriptorReference(DescriptorKind.Schema, "sample.item", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));
        var schema = new ObjectSchemaDefinition(schemaDescriptor,
            [new SchemaField("key", text), new SchemaField("value", text)]);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(descriptor,
                callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(schemaDescriptor, schemaDefinition: schema)]);
        var compiled = await new FuwenSourceCompiler(catalogue).CompileAsync(GroupedFanOutBudgetSource,
            cancellationToken: TestContext.Current.CancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var admitted = await new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: TestContext.Current.CancellationToken);
        admitted.Succeeded.Should().BeTrue(string.Join("; ", admitted.Admission.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return admitted;
    }

    [Fact]
    public async Task Grouped_fanout_restart_of_one_operation_reuses_its_predecessor_and_sibling_item()
    {
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-restart", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference(),
                        observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 2)))
                .CreateGroupedAsync("grouped-fanout-restart", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("[\"a\",\"b\"]");
            var runId = await engine.StartAsync("grouped-fanout-restart", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);

            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var before = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            var operationSteps = before.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity &&
                match.Descriptor.DeclaredNodePath?.EndsWith("/second", StringComparison.Ordinal) == true &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem)).ToArray();
            operationSteps.Should().HaveCount(2);
            var chosen = operationSteps[0];
            FuwenZhinuStepMapper.TryMatchStepKey(map, chosen.StepKey, out var chosenMatch).Should().BeTrue();
            var siblingToken = operationSteps.Single(step => step.Id != chosen.Id);
            FuwenZhinuStepMapper.TryMatchStepKey(map, siblingToken.StepKey, out var siblingMatch).Should().BeTrue();
            var itemToken = chosenMatch.Scope.Single(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem).ItemToken;
            var siblingItemToken = siblingMatch.Scope.Single(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem).ItemToken;
            var predecessor = before.Single(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity && match.Descriptor.DeclaredNodePath?.EndsWith("/first", StringComparison.Ordinal) == true &&
                match.Scope.Single(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem).ItemToken == itemToken);
            var siblingOperations = before.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem && scope.ItemToken == siblingItemToken)).ToArray();
            var predecessorMembership = await store.GetStepGroupMembershipAsync(predecessor.Id, TestContext.Current.CancellationToken);
            var siblingMemberships = new Dictionary<Guid, ActivityGroupStepMembership?>();
            foreach (var step in siblingOperations)
                siblingMemberships.Add(step.Id, await store.GetStepGroupMembershipAsync(step.Id, TestContext.Current.CancellationToken));
            var callsBefore = activity.Requests.Count;

            await engine.RestartStepAsync(runId, chosen.StepKey, TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);

            var after = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            after.Should().Contain(step => step.Id == predecessor.Id && step.Revision == predecessor.Revision);
            (await store.GetStepGroupMembershipAsync(predecessor.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(predecessorMembership);
            foreach (var sibling in siblingOperations)
            {
                after.Should().Contain(step => step.Id == sibling.Id && step.Revision == sibling.Revision);
                (await store.GetStepGroupMembershipAsync(sibling.Id, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(siblingMemberships[sibling.Id]);
            }
            activity.Requests.Should().HaveCount(callsBefore + 1);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_rejects_changed_selection_values_before_restarting_provider_work()
    {
        var admission = await AdmitGroupedFanOutBudgetSource();
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-selection-integrity", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("fanout-integrity", "1", admission, TestContext.Current.CancellationToken);
            var database = Path.Combine(root, "run.db");
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = database, Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var input = JsonSerializer.SerializeToElement(new[] { new { key = "a", value = "original" } });
            var runId = await engine.StartAsync("fanout-integrity", "1", input, cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            var selection = steps.Single(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.FanOutSelection);
            var provider = steps.Single(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity);
            var changed = JsonNode.Parse(selection.OutputJson!)!;
            changed[0]!["value"]!["value"] = "altered";
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE workflow_steps SET output_json=$output WHERE id=$id";
                command.Parameters.AddWithValue("$output", changed.ToJsonString());
                command.Parameters.AddWithValue("$id", selection.Id.ToString("D"));
                (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
            }
            await engine.RestartStepAsync(runId, provider.StepKey, TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var failed = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
            failed!.Status.Should().Be(WorkflowStatus.Failed);
            failed.Error!.Message.Should().Contain("Persisted grouped selection differs");
            activity.Requests.Should().ContainSingle();
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_duplicate_keys_reject_before_item_roots_or_provider_work()
    {
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-duplicate", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            var registration = await factory.CreateGroupedAsync("grouped-fanout-duplicate", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[\"same\",\"same\"]");
            var runId = await engine.StartAsync("grouped-fanout-duplicate", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
            activity.Requests.Should().BeEmpty();
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem)).Should().BeEmpty();
            using var reader = new WorkflowGroupReader(store);
            (await ReadGroupOccurrences(reader, runId, TestContext.Current.CancellationToken))
                .Should().NotContain(item => item.GroupKey.EndsWith("/$group/row", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Grouped_fanout_operation_failure_is_persisted_and_prevents_completion()
    {
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-failure", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new GroupFanOutActivity(failSecondFor: "b");
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()))
                .CreateGroupedAsync("grouped-fanout-failure", "1", admission, TestContext.Current.CancellationToken);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            using var input = JsonDocument.Parse("[\"a\",\"b\"]");
            var runId = await engine.StartAsync("grouped-fanout-failure", "1", input.RootElement.Clone(), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
            var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            steps.Where(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.FanOutAggregate && step.Status == StepStatus.Completed).Should().NotBeEmpty();
            steps.Should().NotContain(step => step.StepKey == registration.Definition.ReadPlan().Nodes.OfType<ReturnNode>().Single().StructuralPath &&
                step.Status == StepStatus.Completed);
            activity.Requests.Should().Contain(request => request.Invocation.StructuralPath.EndsWith("/second", StringComparison.Ordinal) &&
                RuntimeValueJson.ToJsonElement(request.Arguments.Single().Value).GetString() == "b");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Process_loss_between_grouped_fanout_operations_reuses_the_committed_operation()
    {
        var admission = await AdmitGroupSource(GroupedFanOutSource);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-process", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(55));
        var ct = timeout.Token;
        System.Diagnostics.Process? worker = null;
        try
        {
            var definitions = new DirectoryWorkflowDefinitionStore(root);
            var registration = await new FuwenZhinuWorkflowFactory(definitions, IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(new GroupFanOutActivity(), new UnusedContext(), new UnusedInference(),
                    observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: 1)))
                .CreateGroupedAsync("group-process", "1", admission, ct);
            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "fanout-start");
            var error = worker.StandardError.ReadToEndAsync(ct);
            var secondEntered = Path.Combine(root, "fanout-second-entered");
            while (!File.Exists(secondEntered))
            {
                if (worker.HasExited) throw new InvalidOperationException("Fan-out worker exited before the second operation barrier: " + await error);
                await Task.Delay(20, ct);
            }

            var runId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(root, "run-id"), ct));
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            var beforeSteps = await store.GetStepsAsync(runId, ct);
            var first = beforeSteps.Single(step => FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match) &&
                match.Descriptor.Kind == FuwenZhinuStepKind.Activity &&
                match.Descriptor.DeclaredNodePath?.EndsWith("/first", StringComparison.Ordinal) == true &&
                match.Scope.Any(scope => scope.Kind == FuwenZhinuStepScopeKind.FanOutItem));
            first.Status.Should().Be(StepStatus.Completed);
            var firstMembership = await store.GetStepGroupMembershipAsync(first.Id, ct);
            firstMembership.Should().NotBeNull();
            firstMembership!.Role.Should().Be(ActivityGroupMemberRole.Work);
            using var reader = new WorkflowGroupReader(store);
            var beforeGroups = await ReadGroupOccurrences(reader, runId, ct);

            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(ct);
            worker.Dispose();
            worker = null;
            (await File.ReadAllLinesAsync(Path.Combine(root, "effects"), ct)).Should().ContainSingle(line => line.EndsWith("/first", StringComparison.Ordinal));

            worker = StartGroupWorker(root, admission.Definition.Fingerprint, "fanout-resume");
            error = worker.StandardError.ReadToEndAsync(ct);
            await worker.WaitForExitAsync(ct);
            worker.ExitCode.Should().Be(0, await error);

            var afterSteps = await store.GetStepsAsync(runId, ct);
            afterSteps.Should().Contain(step => step.Id == first.Id && step.Revision == first.Revision && step.Status == StepStatus.Completed);
            (await store.GetStepGroupMembershipAsync(first.Id, ct)).Should().BeEquivalentTo(firstMembership);
            var afterGroups = await ReadGroupOccurrences(reader, runId, ct);
            afterGroups.Select(group => (group.OccurrenceId, group.ParentOccurrenceId)).Should()
                .BeEquivalentTo(beforeGroups.Select(group => (group.OccurrenceId, group.ParentOccurrenceId)));
            (await File.ReadAllLinesAsync(Path.Combine(root, "effects"), ct)).Should().HaveCount(2)
                .And.Contain(line => line.EndsWith("/first", StringComparison.Ordinal))
                .And.Contain(line => line.EndsWith("/second", StringComparison.Ordinal));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "result"), ct));
            result.RootElement.EnumerateArray().Select(value => value.GetString()).Should().Equal("a");
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

    [Fact]
    public async Task Grouped_fanout_admission_rejects_missing_or_multiple_item_roots_before_storage()
    {
        const string prefix = "workflow demo(input: list<string>[2]) -> list<string>[2] { group root { fanout process over input as item: string key item max 2 { ";
        const string suffix = " } yield first -> list<string>[2]; yield process; } return root; }";
        var sources = new[]
        {
            prefix + "activity first = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: item;) -> string;" + suffix,
            prefix + "group one { activity first = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: item;) -> string; yield first; } " +
            "group two { activity second = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: item;) -> string; yield second; }" +
            " } yield two -> list<string>[2]; yield process; } return root; }",
            """
            workflow demo(input: list<string>[2]) -> list<string>[2] {
              group root {
                repeat outer max 1 state current: list<string>[2] = input {
                  checkpoint saved value current -> list<string>[2];
                } continue saved break iter == 1 -> list<string>[2];
                fanout process over outer as item: string key item max 2 {
                  group row {
                    activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                    yield first;
                  }
                } yield row -> list<string>[2];
                yield process;
              }
              return root;
            }
            """,
        };
        foreach (var source in sources)
        {
            var admission = await AdmitGroupSource(source);
            var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-invalid", Guid.NewGuid().ToString("N"));
            try
            {
                var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                    new FuwenZhinuExecutionPorts(new GroupFanOutActivity(), new UnusedContext(), new UnusedInference()));
                var action = async () => await factory.CreateGroupedAsync("invalid-fanout", "1", admission, TestContext.Current.CancellationToken);
                await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
                Directory.EnumerateFiles(root).Should().BeEmpty();
            }
            finally { DeleteDirectory(root); }
        }
    }

    [Fact]
    public async Task Nested_grouped_fanout_is_rejected_by_admission_before_storage()
    {
        const string source = """
            workflow demo(input: list<string>[2]) -> list<list<string>[2]>[2] {
              group root {
                fanout outer over input as item: string key item max 2 {
                  group row {
                    fanout inner over input as sub: string key sub max 2 {
                      activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: sub;) -> string;
                    } yield first -> list<string>[2];
                    yield inner;
                  }
                } yield row -> list<list<string>[2]>[2];
                yield outer;
              }
              return root;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-nested-reject", Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(new GroupFanOutActivity(), new UnusedContext(), new UnusedInference()));
            var action = async () => await factory.CreateGroupedAsync("nested-fanout", "1", admission, TestContext.Current.CancellationToken);
            await action.Should().ThrowAsync<FuwenZhinuAdmissionException>();
            Directory.EnumerateFiles(root).Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class GroupFanOutActivity(string? failSecondFor = null) : IActivityExecutor
    {
        private readonly ConcurrentQueue<ActivityExecutionRequest> requests = new();
        public IReadOnlyCollection<ActivityExecutionRequest> Requests => requests.ToArray();

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            requests.Enqueue(request);
            var value = RuntimeValueJson.ToJsonElement(request.Arguments.Single().Value).GetString();
            if (failSecondFor is not null && value == failSecondFor &&
                request.Invocation.StructuralPath.EndsWith("/second", StringComparison.Ordinal))
                throw new InvalidOperationException("fan-out second operation failed");
            if (value == "a" && request.Invocation.StructuralPath.EndsWith("/first", StringComparison.Ordinal))
                await Task.Delay(30, cancellationToken);
            return ActivityExecutionResult.Succeeded(request.Arguments.Single().Value);
        }
    }

    private sealed class CountingBudgetActivity : IActivityExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(request.Arguments.Single().Value));
        }
    }

    private sealed class OversizedBudgetActivity(string output) : IActivityExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(output))));
        }
    }
}
