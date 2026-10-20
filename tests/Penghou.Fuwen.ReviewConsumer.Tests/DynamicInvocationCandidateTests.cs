using FluentAssertions;
using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

public sealed partial class SelectionCandidateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dynamic_candidate_keeps_operation_keys_run_local_and_maps_persisted_keys(bool repeat)
    {
        var source = repeat ? """
            workflow demo(input: string) -> string {
              group root {
                repeat process max 2 state current: string = input {
                  group row {
                    activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: current;) -> string;
                    yield echo;
                  }
                } continue row break iter == 2 -> string;
                yield process;
              }
              return root;
            }
            """ : """
            workflow demo(input: list<string>[2]) -> list<string>[2] {
              fanout process over input as item: string key item max 2 concurrency 1 {
                activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
              } yield echo -> list<string>[2];
              return process;
            }
            """;
        var ct = TestContext.Current.CancellationToken;
        var compiled = await new FuwenSourceCompiler(SelectionCatalogue()).CompileAsync(source, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(value => value.Message)));
        compiled.Plan!.IrVersion.Should().Be(FuwenContracts.RunScopedDynamicsIrVersion);
        compiled.Plan.UsesExpandedGroupScopes.Should().Be(repeat);
        var service = new WorkflowAdmissionService(new WorkflowCompiler(SelectionCatalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])));
        var grouped = repeat ? await service.AdmitGroupedAsync(compiled.GroupedDefinition!, cancellationToken: ct) : null;
        var admission = grouped?.Admission ?? await service.AdmitAsync(compiled.Plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-dynamic-consumer", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new SelectionActivity(1, 0);
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            var registration = grouped is null ? await factory.CreateAsync("dynamic", "1", admission, ct)
                : await factory.CreateGroupedAsync("dynamic", "1", grouped, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var map = FuwenZhinuStepMapper.Map(registration.Definition.ReadPlan());
            foreach (var index in new[] { 0, 1 })
            {
                var input = repeat ? JsonSerializer.SerializeToElement("same") : JsonSerializer.SerializeToElement(new[] { "a", "b" });
                var id = await engine.StartAsync("dynamic", "1", input, cancellationToken: ct);
                await engine.ExecuteAsync(id, ct);
                await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct);
                activity.Requests.Should().HaveCount((index + 1) * 2);
                activity.Requests.Skip(index * 2).Should().OnlyContain(value => value.Invocation.RuntimePath.StartsWith(id.ToString("D") + "/", StringComparison.Ordinal));
                activity.Requests.Select(value => value.Invocation.OperationKey).Should().OnlyHaveUniqueItems();
                foreach (var step in await store.GetStepsAsync(id, ct))
                {
                    FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out var match).Should().BeTrue(step.StepKey);
                    FuwenZhinuStepMapper.ResolveStepKey(match.Descriptor, match.Scope).Should().Be(step.StepKey);
                }
            }
        }
        finally { DeleteDirectory(directory); }
    }
}
