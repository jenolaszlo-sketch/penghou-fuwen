using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Fuwen.Baize;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

public sealed partial class ReviewConsumerTests
{
    [Fact]
    public async Task Stock_Baize_turn_executor_runs_model_tool_model_through_public_packages()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var plan = admission.Compilation.Plan!;
        var prompt = plan.Prompts!.Single(item => item.Name == "review_doc");
        var readContract = ModelFacingToolContractBuilder.Build(Read, admission.TrustedToolSignatures[Read], plan.Schemas);
        var checklistContract = ModelFacingToolContractBuilder.Build(Checklist, admission.TrustedToolSignatures[Checklist], plan.Schemas);
        var client = new ScriptedBaizeClient(
            new LlmResponse(string.Empty,
                ToolCalls: [new LlmToolCall("provider-read-1", Read.Name, """{"section":"intro"}""")],
                Usage: new LlmUsage(200, 30, 230)),
            new LlmResponse("\"Reads clearly; checklist passes.\"", Usage: new LlmUsage(260, 40, 300)));
        var binding = new BaizeInferenceBinding(
            Profile,
            promptTemplate: null,
            endpoints: [new BaizeEndpointBinding("primary", "fixture", "review-model", client)],
            tools:
            [
                new BaizeToolBinding(Read, new LlmTool(Read.Name, "Read the requested section", readContract.ParametersSchemaJson)),
                new BaizeToolBinding(Checklist, new LlmTool(Checklist.Name, "Check a document", checklistContract.ParametersSchemaJson)),
            ],
            prompt: prompt);
        var turns = new BaizeInferenceTurnExecutor(binding);
        var tools = new ReviewReadTools();
        var sink = new ReviewEvidenceSink();
        var report = turns.PreflightTurnDetailed(new InferenceExecutionRequirement(
            Profile, promptTemplate: null, promptDigest: prompt.GetSemanticDigest(),
            tools: [Read, Checklist], hasContextInputs: false,
            modality: InferenceModality.StructuredText,
            toolRequirements:
            [
                new InferenceToolRequirement(Read, modelContract: readContract),
                new InferenceToolRequirement(Checklist, modelContract: checklistContract),
            ]));
        report.IsExecutable.Should().BeTrue(InferencePreflightReportRenderer.RenderHuman(report));

        var registration = await new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(
                new UnusedActivity(), new UnusedContext(), new UnusedInference(),
                observer: null, new FuwenZhinuExecutionPorts.Options(), turns, tools,
                inferenceHostCeilings: null, evidenceSink: sink,
                protectedPayloadStore: new InMemoryProtectedPayloadStore()))
            .CreateAsync("doc_review", "1", admission, ct);
        var root = NewRoot();
        try
        {
            await using var engine = new WorkflowEngine(
                new SqliteWorkflowStore(new ZhinuSqliteOptions
                {
                    DatabasePath = Path.Combine(root, "workflow.db"),
                    Pooling = false,
                    BusyTimeout = TimeSpan.FromSeconds(2),
                }),
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            var run = await engine.StartAsync("doc_review", "1", Input("intro.md"), cancellationToken: ct);
            await engine.ExecuteAsync(run, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(run, cancellationToken: ct);

            output.GetString().Should().Be("Reads clearly; checklist passes.");
            client.Requests.Should().HaveCount(2);
            client.Requests[0].ResponseFormat.Should().NotBeNull();
            var toolResult = client.Requests[1].Messages
                .SelectMany(message => message.Parts).OfType<LlmToolResultContent>()
                .Should().ContainSingle().Subject;
            toolResult.Result.ToolCallId.Should().Be("provider-read-1");
            toolResult.Result.ToolName.Should().Be("review.read");
            tools.Requests.Should().ContainSingle();
            sink.Items.Should().ContainSingle().Which.Failure.Should().BeNull();

            var submittedCalls = client.Requests.Count;
            (await engine.GetRunAsync(run, ct))!.Status.Should().Be(WorkflowStatus.Completed);
            (await engine.GetStepsAsync(run, ct)).Should().NotBeEmpty();
            (await engine.GetEventsAsync(run, afterSequence: 0, limit: 10, cancellationToken: ct))
                .Count.Should().BeLessThanOrEqualTo(10);
            client.Requests.Should().HaveCount(submittedCalls);
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class ScriptedBaizeClient(params LlmResponse[] responses) : ILlmClient, ILlmCompletionClient
    {
        private readonly Queue<LlmResponse> responses = new(responses);
        public List<LlmRequest> Requests { get; } = [];
        public LlmEndpointCapabilities Capabilities { get; } = new()
        {
            NativeStructuredOutput = true,
            StructuredOutputViaTool = true,
            NativeToolCalling = true,
            ToolsWithStructuredOutput = true,
            StrictToolArguments = true,
        };

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(responses.Dequeue());
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamEvent(string.Empty);
            await Task.CompletedTask;
        }
    }
}
