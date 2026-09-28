using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task One_call_inference_fallback_keeps_failed_provider_step_and_records_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = CreateContextInferencePlan();
        using var literal = JsonDocument.Parse("\"manual review\"");
        var plan = fixture.Plan with
        {
            IrVersion = FuwenContracts.InferenceFallbackIrVersion,
            Nodes = fixture.Plan.Nodes.Select(node => node is InferenceNode inference
                ? inference with
                {
                    FailureFallback = new InferenceFailureFallback(
                        [ExecutionFailureCode.SchemaMismatch],
                        new LiteralBinding(literal.RootElement.Clone())),
                }
                : node).ToArray(),
        };
        var catalogue = fixture.Descriptors.Select(descriptor =>
            new TrustedCatalogueDescriptor(descriptor.Reference, callableContract: descriptor.Contract)).ToArray();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
            new InMemoryTrustedCatalogue(catalogue),
            capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));
        var inference = new FailingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(), IdentityFor(admission),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(),
                    new RecordingContextProvider(fixture.ContextDescriptor, fixture.ArtifactDescriptor),
                    CurrentInferenceFixture.WithPreflight(inference)))
            .CreateAsync("fuwen.context", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var engine = new WorkflowEngine(
                new SqliteWorkflowStore(new ZhinuSqliteOptions
                {
                    DatabasePath = Path.Combine(root, "workflow.db"),
                    Pooling = false,
                }),
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            using var input = JsonDocument.Parse("\"question\"");
            var run = await engine.StartAsync("fuwen.context", "1", input.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            (await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct))
                .GetString().Should().Be("manual review");
            inference.Calls.Should().Be(1);
            var steps = await engine.GetStepsAsync(run, ct);
            steps.Should().Contain(step => step.StepKey == "context/infer" && step.Status == StepStatus.Completed);
            steps.Should().Contain(step => step.StepKey == "context/infer/$fallback" && step.Status == StepStatus.Completed);
            using var failedEnvelope = JsonDocument.Parse(
                steps.Single(step => step.StepKey == "context/infer").OutputJson!);
            failedEnvelope.RootElement.GetProperty("failure").GetProperty("code")
                .GetString().Should().Be("schemaMismatch");
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class FailingInference : IInferenceExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(InferenceExecutionResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.SchemaMismatch,
                "Structured output failed validation.")));
        }
    }
}
