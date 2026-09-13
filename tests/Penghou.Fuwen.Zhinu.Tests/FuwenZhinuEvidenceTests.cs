using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Sqlite_retains_inference_evidence_when_success_processing_fails()
    {
        var fixture = await AdmitVerticalAsync();
        var inference = new SuccessfulInferenceWithInvalidOutput(fixture.Profile, fixture.PromptTemplate);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    inference))
            .CreateAsync("fuwen.vertical", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "workflow.db");

        try
        {
            await using var engine = CreateEngine(CreateStore(databasePath), registration);
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync(
                "fuwen.vertical", "1", input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

            (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
                .Status.Should().Be(WorkflowStatus.Failed);
            inference.Requests.Should().ContainSingle();

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT output_json FROM workflow_steps WHERE workflow_run_id = $run AND step_key = $step";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            command.Parameters.AddWithValue("$step", "vertical/infer");
            var persisted = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            using var envelope = JsonDocument.Parse(persisted!);
            envelope.RootElement.GetProperty("failure").GetProperty("code").GetString()
                .Should().Be("bindingFailure");
            var evidence = envelope.RootElement.GetProperty("evidence");
            evidence.GetProperty("profile").GetProperty("name").GetString()
                .Should().Be(fixture.Profile.Name);
            evidence.GetProperty("promptTemplate").GetProperty("name").GetString()
                .Should().Be(fixture.PromptTemplate.Name);
            evidence.GetProperty("attempts").GetArrayLength().Should().Be(1);
            evidence.GetProperty("attempts")[0].GetProperty("succeeded").GetBoolean()
                .Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private sealed class SuccessfulInferenceWithInvalidOutput(
        DescriptorReference profile,
        DescriptorReference promptTemplate) : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var evidence = new InferenceExecutionEvidence(
                profile,
                promptTemplate,
                [new InferenceAttemptEvidence(
                    1, "provider", "model", "endpoint", succeeded: true, promptTokens: 3,
                    completionTokens: 2, totalTokens: 5)]);
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(JsonSerializer.SerializeToElement("invalid")),
                evidence: evidence));
        }
    }
}
