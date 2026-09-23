using System.Text.Json;
using FluentAssertions;
using Penghou.Baize;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Baize;
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
                [new PromptBinding("name", new InputBinding([]))],
                Protocol: CurrentInferenceFixture.OneCallProtocol))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(greetPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("greet", [
                    new WorkflowExecutionPhase([greetPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
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
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), CurrentInferenceFixture.WithPreflight(inference)))
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
                [tool],
                Protocol: CurrentInferenceFixture.OneCallProtocol))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(greetPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("greet", [
                    new WorkflowExecutionPhase([greetPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
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
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), CurrentInferenceFixture.WithPreflight(inference)))
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
    public async Task V8_registered_template_tools_none_remains_explicit_at_execution_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('a'));
        var template = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.template", "1", Digest('b'));
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var inferPath = StructuralNodeIdentity.Create("template", "infer");
        var returnPath = StructuralNodeIdentity.Create("template", "return_result");
        var plan = new WorkflowPlanBuilder("template", "1", str, str, "routing/1")
            .AddCatalogueBinding(profile)
            .AddCatalogueBinding(template)
            .AddNode(new InferenceNode(
                "infer", inferPath, profile, template, [], [], str, [],
                Protocol: CurrentInferenceFixture.OneCallProtocol))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("template", [
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                profile,
                callableContract: new CallableContract(
                    new CallableSignature([], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(template),
        ]);
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        var inference = new TemplateCapturingInference();
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), CurrentInferenceFixture.WithPreflight(inference)))
            .CreateAsync("template", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var inputDocument = JsonDocument.Parse("\"input\"");
            var runId = await engine.StartAsync(
                "template", "1", inputDocument.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            var request = inference.Requests.Should().ContainSingle().Subject;
            request.PromptTemplate.Should().Be(template);
            request.Tools.Should().NotBeNull().And.BeEmpty();
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
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), CurrentInferenceFixture.WithPreflight(inference)))
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

    [Fact]
    public async Task Registered_prompt_alias_executes_end_to_end_through_exact_Baize_binding()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, catalogue, profile, template) = await AdmitRegisteredAliasAsync(ct);
        var client = new AliasLlmClient();
        var evidence = new CapturingProvenanceSink();
        var inference = new BaizeInferenceExecutor(
            [new BaizeInferenceBinding(
                profile,
                template,
                [new BaizeEndpointBinding("alias-endpoint", "alias-provider", "alias-model", client)],
                userPromptTemplate: "Registered template arguments: {arguments}")],
            provenanceSink: evidence);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    catalogue.SnapshotRevision,
                    admission.Receipt!.ResolvedDescriptorSetFingerprint),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), CurrentInferenceFixture.WithPreflight(inference)))
            .CreateAsync("alias", "1", admission, ct);
        var root = Path.Combine(Path.GetTempPath(), "penghou-fuwen-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var engine = CreateEngine(root, registration);
            using var inputDocument = JsonDocument.Parse("\"Alice\"");
            var runId = await engine.StartAsync(
                "alias", "1", inputDocument.RootElement.Clone(), cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);

            output.GetString().Should().Be("Hello, Alice.");
            client.Requests.Should().ContainSingle();
            client.Requests[0].Messages.Should().ContainSingle()
                .Which.Parts.Should().ContainSingle()
                .Which.Should().BeOfType<LlmTextContent>()
                .Which.Text.Should().Contain("\"name\":\"Alice\"");
            evidence.Items.Should().ContainSingle()
                .Which.PromptTemplate.Should().Be(template);
            evidence.Items[0].PromptDigest.Should().BeNull();
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Registered_prompt_alias_with_changed_source_is_rejected_before_storage_or_provider_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, catalogue, profile, _) = await AdmitRegisteredAliasAsync(ct);
        var changedTemplate = new DescriptorReference(
            DescriptorKind.PromptTemplate,
            "sample.template",
            "1",
            Digest('c'));
        var client = new AliasLlmClient();
        var inference = new BaizeInferenceExecutor([
            new BaizeInferenceBinding(
                profile,
                changedTemplate,
                [new BaizeEndpointBinding("alias-endpoint", "alias-provider", "alias-model", client)]),
        ]);
        var store = new CountingDefinitionStore();
        var factory = new FuwenZhinuWorkflowFactory(
            store,
            new FuwenZhinuProviderRuntimeIdentity(
                catalogue.SnapshotRevision,
                admission.Receipt!.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference));

        var act = () => factory.CreateAsync("alias", "1", admission, ct).AsTask();

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*answer/infer*DescriptorUnavailable*");
        store.Writes.Should().Be(0);
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Registered_prompt_alias_requires_preflight_capable_executor_before_storage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admission, catalogue, _, _) = await AdmitRegisteredAliasAsync(ct);
        var inference = new TemplateCapturingInference();
        var store = new CountingDefinitionStore();
        var factory = new FuwenZhinuWorkflowFactory(
            store,
            new FuwenZhinuProviderRuntimeIdentity(
                catalogue.SnapshotRevision,
                admission.Receipt!.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference));

        var act = () => factory.CreateAsync("alias", "1", admission, ct).AsTask();

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*answer/infer*cannot preflight exact prompt-template bindings*");
        store.Writes.Should().Be(0);
        inference.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Structured_manifest_failure_is_rejected_before_storage_or_provider_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var inference = new ManifestInference();
        var store = new CountingDefinitionStore();
        var factory = new FuwenZhinuWorkflowFactory(
            store,
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference));

        var act = () => factory.CreateAsync("greet", "1", admission, ct).AsTask();

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*failed executor preflight*");
        inference.StructuredPreflightCalls.Should().Be(1);
        inference.ProviderCalls.Should().Be(0);
        store.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Legacy_preflight_remains_used_when_executor_has_no_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var admission = await AdmitAsync(ct);
        var inference = new LegacyRejectingInference();
        var store = new CountingDefinitionStore();
        var factory = new FuwenZhinuWorkflowFactory(
            store,
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference));

        var act = () => factory.CreateAsync("greet", "1", admission, ct).AsTask();

        await act.Should().ThrowAsync<FuwenZhinuAdmissionException>()
            .WithMessage("*legacy preflight blocked*");
        inference.PreflightCalls.Should().Be(1);
        inference.ProviderCalls.Should().Be(0);
        store.Writes.Should().Be(0);
    }

    private static async Task<(
        WorkflowAdmissionResult Admission,
        InMemoryTrustedCatalogue Catalogue,
        DescriptorReference Profile,
        DescriptorReference Template)> AdmitRegisteredAliasAsync(CancellationToken cancellationToken)
    {
        var profile = new DescriptorReference(
            DescriptorKind.InferenceProfile,
            "sample.profile",
            "1",
            Digest('a'));
        var template = new DescriptorReference(
            DescriptorKind.PromptTemplate,
            "sample.template",
            "1",
            Digest('b'));
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                profile,
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read,
                    CallableIdempotency.Idempotent,
                    CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(template),
        ]);
        var source =
            $"prompt standard_greeting(name: string) uses registered \"sample.template@1#{new string('b', 64)}\";\n" +
            $"workflow answer(input: string) -> string {{ infer infer = infer \"sample.profile@1#{new string('a', 64)}\" prompt standard_greeting(name: input;) limits aggregate turns 1 modelCalls 1 -> string; return infer; }}";
        var compiled = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(source, cancellationToken: cancellationToken);
        compiled.Succeeded.Should().BeTrue(
            string.Join("; ", compiled.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(compiled.Plan!, cancellationToken: cancellationToken);
        admission.Succeeded.Should().BeTrue(
            string.Join("; ", admission.Diagnostics.Select(item => $"{item.Code}:{item.Message}")));
        return (admission, catalogue, profile, template);
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

    private sealed class TemplateCapturingInference : IInferenceExecutor
    {
        public List<InferenceExecutionRequest> Requests { get; } = [];

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            using var document = JsonDocument.Parse("\"done\"");
            return ValueTask.FromResult(InferenceExecutionResult.Succeeded(
                RuntimeValue.FromJson(document.RootElement)));
        }
    }

    private sealed class ManifestInference : IInferenceExecutor, IInferenceExecutorManifest
    {
        private static readonly InferenceFeatureManifest Manifest = new(
            supportedPromptForms: [InferencePromptForm.WorkflowOwned],
            supportedModalities: [InferenceModality.StructuredText],
            supportsContextDelivery: false,
            maximumContextPayloadUtf8Bytes: null,
            supportedToolEffects: [InferenceToolEffect.ReadOnly],
            supportedLimits: [],
            recoveryQuality: InferenceRecoveryQuality.Unsupported,
            usageQuality: InferenceUsageQuality.Unknown,
            pricingQuality: InferencePricingQuality.Unknown);

        public int StructuredPreflightCalls { get; private set; }
        public int ProviderCalls { get; private set; }
        public InferenceFeatureManifest FeatureManifest => Manifest;

        public InferencePreflightReport PreflightDetailed(InferenceExecutionRequirement requirement)
        {
            StructuredPreflightCalls++;
            return InferencePreflight.Evaluate(requirement, Manifest);
        }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProviderCalls++;
            throw new InvalidOperationException("Provider execution should not be called during registration.");
        }
    }

    private sealed class LegacyRejectingInference : IInferenceExecutor, IInferenceExecutorPreflight
    {
        public int PreflightCalls { get; private set; }
        public int ProviderCalls { get; private set; }

        public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement)
        {
            PreflightCalls++;
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.NotAdmitted,
                "legacy preflight blocked");
        }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProviderCalls++;
            throw new InvalidOperationException("Provider execution should not be called during registration.");
        }
    }

    private sealed class AliasLlmClient : ILlmClient, ILlmCompletionClient
    {
        public List<LlmRequest> Requests { get; } = [];
        public LlmEndpointCapabilities Capabilities { get; } = new()
        {
            NativeStructuredOutput = true,
        };

        public Task<LlmResponse> CompleteAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new LlmResponse("\"Hello, Alice.\""));
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingProvenanceSink : IBaizeInferenceProvenanceSink
    {
        public List<InferenceExecutionEvidence> Items { get; } = [];

        public ValueTask RecordAsync(
            InferenceExecutionEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            Items.Add(evidence);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDefinitionStore : IWorkflowDefinitionStore
    {
        public int Writes { get; private set; }

        public ValueTask<WorkflowDefinitionWriteDisposition> StoreAsync(
            WorkflowDefinitionDocument definition,
            CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.FromResult(WorkflowDefinitionWriteDisposition.Created);
        }

        public ValueTask<WorkflowDefinitionDocument?> ReadAsync(
            string executionFingerprint,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<WorkflowDefinitionDocument?>(null);
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
