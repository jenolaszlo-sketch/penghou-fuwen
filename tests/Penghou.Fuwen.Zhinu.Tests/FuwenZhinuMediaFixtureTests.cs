using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Penghou.Baize.Generation;
using Penghou.Fuwen.Baize;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Zhinu.Tests;

public sealed partial class FuwenZhinuSequentialInterpreterTests
{
    [Fact]
    public async Task Sqlite_media_vertical_publishes_verified_non_code_artifact()
    {
        var fixture = await AdmitMediaPlanAsync();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var client = new CompletedMediaClient(bytes);
        var publisher = new VerifiedMediaPublisher(bytes);
        var inference = new BaizeGenerationInferenceExecutor([
            new BaizeGenerationBinding(
                fixture.Profile,
                fixture.Prompt,
                fixture.Artifact,
                BaizeGenerationModality.Image,
                "media-endpoint",
                "recorded-provider",
                "recorded-image-model",
                client,
                request => new ImageGenerationRequest
                {
                    Prompt = "render a release fixture",
                    IdempotencyKey = request.Invocation.OperationKey,
                },
                publisher),
        ]);
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                IdentityFor(fixture.Admission),
                new FuwenZhinuExecutionPorts(new UnusedActivity(), new UnusedContext(), inference))
            .CreateAsync("fuwen.media-fixture", "1", fixture.Admission, TestContext.Current.CancellationToken);
        var root = CreateTempRoot();

        try
        {
            await using var engine = CreateEngine(CreateStore(Path.Combine(root, "workflow.db")), registration);
            using var input = JsonDocument.Parse("\"request\"");
            var runId = await engine.StartAsync(
                "fuwen.media-fixture",
                "1",
                input.RootElement.Clone(),
                cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            var output = await engine.WaitForCompletionAsync<JsonElement>(
                runId,
                cancellationToken: TestContext.Current.CancellationToken);

            output.ValueKind.Should().Be(JsonValueKind.Object);
            client.Submissions.Should().ContainSingle()
                .Which.Should().Be(publisher.OperationKeys.Single());
            publisher.Calls.Should().Be(1);
            var artifacts = await engine.GetArtifactsAsync(runId, TestContext.Current.CancellationToken);
            artifacts.Should().ContainSingle();
            artifacts[0].ArtifactType.Should().Be("sample.generated-image");
            artifacts[0].ContentHash.Should().EndWith(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<MediaFixture> AdmitMediaPlanAsync()
    {
        static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));
        var inputType = new PrimitiveType(FuwenPrimitiveKind.String);
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.media-profile", "1", Digest('a'));
        var prompt = new DescriptorReference(DescriptorKind.PromptTemplate, "sample.media-prompt", "1", Digest('b'));
        var artifact = new DescriptorReference(DescriptorKind.Artifact, "sample.generated-image", "1", Digest('c'));
        var outputType = new ArtifactType(artifact);
        var inferencePath = StructuralNodeIdentity.Create("media-fixture", "generate");
        var returnPath = StructuralNodeIdentity.Create("media-fixture", "return_result");
        var plan = new WorkflowPlan(
            FuwenContracts.IrVersionV3,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersionV3,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersionV3,
            "media-fixture",
            "1",
            inputType,
            outputType,
            "routing/1",
            [],
            [profile, prompt, artifact],
            new CapabilityManifest([]),
            [
                new InferenceNode("generate", inferencePath, profile, prompt, [], [], outputType, ContextRequirements: []),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferencePath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("media-fixture", [
                    new WorkflowExecutionPhase([inferencePath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                profile,
                callableContract: new CallableContract(
                    new CallableSignature([], outputType),
                    CallableEffect.Read,
                    CallableIdempotency.Idempotent,
                    CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(prompt),
            new TrustedCatalogueDescriptor(artifact),
        ]);
        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                catalogue,
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        admission.Succeeded.Should().BeTrue(string.Join("; ", admission.Diagnostics.Select(item => item.Message)));
        return new MediaFixture(admission, profile, prompt, artifact);
    }

    private sealed record MediaFixture(
        WorkflowAdmissionResult Admission,
        DescriptorReference Profile,
        DescriptorReference Prompt,
        DescriptorReference Artifact);

    private sealed class CompletedMediaClient(byte[] bytes) : IGenerationClient
    {
        private readonly GenerationOperationHandle handle = new(
            "recorded-provider",
            "media-endpoint",
            "operation-1",
            "recorded-image-model",
            new Dictionary<string, string>());

        public List<string> Submissions { get; } = [];
        public GenerationCapabilities Capabilities { get; } = new()
        {
            Features = GenerationFeature.IdempotentSubmission | GenerationFeature.OperationRetrieval,
        };

        public Task<GenerationOperation> SubmitAsync(GenerationRequest request, CancellationToken cancellationToken = default)
        {
            Submissions.Add(request.IdempotencyKey!);
            var asset = new GeneratedAsset(
                new InlineGeneratedAssetSource(bytes, "image/png"),
                "image/png",
                "fixture.png",
                bytes.Length,
                null,
                new Dictionary<string, object?>());
            return Task.FromResult(new GenerationOperation(
                handle,
                GenerationOperationState.Succeeded,
                new GenerationResult([asset], new Dictionary<string, object?>()),
                ProviderMetadata: new Dictionary<string, object?>()));
        }

        public Task<GenerationOperation> GetAsync(GenerationOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The recorded fixture completes on submission.");

        public Task<GenerationOperation> CancelAsync(GenerationOperationHandle operationHandle, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The recorded fixture never requires cancellation.");
    }

    private sealed class VerifiedMediaPublisher(byte[] bytes) : IBaizeGeneratedAssetPublisher
    {
        public int Calls { get; private set; }
        public List<string> OperationKeys { get; } = [];

        public ValueTask<IReadOnlyList<ArtifactPublicationReceipt>> PublishAsync(
            InferenceExecutionRequest request,
            IReadOnlyList<GeneratedAsset> assets,
            DescriptorReference artifactDescriptor,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            OperationKeys.Add(request.Invocation.OperationKey);
            assets.Should().ContainSingle();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            IReadOnlyList<ArtifactPublicationReceipt> receipts =
            [
                new ArtifactPublicationReceipt(
                    request.Invocation.OperationKey,
                    new ArtifactReference(
                        "verified-media-store",
                        "fixture-image-1",
                        artifactDescriptor,
                        new ContentDigest("sha256", "immutable-file-bytes/v1", digest),
                        bytes.Length,
                        "fixture.png"),
                    "media-receipt-1",
                    PublicationDisposition.Created),
            ];
            return ValueTask.FromResult(receipts);
        }
    }
}
