using System.Text.Json;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.MarangConsumer.Tests;

// Minimal Marang-style host adapter written only against published Fuwen
// surfaces. It owns provider/model transport for one turn, exact read-tool
// execution over an in-memory repository snapshot, workspace context, the
// explicit promotion activity, and preflight manifests. It contains no
// workflow or protocol semantics: those stay in the admitted Fuwen plan and
// the Zhinu coordinator.
internal static class MarangDescriptors
{
    private static ContentDigest Digest(char value) =>
        new("sha256", "descriptor/v1", new string(value, 64));

    public static DescriptorReference Profile { get; } =
        new(DescriptorKind.InferenceProfile, "marang.reasoning", "1", Digest('a'));
    public static DescriptorReference PlanningSchema { get; } =
        new(DescriptorKind.Schema, "marang.planning", "1", Digest('9'));
    public static DescriptorReference Search { get; } =
        new(DescriptorKind.Tool, "marang.repo.search", "1", Digest('b'));
    public static DescriptorReference ReadFile { get; } =
        new(DescriptorKind.Tool, "marang.repo.readFile", "1", Digest('c'));
    public static DescriptorReference Impact { get; } =
        new(DescriptorKind.Tool, "marang.graph.impact", "1", Digest('d'));
    public static DescriptorReference Workspace { get; } =
        new(DescriptorKind.ContextProvider, "marang.workspace", "1", Digest('e'));
    public static DescriptorReference Promote { get; } =
        new(DescriptorKind.Activity, "marang.promote", "1", Digest('f'));

    public static string Pin(DescriptorReference descriptor) =>
        $"{descriptor.Name}@{descriptor.Version}#{descriptor.ContentDigest.Value}";
}

internal static class MarangScenario
{
    public const string WorkflowName = "marang_planning";
    public const string Objective = "Prepare a bounded implementation plan for the requested change.";

    public static string Source => $$$"""
        schema PlanningResult "{{{MarangDescriptors.Pin(MarangDescriptors.PlanningSchema)}}}" {
          summary: string;
          affectedPaths: list<string>[10];
          verification: list<string>[10];
          proposedMutation: string;
          requiresExplicitActivity: bool;
        }

        prompt marang_planning(objective: string) {
          system "You are Marang planning supervision. Produce a bounded implementation plan as PlanningResult."
          user "Objective: {{ objective }}."
        }

        toolset marang_read_tools {
          use "{{{MarangDescriptors.Pin(MarangDescriptors.Search)}}}";
          use "{{{MarangDescriptors.Pin(MarangDescriptors.ReadFile)}}}";
          use "{{{MarangDescriptors.Pin(MarangDescriptors.Impact)}}}";
        }

        workflow marang_planning(objective: string) -> PlanningResult {
          context workspace = context "{{{MarangDescriptors.Pin(MarangDescriptors.Workspace)}}}" (revision: "main@scenario-fixed-revision";) -> json;
          infer planning = infer "{{{MarangDescriptors.Pin(MarangDescriptors.Profile)}}}" prompt marang_planning(objective: input;) tools marang_read_tools with workspace limits maxTokens 800 timeout 30 aggregate turns 3 modelCalls 3 toolCalls 4 promptTokens 12000 completionTokens 3000 totalTokens 15000 durationMs 90000 toolArgumentBytes 24000 toolResultBytes 24000 -> PlanningResult;
          activity promote = activity "{{{MarangDescriptors.Pin(MarangDescriptors.Promote)}}}" (proposal: planning.proposedMutation; approved: planning.requiresExplicitActivity;) -> string;
          return planning;
        }
        """;

