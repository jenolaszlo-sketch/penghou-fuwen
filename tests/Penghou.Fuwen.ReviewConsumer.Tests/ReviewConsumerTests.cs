using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.ReviewConsumer.Tests;

// Second, product-neutral consumer: a documentation-review host built only on
// published Fuwen packages. It reuses the shipped deterministic turn fixture
// as its model and implements its own read tools, catalogue, and evidence
// sink, proving Fuwen carries no Marang-specific semantics.
public sealed class ReviewConsumerTests
{
    private static ContentDigest Digest(char value) =>
        new("sha256", "descriptor/v1", new string(value, 64));

    private static DescriptorReference Profile { get; } =
        new(DescriptorKind.InferenceProfile, "review.reasoning", "1", Digest('a'));
    private static DescriptorReference Read { get; } =
        new(DescriptorKind.Tool, "review.read", "1", Digest('b'));
    private static DescriptorReference Checklist { get; } =
        new(DescriptorKind.Tool, "review.checklist", "1", Digest('c'));

    private static string Pin(DescriptorReference descriptor) =>
        $"{descriptor.Name}@{descriptor.Version}#{descriptor.ContentDigest.Value}";

    // Fuwen prompt placeholder as data: a plain string needs no brace escaping.
    private const string Placeholder = "{{ target }}";

    private static string Source => $$$"""
        prompt review_doc(target: string) {
          system "You review documents against a checklist."
          user "Review {{{Placeholder}}}."
        }

        toolset review_tools {
          use "{{{Pin(Read)}}}";
          use "{{{Pin(Checklist)}}}";
        }

        workflow doc_review(target: string) -> string {
          infer review = infer "{{{Pin(Profile)}}}" prompt review_doc(target: input;) tools review_tools limits maxTokens 200 timeout 30 aggregate turns 2 modelCalls 2 toolCalls 2 totalTokens 4000 durationMs 60000 -> string;
          return review;
        }
        """;

    private static InMemoryTrustedCatalogue Catalogue()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        static CallableContract Simple(FuwenType output) => new(
            new CallableSignature([new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))], output),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        return new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(Profile, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(Read, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(Checklist, callableContract: Simple(text)),
        ]);
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(CancellationToken ct)
    {
        var compiled = await new FuwenSourceCompiler(Catalogue()).CompileAsync(Source, cancellationToken: ct);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                Catalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan!, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        return admission;
    }

    private static JsonElement Input(string target)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(target));
        return document.RootElement.Clone();
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-review", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement.Clone());
    }

    [Fact]
    public async Task Compiles_admits_runs_and_returns_a_typed_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var turns = new DeterministicFakeTurnExecutor(
        [
            new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal("read-1", Read, """{"section":"intro"}""")],
                new InferenceTurnUsage(200, 30, 230)),
            new InferenceFinalCandidateResult("\"Reads clearly; checklist passes.\"", new InferenceTurnUsage(260, 40, 300)),
        ]);
        var tools = new ReviewReadTools();
        var sink = new ReviewEvidenceSink();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(), new UnusedInference(),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns, tools,
                    evidenceSink: sink))
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
            var runId = await engine.StartAsync("doc_review", "1", Input("intro.md"), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("Reads clearly; checklist passes.");
            tools.Requests.Should().ContainSingle()
                .Which.Tool.Should().Be(Read);

            var evidence = sink.Items.Should().ContainSingle().Subject;
            evidence.Failure.Should().BeNull();
            evidence.Operations.Count(operation => operation.Kind == InferenceOperationKind.ModelTurn).Should().Be(2);
            evidence.ToolOutcomes.Should().ContainSingle();
            evidence.TotalTokens.Should().Be(230 + 300);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Undeclared_tool_is_rejected_before_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var unknown = new DescriptorReference(
            DescriptorKind.Tool, "review.evil", "1", new ContentDigest("sha256", "descriptor/v1", new string('7', 64)));
        var turns = new DeterministicFakeTurnExecutor(
        [
            new InferenceToolCallTurnResult([new InferenceToolCallProposal("evil-1", unknown, "{}")]),
        ]);
        var tools = new ReviewReadTools();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(
                    new UnusedActivity(), new UnusedContext(), new UnusedInference(),
                    observer: null, new FuwenZhinuExecutionPorts.Options(), turns, tools))
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
            var runId = await engine.StartAsync("doc_review", "1", Input("intro.md"), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);

            (await engine.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Failed);
            tools.Requests.Should().BeEmpty();
        }
        finally { DeleteDirectory(root); }
    }

    private sealed class ReviewReadTools : IInferenceReadToolExecutor
    {
        public List<InferenceReadToolRequest> Requests { get; } = [];

        public ValueTask<InferenceReadToolResult> ExecuteAsync(
            InferenceReadToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            var result = request.Tool.Name switch
            {
                "review.read" => Json("""{"section":"intro","text":"Clear prose."}"""),
                "review.checklist" => Json("""{"items":["clarity"],"passed":true}"""),
                _ => throw new InvalidOperationException($"Undeclared review tool '{request.Tool.Name}'."),
            };
            return ValueTask.FromResult(InferenceReadToolResult.Succeeded(
                result, new InferenceReadToolEvidence(request.Tool, request.OperationKey)));
        }
    }

    private sealed class ReviewEvidenceSink : IInferenceEvidenceSink
    {
        public List<InferenceProtocolEvidence> Items { get; } = [];

        public ValueTask RecordAsync(InferenceProtocolEvidence evidence, CancellationToken cancellationToken = default)
        {
            Items.Add(evidence ?? throw new ArgumentNullException(nameof(evidence)));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No activities in the review workflow.");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No context in the review workflow.");
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Review coordinated inference never uses the one-call executor.");
    }
}
