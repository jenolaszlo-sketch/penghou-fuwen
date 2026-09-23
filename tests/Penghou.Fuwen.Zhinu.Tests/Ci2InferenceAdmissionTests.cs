using FluentAssertions;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed class Ci2InferenceAdmissionTests
{
    [Fact]
    public async Task Factory_admission_carries_deterministic_current_aggregate_limits()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = Descriptor(DescriptorKind.InferenceProfile, "sample.profile", 'a');
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "sample.prompt", 'b');
        var protocol = new InferenceProtocol(
            new InferenceProtocolLimits(
                maxTurns: 4,
                maxModelCalls: 5,
                maxToolCalls: 3,
                maxPromptTokens: 2_000,
                maxCompletionTokens: 1_000,
                maxTotalTokens: 3_000,
                maxDurationMilliseconds: 30_000,
                maxToolArgumentBytes: 8_000,
                maxToolResultBytes: 16_000,
                maxRetainedConversationBytes: 32_000,
                maxRetainedEvidenceBytes: 64_000,
                cost: new InferenceCostLimit("USD", 12_500)));
        var plan = new WorkflowPlanBuilder(
                "ci2", "1", new PrimitiveType(FuwenPrimitiveKind.String),
                new PrimitiveType(FuwenPrimitiveKind.String), "routing/1")
            .AddCatalogueBinding(profile)
            .AddCatalogueBinding(prompt)
            .AddNode(new InferenceNode(
                "infer",
                StructuralNodeIdentity.Create("ci2", "infer"),
                profile,
                prompt,
                [new ArgumentBinding("request", new InputBinding([]))],
                [],
                new PrimitiveType(FuwenPrimitiveKind.String),
                ContextRequirements: [],
                Protocol: protocol))
            .AddNode(new ReturnNode(
                "return_result",
                StructuralNodeIdentity.Create("ci2", "return_result"),
                new NodeOutputBinding(StructuralNodeIdentity.Create("ci2", "infer"), [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("ci2", [
                    new WorkflowExecutionPhase([
                        StructuralNodeIdentity.Create("ci2", "infer"),
                    ]),
                    new WorkflowExecutionPhase([
                        StructuralNodeIdentity.Create("ci2", "return_result"),
                    ]),
                ]),
            ]))
            .Build();
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(
                        profile,
                        callableContract: new CallableContract(
                            new CallableSignature([new CallableParameter("request", str)], str),
                            CallableEffect.Read,
                            CallableIdempotency.Idempotent,
                            CallableRetrySafety.Safe)),
                    new TrustedCatalogueDescriptor(prompt),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(static d => $"{d.Code}:{d.Message} path={d.Path} actual={d.Actual}")));

        var inference = new CapturingManifestInference(profile, prompt);
        var factory = new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference));

        await factory.CreateAsync("ci2", "1", admission, ct);

        inference.Requirement.Should().NotBeNull();
        inference.Requirement!.Limits.Limits.Select(static limit => limit.Dimension)
            .Should().Equal(
                InferenceLimitDimension.Turns,
                InferenceLimitDimension.ModelCalls,
                InferenceLimitDimension.ToolCalls,
                InferenceLimitDimension.PromptTokens,
                InferenceLimitDimension.CompletionTokens,
                InferenceLimitDimension.TotalTokens,
                InferenceLimitDimension.DurationMilliseconds,
                InferenceLimitDimension.ToolArgumentBytes,
                InferenceLimitDimension.ToolResultBytes,
                InferenceLimitDimension.RetainedConversationBytes,
                InferenceLimitDimension.RetainedEvidenceBytes,
                InferenceLimitDimension.CostMicrounits);
        inference.Requirement.Limits.GetMaximum(InferenceLimitDimension.TotalTokens).Should().Be(3_000);
        inference.Requirement.Limits.GetMaximum(InferenceLimitDimension.CostMicrounits).Should().Be(12_500);
    }

    [Fact]
    public async Task Mixed_plan_routes_each_node_to_its_own_executor_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var profileA = Descriptor(DescriptorKind.InferenceProfile, "sample.coordinated", 'a');
        var profileB = Descriptor(DescriptorKind.InferenceProfile, "sample.onecall", 'c');
        var templateB = Descriptor(DescriptorKind.PromptTemplate, "sample.template-b", 'd');
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var coordinatedPath = StructuralNodeIdentity.Create("mixed", "coordinated");
        var legacyPath = StructuralNodeIdentity.Create("mixed", "legacy");
        var returnPath = StructuralNodeIdentity.Create("mixed", "return_result");
        var localPrompt = new PromptDefinition(
            "local",
            [new PromptParameter("question", str)],
            [new PromptMessage(PromptMessageRole.User, "Answer {{ question }}.")]);
        var plan = new WorkflowPlanBuilder(
                "mixed", "1", str, str, "routing/1")
            .AddCatalogueBinding(profileA)
            .AddCatalogueBinding(profileB)
            .AddCatalogueBinding(templateB)
            .AddPrompt(localPrompt)
            .AddNode(new InferenceNode(
                "coordinated", coordinatedPath, profileA, null,
                [], [], str, [],
                PromptName: "local",
                PromptBindings: [new PromptBinding("question", new InputBinding([]))],
                Protocol: new InferenceProtocol(new InferenceProtocolLimits(maxTurns: 2, maxModelCalls: 2))))
            .AddNode(new InferenceNode(
                "legacy", legacyPath, profileB, templateB,
                [new ArgumentBinding("request", new NodeOutputBinding(coordinatedPath, []))], [], str, []))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(legacyPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("mixed", [
                    new WorkflowExecutionPhase([coordinatedPath]),
                    new WorkflowExecutionPhase([legacyPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
        var promptDigest = localPrompt.GetSemanticDigest();
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue(
                [
                    new TrustedCatalogueDescriptor(profileA, callableContract: Contract(str)),
                    new TrustedCatalogueDescriptor(profileB, callableContract: Contract(str)),
                    new TrustedCatalogueDescriptor(templateB),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: ct);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(static d => d.Message)));

        // The turn manifest binds only the coordinated profile and prompt
        // digest; the one-call manifest binds the legacy template. Registration
        // must route each node to its own manifest rather than failing the
        // legacy node against the turn one.
        var turns = new SelectiveTurnManifestExecutor(profileA, promptDigest, tools: []);
        var inference = new CapturingManifestInference(profileB, templateB);
        var factory = new FuwenZhinuWorkflowFactory(
            new InMemoryWorkflowDefinitionStore(),
            new FuwenZhinuProviderRuntimeIdentity(
                admission.Receipt!.CatalogueSnapshotRevision,
                admission.Receipt.ResolvedDescriptorSetFingerprint),
            new FuwenZhinuExecutionPorts(
                new UnusedActivity(), new UnusedContext(), inference,
                observer: null, new FuwenZhinuExecutionPorts.Options(), turns));

        await factory.CreateAsync("mixed", "1", admission, ct);

        inference.Requirement.Should().NotBeNull();
        inference.Requirement!.Profile.Should().Be(profileB);
    }

    private static CallableContract Contract(PrimitiveType str) => new(
        new CallableSignature([new CallableParameter("request", str)], str),
        CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);

    private static DescriptorReference Descriptor(DescriptorKind kind, string name, char digest) =>
        new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string(digest, 64)));

    private sealed class CapturingManifestInference : IInferenceExecutor, IInferenceExecutorManifest
    {
        public CapturingManifestInference(DescriptorReference profile, DescriptorReference prompt)
        {
            RequirementManifest = new InferenceFeatureManifest(
                [InferencePromptForm.RegisteredTemplate],
                [InferenceModality.StructuredText],
                supportsContextDelivery: false,
                maximumContextPayloadUtf8Bytes: null,
                supportedToolEffects: [InferenceToolEffect.ReadOnly],
                supportedLimits: Enum.GetValues<InferenceLimitDimension>()
                    .Select(static dimension => new InferenceLimit(dimension, 1_000_000))
                    .ToArray(),
                recoveryQuality: InferenceRecoveryQuality.Unsupported,
                usageQuality: InferenceUsageQuality.Unknown,
                pricingQuality: InferencePricingQuality.Unknown,
                profiles: [profile],
                promptTemplates: [prompt]);
        }

        private InferenceFeatureManifest RequirementManifest { get; }
        public InferenceExecutionRequirement? Requirement { get; private set; }
        public InferenceFeatureManifest FeatureManifest => RequirementManifest;

        public InferencePreflightReport PreflightDetailed(InferenceExecutionRequirement requirement)
        {
            Requirement = requirement;
            return InferencePreflight.Evaluate(requirement, RequirementManifest);
        }

        public ValueTask<InferenceExecutionResult> ExecuteAsync(
            InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider execution should not occur during admission.");
    }

    private sealed class SelectiveTurnManifestExecutor : IInferenceTurnExecutor, IInferenceTurnExecutorManifest
    {
        private readonly InferenceFeatureManifest manifest;

        public SelectiveTurnManifestExecutor(
            DescriptorReference profile,
            string? promptDigest,
            IReadOnlyList<DescriptorReference>? tools = null)
        {
            manifest = new InferenceFeatureManifest(
                [InferencePromptForm.WorkflowOwned],
                [InferenceModality.StructuredText],
                supportsContextDelivery: false,
                maximumContextPayloadUtf8Bytes: null,
                [InferenceToolEffect.ReadOnly],
                [
                    new InferenceLimit(InferenceLimitDimension.Turns, 10),
                    new InferenceLimit(InferenceLimitDimension.ModelCalls, 10),
                ],
                InferenceRecoveryQuality.Unsupported,
                InferenceUsageQuality.Unknown,
                InferencePricingQuality.Unknown,
                profiles: [profile],
                tools: tools,
                workflowPromptDigests: promptDigest is null ? [] : [promptDigest]);
        }

        public InferenceFeatureManifest TurnFeatureManifest => manifest;

        public InferencePreflightReport PreflightTurnDetailed(InferenceExecutionRequirement requirement) =>
            InferencePreflight.Evaluate(requirement ?? throw new ArgumentNullException(nameof(requirement)), manifest);

        public ValueTask<InferenceTurnResult> ExecuteTurnAsync(
            InferenceTurnRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Turn execution should not occur during admission.");
    }

    private sealed class UnusedActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity execution should not occur during admission.");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Context execution should not occur during admission.");
    }
}