    public static InMemoryTrustedCatalogue Catalogue()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var boolean = new PrimitiveType(FuwenPrimitiveKind.Boolean);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        static CallableContract Simple(FuwenType output) => new(
            new CallableSignature([new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))], output),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        return new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(
                MarangDescriptors.PlanningSchema,
                new ObjectSchemaDefinition(
                    MarangDescriptors.PlanningSchema,
                    [
                        new SchemaField("summary", text),
                        new SchemaField("affectedPaths", new ListType(text, 10)),
                        new SchemaField("verification", new ListType(text, 10)),
                        new SchemaField("proposedMutation", text),
                        new SchemaField("requiresExplicitActivity", boolean),
                    ])),
            new TrustedCatalogueDescriptor(MarangDescriptors.Profile, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(MarangDescriptors.Search, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(MarangDescriptors.ReadFile, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(MarangDescriptors.Impact, callableContract: Simple(text)),
            new TrustedCatalogueDescriptor(
                MarangDescriptors.Workspace,
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("revision", text)], json),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                MarangDescriptors.Promote,
                callableContract: new CallableContract(
                    new CallableSignature(
                    [
                        new CallableParameter("proposal", text),
                        new CallableParameter("approved", boolean),
                    ], text),
                    CallableEffect.Write, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
    }

    // Marang host ceilings from the scenario table; the authored source bounds
    // below must win every dimension as the component-wise minimum.
    public static InferenceLimitSet HostCeilings() => new(
    [
        new InferenceLimit(InferenceLimitDimension.Turns, 4),
        new InferenceLimit(InferenceLimitDimension.ModelCalls, 4),
        new InferenceLimit(InferenceLimitDimension.ToolCalls, 6),
        new InferenceLimit(InferenceLimitDimension.TotalTokens, 18000),
        new InferenceLimit(InferenceLimitDimension.DurationMilliseconds, 120000),
        new InferenceLimit(InferenceLimitDimension.ToolArgumentBytes, 32000),
        new InferenceLimit(InferenceLimitDimension.ToolResultBytes, 32000),
    ]);
}

// Deterministic scripted model transport for the Marang planning trace:
// search, then read, then a typed PlanningResult. Test-controlled gates allow
// crash injection without touching any other host behavior.
internal sealed class MarangPlanningTurns : IInferenceTurnExecutor, IInferenceTurnExecutorManifest
{
    private readonly InferenceFeatureManifest manifest;
    private readonly Func<InferenceTurnRequest, CancellationToken, ValueTask<InferenceTurnResult>> behavior;

    public MarangPlanningTurns(
        InferenceFeatureManifest manifest,
        Func<InferenceTurnRequest, CancellationToken, ValueTask<InferenceTurnResult>> behavior)
    {
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
    }

    public List<InferenceTurnRequest> Requests { get; } = [];
    public InferenceFeatureManifest TurnFeatureManifest => manifest;

    public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement) =>
        InferencePreflight.Evaluate(requirement ?? throw new ArgumentNullException(nameof(requirement)), manifest);

    public async ValueTask<InferenceTurnResult> ExecuteTurnAsync(
        InferenceTurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);
        return await behavior(request, cancellationToken).ConfigureAwait(false);
    }

    public static MarangPlanningTurns SuccessTrace(InferenceFeatureManifest manifest) =>
        new(manifest, (request, _) => ValueTask.FromResult<InferenceTurnResult>(request.TurnOrdinal switch
        {
            0 => new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal(
                    "model-call-1", MarangDescriptors.Search,
                    """{"query":"InferenceExecutionRequest","pathPrefix":"src","maxResults":10}""")],
                new InferenceTurnUsage(1200, 60, 1260)),
            1 => new InferenceToolCallTurnResult(
                [new InferenceToolCallProposal(
                    "model-call-2", MarangDescriptors.ReadFile,
                    """{"path":"src/Penghou.Fuwen/ExecutionPorts.cs","startLine":1,"endLine":80}""")],
                new InferenceTurnUsage(1400, 70, 1470)),
            _ => new InferenceFinalCandidateResult(
                """{"summary":"Add bounded planning with explicit promotion.","affectedPaths":["src/Penghou.Fuwen/ExecutionPorts.cs"],"verification":["dotnet test Penghou.Fuwen.slnx -c Release"],"proposedMutation":"update the implementation plan documentation","requiresExplicitActivity":true}""",
                new InferenceTurnUsage(1600, 120, 1720)),
        }));
}

// Exact read-tool execution over a fixed in-memory repository snapshot. Every
// call is recorded under its stable operation key; failures are injected
// explicitly per test. Results are bounded typed data, never policy.
internal sealed class MarangRepositoryTools : IInferenceReadToolExecutor
{
    private readonly Func<InferenceReadToolRequest, InferenceReadToolResult?>? overrideBehavior;

    public MarangRepositoryTools(Func<InferenceReadToolRequest, InferenceReadToolResult?>? overrideBehavior = null)
    {
        this.overrideBehavior = overrideBehavior;
    }

    public List<InferenceReadToolRequest> Requests { get; } = [];

