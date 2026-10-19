using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grouped_fanout_requires_durable_work_on_every_structural_path(bool bothBranchesHaveWork)
    {
        var alternate = bothBranchesHaveWork
            ? "activity right = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: item;) -> string;"
            : "";
        var source = $$"""
            workflow demo(input: list<string>[2]) -> list<string>[2] {
              group root {
                fanout process over input as item: string key item max 2 {
                  group row {
                    if decision item == "a" {
                      activity left = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
                    } else { {{alternate}} }
                  }
                } yield item -> list<string>[2];
                yield process;
              }
              return root;
            }
            """;
        var admission = await AdmitGroupSource(source);
        var root = Path.Combine(Path.GetTempPath(), "fuwen-group-fanout-paths", Guid.NewGuid().ToString("N"));
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var activity = new GroupFanOutActivity();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(root), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            if (!bothBranchesHaveWork)
            {
                var register = async () => await factory.CreateGroupedAsync("fanout-paths", "1", admission, ct);
                await register.Should().ThrowAsync<FuwenZhinuAdmissionException>().WithMessage("*durable operation*every*path*");
                Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Should().BeEmpty();
                activity.Requests.Should().BeEmpty();
                return;
            }

            var registration = await factory.CreateGroupedAsync("fanout-paths", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(root, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var run = await engine.StartAsync("fanout-paths", "1", JsonSerializer.SerializeToElement(new[] { "a", "b" }), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            var result = await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct);
            result.EnumerateArray().Select(value => value.GetString()).Should().Equal("a", "b");
            activity.Requests.Should().HaveCount(2);
        }
        finally { DeleteDirectory(root); }
    }
}
