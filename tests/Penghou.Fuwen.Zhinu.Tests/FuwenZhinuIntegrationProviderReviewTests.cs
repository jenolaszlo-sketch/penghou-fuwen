using FluentAssertions;
using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task Review_dynamic_provider_run_scope_is_versioned_for_all_provider_kinds(bool repeat, bool grouped, bool legacy)
    {
        var ct = TestContext.Current.CancellationToken;
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        TrustedCatalogueDescriptor Callable(DescriptorKind kind, string name, char digest, string parameter) =>
            new(new DescriptorReference(kind, name, "1", Digest(digest)), callableContract: new CallableContract(
                new CallableSignature([new CallableParameter(parameter, text)], text),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe));
        var catalogue = new InMemoryTrustedCatalogue([
            Callable(DescriptorKind.Activity, "sample.echo", 'a', "value"),
            Callable(DescriptorKind.ContextProvider, "sample.context", 'b', "request"),
            Callable(DescriptorKind.InferenceProfile, "sample.profile", 'c', "request"),
            new TrustedCatalogueDescriptor(new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('d')))]);
        var type = repeat ? "string" : "list<string>[2]";
        var item = repeat ? "current" : "item";
        var header = repeat ? "repeat process max 1 state current: string = input" : "fanout process over input as item: string key item max 2 concurrency 1";
        var tail = repeat ? $"continue {(grouped ? "row" : "echo")} break iter == 1 -> string;" : $"yield {(grouped ? "row" : "echo")} -> list<string>[2];";
        var source = $$"""
            workflow demo(input: {{type}}) -> {{type}} {
              {{(grouped ? "group root {" : "")}}
                {{header}} {
                  {{(grouped ? "group row {" : "")}}
                    context cx = context "sample.context@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" (request: {{item}};) -> string;
                    infer answer = infer "sample.profile@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" using "sample.prompt@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" (request: cx;) with cx -> string;
                    activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: answer;) -> string;
                  {{(grouped ? "yield echo; }" : "")}}
                } {{tail}}
              {{(grouped ? "yield process; } return root;" : "return process;")}}
            }
            """;
        var compiled = await new FuwenSourceCompiler(catalogue).CompileAsync(source, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(value => value.Message)));
        var plan = compiled.Plan!;
        plan.IrVersion.Should().Be(FuwenContracts.RunScopedDynamicsIrVersion);
        plan.UsesExpandedGroupScopes.Should().Be(grouped);
        if (legacy) plan = plan with { IrVersion = grouped ? FuwenContracts.GroupExpansionIrVersion : FuwenContracts.IrVersion, ExpandedGroupScopes = false };
        var bytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        if (legacy) System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("expandedGroupScopes");
        var service = new WorkflowAdmissionService(new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
        GroupedWorkflowAdmissionResult? groupAdmission = grouped
            ? await service.AdmitGroupedAsync(GroupedWorkflowDefinitionDocument.Create(WorkflowDefinitionDocument.Create(plan), compiled.GroupedDefinition!.Groups), cancellationToken: ct) : null;
        var admission = groupAdmission?.Admission ?? await service.AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(value => value.Message)));
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-review-dynamic-providers", Guid.NewGuid().ToString("N"));
        try
        {
            var observer = new RecordingObserver();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(new RecordingActivity(), new EchoFanOutContext(), CurrentInferenceFixture.WithPreflight(new EchoFanOutInference()), observer));
            var registration = groupAdmission is null ? await factory.CreateAsync("review", "1", admission, ct)
                : await factory.CreateGroupedAsync("review", "1", groupAdmission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            foreach (var index in new[] { 0, 1 })
            {
                var input = repeat ? JsonSerializer.SerializeToElement("same") : JsonSerializer.SerializeToElement(new[] { "a", "b" });
                var id = await engine.StartAsync("review", "1", input, cancellationToken: ct);
                await engine.ExecuteAsync(id, ct);
                await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct);
                var calls = observer.Observations.Where(value => value.Kind == ExecutionObservationKind.Requested).Select(value => value.Invocation).ToArray();
                var perRun = repeat ? 3 : 6;
                calls.Should().HaveCount((index + 1) * perRun);
                calls.Skip(index * perRun).Should().OnlyContain(value => value.RuntimePath.StartsWith(id.ToString("D") + "/", StringComparison.Ordinal) == (!legacy || grouped && !repeat));
                if (!legacy) calls.Select(value => value.OperationKey).Should().OnlyHaveUniqueItems();
                AssertEveryStepMatches(registration.Definition.ReadPlan(), await store.GetStepsAsync(id, ct));
                if (!repeat && !grouped)
                {
                    var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
                    foreach (var step in await store.GetStepsAsync(id, ct))
                        if (step.StepKey.Contains("/$item/sha256-", StringComparison.Ordinal))
                        {
                            var invalidKey = step.StepKey[..^64] + new string('z', 64);
                            FuwenZhinuStepMapper.TryMatchStepKey(map, invalidKey, out _).Should().BeFalse();
                        }
                }
                await engine.RestartStepAsync(id, plan.Nodes.OfType<ReturnNode>().Single().StructuralPath, ct);
                await engine.ExecuteAsync(id, ct);
                await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct);
                observer.Observations.Count(value => value.Kind == ExecutionObservationKind.Requested).Should().Be(calls.Length,
                    "completed dynamic results must replay without dispatching providers again");
            }
            WorkflowPlanIdentity.GetCanonicalBytes(registration.Definition.ReadPlan()).Should().Equal(bytes);
        }
        finally { DeleteDirectory(directory); }
    }
}
