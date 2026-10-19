using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Ungrouped_repeat_wait_uses_native_wait_with_historical_key_and_step_input_contract()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop max 1 state current: string = input {
                wait approval signal approve type string;
              } continue approval break iter == 1 -> string;
              return loop;
            }
            """;
        var compiled = await new FuwenSourceCompiler(GroupCatalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        compiled.GroupedDefinition.Should().BeNull();
        var admission = await GroupAdmissionService().AdmitAsync(compiled.Plan!, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue();
        var registration = await new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(), IdentityFor(admission),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), new UnusedInference()))
            .CreateAsync("legacy-wait", "1", admission, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-legacy-repeat-wait", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var run = await engine.StartAsync("legacy-wait", "1", JsonSerializer.SerializeToElement("input"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            var wait = (await store.GetStepsAsync(run, ct)).Single(step => step.StepKey == "$loop/loop/1/body/approval");
            wait.Status.Should().Be(StepStatus.Waiting);
            wait.InputJson.Should().Be(JsonSerializer.Serialize("approve"));
            (await store.GetStepGroupMembershipAsync(wait.Id, ct)).Should().BeNull();
            await engine.SendSignalAsync(run, "approve", "accepted", ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct)).GetString().Should().Be("accepted");
            var finished = (await store.GetStepsAsync(run, ct)).Single(step => step.StepKey == wait.StepKey);
            finished.Id.Should().Be(wait.Id);
            finished.Revision.Should().Be(wait.Revision);
        }
        finally { DeleteDirectory(root); }
    }
}
