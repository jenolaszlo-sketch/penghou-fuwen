using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen.Compiler;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Fuwen.Zhinu.Tests;

/// <summary>
/// R29 execution wiring: a v8 workflow-owned prompt inference executes
/// end to end on the sequential adapter. The executor receives the resolved
/// definition with deterministically rendered messages, and prompt semantics
/// participate in the request identity so reuse stays input-sensitive.
/// </summary>
public sealed class FuwenZhinuPromptExecutionTests
{
    private static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));

    private static PromptDefinition Greet() => new(
        "greet",
        [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
        [
            new PromptMessage(PromptMessageRole.System, "You greet users concisely."),
            new PromptMessage(PromptMessageRole.User, "Greet {{ name }}."),
        ]);

    private static WorkflowPlan CreatePlan()
    {
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a'));
        var greetPath = StructuralNodeIdentity.Create("greet", "greet");
        var returnPath = StructuralNodeIdentity.Create("greet", "return_result");
        var stringType = new PrimitiveType(FuwenPrimitiveKind.String);
        return new WorkflowPlanBuilder("greet", "1", stringType, stringType, "routing/1")
            .AddCatalogueBinding(profile)
            .AddPrompt(Greet())
            .AddNode(new InferenceNode(
                "greet",
                greetPath,
                profile,
                null,
                [],
                [],
                stringType,
                [],
                "greet",
                [new PromptBinding("name", new InputBinding([]))]))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(greetPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("greet", [
                    new WorkflowExecutionPhase([greetPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .BuildV8();
    }

    private static ITrustedCatalogue Catalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
    }

    private static async Task<WorkflowAdmissionResult> AdmitAsync(CancellationToken ct)
    {
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                Catalogue(),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(CreatePlan(), cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        return admission;
    }

    [Fact]
    public async Task Prompt_inference_executes_with_rendered_messages_and_prompt_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var inference = new CapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("greet", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var inputDocument = JsonDocument.Parse("\"Alice\"");
            var runId = await engine.StartAsync(
                "greet", "1", inputDocument.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("Hello, Alice.");
            var request = inference.Requests.Should().ContainSingle().Subject;
            request.PromptTemplate.Should().BeNull();
            request.Prompt!.Name.Should().Be("greet");
            request.RenderedPrompt!.Select(message => message.Text).Should().Equal(
                "You greet users concisely.", "Greet Alice.");
            request.RenderedPrompt!.Select(message => message.Role).Should().Equal(
                PromptMessageRole.System, PromptMessageRole.User);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Declared_tools_flow_into_the_request_and_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('b'));
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var greetPath = StructuralNodeIdentity.Create("greet", "greet");
        var returnPath = StructuralNodeIdentity.Create("greet", "return_result");
        var plan = new WorkflowPlanBuilder("greet", "1", str, str, "routing/1")
            .AddCatalogueBinding(new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a')))
            .AddCatalogueBinding(tool)
            .AddPrompt(Greet())
            .AddNode(new InferenceNode(
                "greet",
                greetPath,
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a')),
                null,
                [],
                [],
                str,
                [],
                "greet",
                [new PromptBinding("name", new InputBinding([]))],
                [tool]))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(greetPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("greet", [
                    new WorkflowExecutionPhase([greetPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .BuildV8();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(
                        new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a')),
                        callableContract: new CallableContract(
                            new CallableSignature([new CallableParameter("request", str)], str),
                            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                    new TrustedCatalogueDescriptor(
                        tool,
                        callableContract: new CallableContract(
                            new CallableSignature([new CallableParameter("query", str)], str),
                            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));

        var inference = new CapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("greet", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var inputDocument = JsonDocument.Parse("\"Alice\"");
            var runId = await engine.StartAsync(
                "greet", "1", inputDocument.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            var request = inference.Requests.Should().ContainSingle().Subject;
            request.Tools.Should().ContainSingle().Which.Should().Be(tool);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Prompt_request_identity_is_input_sensitive_across_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var inference = new CapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("greet", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            foreach (var name in new[] { "Alice", "Bob" })
            {
                using var inputDocument = JsonDocument.Parse(JsonSerializer.Serialize(name));
                var runId = await engine.StartAsync(
                    "greet", "1", inputDocument.RootElement.Clone(), cancellationToken: ct);
                await engine.ExecuteAsync(runId, ct);
                await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
            }

            inference.Requests.Should().HaveCount(2);
            inference.Requests[0].Invocation.EffectiveRequestFingerprint
                .Should().NotBe(inference.Requests[1].Invocation.EffectiveRequestFingerprint);
            inference.Requests[1].RenderedPrompt!.Last().Text.Should().Be("Greet Bob.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static WorkflowEngine CreateEngine(
        string root,
        FuwenZhinuWorkflowRegistration registration) =>
        new(
            new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
                BusyTimeout = TimeSpan.FromSeconds(2),
            }),
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(5),
            });

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private sealed class CapturingInference : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var name = request.RenderedPrompt!
                .First(message => message.Role == PromptMessageRole.User).Text;
            using var document = JsonDocument.Parse(JsonSerializer.Serialize("Hello, " + name
                .Replace("Greet ", string.Empty, StringComparison.Ordinal)
                .TrimEnd('.') + "."));
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement)));
        }
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(
            ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity executor should not be called.");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(
            ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context provider should not be called.");
    }
}
