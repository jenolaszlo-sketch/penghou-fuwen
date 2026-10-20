using FluentAssertions;
using System.Text.Json;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    private const string ReviewRepeatSource = """
        workflow demo(input: string) -> string {
          group root {
            repeat loop max 2 state current: string = input {
              group row {
                activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: current;) -> string;
                yield echo;
              }
            } continue row break iter == 2 -> string;
            yield loop;
          }
          return root;
        }
        """;

    [Fact]
    public async Task Review_grouped_repeat_operation_keys_are_distinct_between_identical_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitGroupSource(ReviewRepeatSource);
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-review-run-identity", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var registration = await new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission.Admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference())).CreateGroupedAsync("review", "1", admission, ct);
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
            await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
            var runs = new List<Guid>();
            for (var index = 0; index < 2; index++)
            {
                var id = await engine.StartAsync("review", "1", JsonSerializer.SerializeToElement("same"), cancellationToken: ct);
                runs.Add(id);
                await engine.ExecuteAsync(id, ct);
                (await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct)).GetString().Should().Be("same");
            }
            activity.Requests.Should().HaveCount(4);
            activity.Requests.Select(request => request.Invocation.OperationKey).Should().OnlyHaveUniqueItems(
                "external idempotency keys must distinguish independent workflow runs");
            foreach (var run in runs)
                activity.Requests.Count(request => request.Invocation.RuntimePath.StartsWith(run.ToString("D") + "/", StringComparison.Ordinal)).Should().Be(2);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Review_ungrouped_repeat_names_cannot_alias_native_loop_state_across_regions()
    {
        const string source = """
            workflow demo(input: string) -> string {
              if first input == "same" {
                repeat shared max 1 state current: string = input {
                  activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: "first";) -> string;
                } continue echo break iter == 1 -> string;
              } else { }
              if second input == "same" {
                repeat shared max 1 state current: string = input {
                  activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: "second";) -> string;
                } continue echo break iter == 1 -> string;
              } else { }
              return input;
            }
            """;
        var ct = TestContext.Current.CancellationToken;
        var compiled = await new FuwenSourceCompiler(GroupCatalogue()).CompileAsync(source, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(value => value.Message)));
        var admission = await GroupAdmissionService().AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-review-loop-alias", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            var create = async () => await factory.CreateAsync("review", "1", admission, ct);
            await create.Should().ThrowAsync<FuwenZhinuAdmissionException>();
            Directory.EnumerateFiles(directory).Should().BeEmpty();
            activity.Requests.Should().BeEmpty();
        }
        finally { DeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Review_provider_identity_budget_is_checked_before_storage(bool dynamic, bool fits)
    {
        var source = new System.Text.StringBuilder("workflow demo(input: string) -> string {\n");
        var depth = dynamic ? 13 : 14;
        for (var index = 0; index < depth; index++)
            source.AppendLine("if " + ("c" + index).PadRight(!dynamic && index == 0 ? (fits ? 48 : 49) : 64, 'x') + " input == \"same\" {");
        if (dynamic) source.AppendLine("repeat " + "r".PadRight(fits ? 41 : 42, 'x') + " max 1 state current: string = input {");
        source.AppendLine("activity echo = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: input;) -> string;");
        if (dynamic) source.AppendLine("} continue echo break iter == 1 -> string;");
        for (var index = 0; index < depth; index++) source.AppendLine("} else { }");
        source.AppendLine("return input; }");
        var ct = TestContext.Current.CancellationToken;
        var compiled = await new FuwenSourceCompiler(GroupCatalogue()).CompileAsync(source.ToString(), cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(string.Join("; ", compiled.Diagnostics.Select(value => value.Message)));
        var admission = await GroupAdmissionService().AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue();
        var directory = Path.Combine(Path.GetTempPath(), "fuwen-review-invocation-budget", Guid.NewGuid().ToString("N"));
        try
        {
            var activity = new RecordingActivity();
            var factory = new FuwenZhinuWorkflowFactory(new DirectoryWorkflowDefinitionStore(directory), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(activity, new UnusedContext(), new UnusedInference()));
            if (!fits)
            {
                var create = async () => await factory.CreateAsync("review", "1", admission, ct);
                await create.Should().ThrowAsync<FuwenZhinuAdmissionException>();
                Directory.EnumerateFiles(directory).Should().BeEmpty();
                activity.Requests.Should().BeEmpty();
            }
            else
            {
                var registration = await factory.CreateAsync("review", "1", admission, ct);
                var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
                await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()));
                var id = await engine.StartAsync("review", "1", JsonSerializer.SerializeToElement("same"), cancellationToken: ct);
                await engine.ExecuteAsync(id, ct);
                (await engine.WaitForCompletionAsync<JsonElement>(id, cancellationToken: ct)).GetString().Should().Be("same");
                System.Text.Encoding.UTF8.GetByteCount(activity.Requests.Should().ContainSingle().Which.Invocation.RuntimePath)
                    .Should().Be(ExecutionInvocation.MaximumPathUtf8Bytes);
            }
        }
        finally { DeleteDirectory(directory); }
    }
}