    public ValueTask<InferenceReadToolResult> ExecuteAsync(
        InferenceReadToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (overrideBehavior?.Invoke(request) is { } overridden)
            return ValueTask.FromResult(overridden);

        // Host authorization: the scope must name this exact tool, and the
        // result must fit the caller-declared ceiling before it is returned.
        var expectedScope = $"{request.Tool.Name}@{request.Tool.Version}";
        if (!string.Equals(request.Scope, expectedScope, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(InferenceReadToolResult.Failed(new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.PolicyRejected,
                $"Tool '{request.Tool.Name}' is not authorized for scope '{request.Scope}'.")));
        }

        var arguments = RuntimeValueJson.ToJsonElement(request.Arguments);
        var result = request.Tool.Name switch
        {
            "marang.repo.search" => Json(
                """{"matches":[{"path":"src/Penghou.Fuwen/ExecutionPorts.cs","line":1,"preview":"public sealed class InferenceExecutionRequest"}],"truncated":false}"""),
            "marang.repo.readFile" => Json(
                """{"path":"src/Penghou.Fuwen/ExecutionPorts.cs","revision":"main@scenario-fixed-revision","content":"public sealed class InferenceExecutionRequest","startLine":1,"endLine":80,"truncated":false}"""),
            "marang.graph.impact" => Json("""{"nodes":[],"truncated":false}"""),
            _ => throw new InvalidOperationException($"Undeclared Marang tool '{request.Tool.Name}'."),
        };
        if (request.MaximumResultUtf8Bytes is int ceiling)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(
                System.Text.Json.JsonSerializer.Serialize(RuntimeValueJson.ToJsonElement(result)));
            if (bytes > ceiling)
            {
                return ValueTask.FromResult(InferenceReadToolResult.Failed(new ExecutionFailure(
                    ExecutionFailureKind.Contract,
                    ExecutionFailureCode.PayloadLimitExceeded,
                    $"Tool '{request.Tool.Name}' result exceeds the {ceiling}-byte ceiling.")));
            }
        }
        return ValueTask.FromResult(InferenceReadToolResult.Succeeded(
            result, new InferenceReadToolEvidence(request.Tool, request.OperationKey)));
    }

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement.Clone());
    }
}

// Fixed workspace snapshot for the Marang scenario revision.
internal sealed class MarangWorkspaceProvider : IContextProvider
{
    public List<ContextExecutionRequest> Requests { get; } = [];

    public ValueTask<ContextExecutionResult> ExecuteAsync(
        ContextExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);
        using var document = JsonDocument.Parse("""{"revision":"main@scenario-fixed-revision","root":"src"}""");
        var snapshot = new ContextSnapshotReference(
            MarangDescriptors.Workspace,
            "marang-snapshot-1",
            new ContentDigest("sha256", "request/v1", new string('1', 64)),
            new ContentDigest("sha256", "content/v1", new string('2', 64)),
            [],
            "marang-policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        return ValueTask.FromResult(ContextExecutionResult.Succeeded(
            RuntimeValue.FromJson(document.RootElement.Clone()), snapshot));
    }
}

// The only writer in the Marang proof. It runs as an explicit authorized
// workflow activity after inference completes; the model can only propose.
internal sealed class MarangPromotionActivity : IActivityExecutor
{
    public List<(string Proposal, bool Approved)> Committed { get; } = [];

    public ValueTask<ActivityExecutionResult> ExecuteAsync(
        ActivityExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = RuntimeValueJson.ToJsonElement(request.Arguments.Single(a => a.Name == "proposal").Value);
        var approved = RuntimeValueJson.ToJsonElement(request.Arguments.Single(a => a.Name == "approved").Value);
        var proposal = arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString()!
            : arguments.GetRawText();
        Committed.Add((proposal, approved.ValueKind == JsonValueKind.True));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize($"promoted:{proposal}"));
        return ValueTask.FromResult(ActivityExecutionResult.Succeeded(
            RuntimeValue.FromJson(document.RootElement.Clone())));
    }
}

// The one-call executor exists only because registration carries one; the
// coordinated Marang path never invokes it.
internal sealed class MarangUnusedInference : IInferenceExecutor
{
    public ValueTask<InferenceExecutionResult> ExecuteAsync(
        InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Marang coordinated inference never uses the one-call executor.");
}

internal sealed class MarangEvidenceSink : IInferenceEvidenceSink
{
    public List<InferenceProtocolEvidence> Items { get; } = [];

    public ValueTask RecordAsync(InferenceProtocolEvidence evidence, CancellationToken cancellationToken = default)
    {
        Items.Add(evidence ?? throw new ArgumentNullException(nameof(evidence)));
        return ValueTask.CompletedTask;
    }
}
