using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenInferenceCoordinatorTests
{
    [Fact]
    public async Task Recovered_tool_batch_must_match_the_assistant_call_before_tool_io()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = CreatePlan(Limits(turns: 2, modelCalls: 2, toolCalls: 1), [Search]);
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
            ValueTask.FromResult<InferenceTurnResult>(new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("call-1", Search, "{\"q\":1}")], ExactUsage())));
        var tool = new BlockingReadTool();
        var registration = await RegisterAsync(plan, turns, tool, ct: ct);
        var root = NewRoot();
        try
        {
            var engine1 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            using var input = JsonDocument.Parse("\"q\"");
            var runId = await engine1.StartAsync("coord", "1", input.RootElement.Clone(), cancellationToken: ct);
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = engine1.ExecuteAsync(runId, interruption.Token);
            await tool.Entered.Task.WaitAsync(ct);
            await interruption.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) { }
            await engine1.DisposeAsync();

            var databasePath = Path.Combine(root, "workflow.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT step_key, output_json FROM workflow_steps WHERE workflow_run_id = $run AND output_json IS NOT NULL";
                read.Parameters.AddWithValue("$run", runId.ToString("D"));
                await using var reader = await read.ExecuteReaderAsync(ct);
                string? commitKey = null;
                string? changedOutput = null;
                while (await reader.ReadAsync(ct))
                {
                    var key = reader.GetString(0);
                    var output = reader.GetString(1);
                    if (!key.EndsWith("/commit", StringComparison.Ordinal) ||
                        !output.Contains("fuwen-inference-coordinator-state/v5-native-tool-history", StringComparison.Ordinal))
                        continue;
                    var parsed = JsonNode.Parse(output)!;
                    var state = FindToolPhaseState(parsed);
                    if (state is null)
                        continue;
                    AssertCoordinatorRecoveredState(state, plan, corrupt: false);
                    var pending = (JsonArray)GetProperty(state, "Pending")!;
                    var pendingCall = (JsonObject)pending[0]!;
                    var callIdKey = pendingCall.Select(pair => pair.Key).Single(key =>
                        string.Equals(key, "CallId", StringComparison.OrdinalIgnoreCase));
                    pendingCall[callIdKey] = "forged-call";
                    AssertCoordinatorRecoveredState(state, plan, corrupt: true);
                    commitKey = key;
                    changedOutput = parsed.ToJsonString();
                    break;
                }
                commitKey.Should().NotBeNull();
                await reader.DisposeAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE workflow_steps SET output_json = $output WHERE workflow_run_id = $run AND step_key = $step";
                update.Parameters.AddWithValue("$output", changedOutput!);
                update.Parameters.AddWithValue("$run", runId.ToString("D"));
                update.Parameters.AddWithValue("$step", commitKey!);
                (await update.ExecuteNonQueryAsync(ct)).Should().Be(1);
            }

            await Task.Delay(350, ct);
            await using var engine2 = CreateEngine(root, registration, TimeSpan.FromMilliseconds(150));
            await engine2.RunAvailableAsync(ct);
            var failed = (await engine2.GetRunAsync(runId, ct))!;
            failed.Status.Should().Be(WorkflowStatus.Failed);
            failed.Error!.Message.Should().Contain("incompatible input or result contract");
            turns.ObservedRequests.Should().ContainSingle();
            tool.Calls.Should().Be(1);
        }
        finally { DeleteDirectory(root); }
    }

    private static void AssertCoordinatorRecoveredState(JsonObject state, WorkflowPlan plan, bool corrupt)
    {
        var type = typeof(FuwenZhinuWorkflowFactory).Assembly.GetType(
            "Penghou.Fuwen.Zhinu.FuwenInferenceCoordinator", throwOnError: true)!;
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        using var document = JsonDocument.Parse(state.ToJsonString());
        var recovered = type.GetMethod("ReadCoordinatorState", flags)!.Invoke(
            null, [document.RootElement.Clone(), "coord/infer"]);
        var infer = plan.Nodes.OfType<InferenceNode>().Single();
        var effective = type.GetMethod("EffectiveLimits", flags)!.Invoke(
            null, [infer.Protocol!.Limits, null]);
        var failure = (ExecutionFailure?)type.GetMethod("ValidateRecoveredState", flags)!.Invoke(
            null, [recovered, infer.Tools, effective]);
        if (!corrupt)
        {
            failure.Should().BeNull();
            return;
        }
        failure.Should().NotBeNull();
        failure!.Code.Should().Be(ExecutionFailureCode.InvalidInput);
        failure.Message.Should().Contain("incompatible phase or operation fields");
    }

    private static JsonObject? FindToolPhaseState(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (GetProperty(obj, "Semantics")?.ToString() ==
                "fuwen-inference-coordinator-state/v5-native-tool-history" &&
                GetProperty(obj, "Phase")?.ToString() == "tool")
                return obj;
            foreach (var child in obj)
                if (FindToolPhaseState(child.Value) is { } found)
                    return found;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                if (FindToolPhaseState(child) is { } found)
                    return found;
        }
        return null;
    }

    private static JsonNode? GetProperty(JsonObject obj, string name) =>
        obj.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class BlockingReadTool : IInferenceReadToolExecutor
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);

        public async ValueTask<InferenceReadToolResult> ExecuteAsync(
            InferenceReadToolRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The interrupted tool should not complete.");
        }
    }
}
