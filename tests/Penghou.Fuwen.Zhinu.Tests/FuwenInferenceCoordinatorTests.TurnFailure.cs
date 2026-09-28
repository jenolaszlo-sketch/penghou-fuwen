using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenInferenceCoordinatorTests
{
    [Theory]
    [InlineData(ExecutionFailureCode.NotAdmitted, false)]
    [InlineData(ExecutionFailureCode.ProviderError, true)]
    public async Task Typed_turn_failure_preserves_code_and_commitment_without_a_second_call(
        ExecutionFailureCode code, bool mayHaveCommitted)
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var turns = DeterministicFakeTurnExecutor.FromResponder((_, _) =>
        {
            calls++;
            throw new InferenceTurnFailureException(new ExecutionFailure(
                mayHaveCommitted ? ExecutionFailureKind.Provider : ExecutionFailureKind.Admission,
                code,
                "Selected turn failed.",
                mayHaveCommittedEffect: mayHaveCommitted));
        });
        var sink = new RecordingEvidenceSink();
        var registration = await RegisterAsync(CreatePlan(Limits(2, 2)), turns, evidenceSink: sink, ct: ct);
        var root = NewRoot();
        try
        {
            await using var engine = CreateEngine(root, registration);
            var run = await engine.StartAsync("coord", "1", JsonSerializer.SerializeToElement("q"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);

            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            calls.Should().Be(1);
            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure!.Code.Should().Be(code);
            evidence.Failure.MayHaveCommittedEffect.Should().Be(mayHaveCommitted);
        }
        finally { DeleteDirectory(root); }
    }
}
