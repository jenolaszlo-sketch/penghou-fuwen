using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Inference limits: parsing, IR gating, ranges, and tool-effect alignment.</summary>
public sealed class FuwenSourceLimitTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static ITrustedCatalogue Catalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d')),
            callableContract: new CallableContract(
                new CallableSignature(
                    [new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
                    new PrimitiveType(FuwenPrimitiveKind.String)),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.PromptTemplate, "sample.template", "1", Digest('c'))),
    ]);

    private const string ProfileRef = "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string TemplateRef = "sample.template@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static string WorkflowWithLimits(string limitsClause) =>
        "workflow demo(input: string) -> string {\n" +
        $"  infer hello = infer \"{ProfileRef}\" using \"{TemplateRef}\" (request: input;) {limitsClause} -> string;\n" +
        "  return hello;\n" +
        "}";

    private static async Task<WorkflowPlan> CompileAsync(string source)
    {
        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        return result.Plan!;
    }

    private static async Task<FuwenSourceCompilationResult> CompileRawAsync(string source) =>
        await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

    private static InferenceNode SingleInference(WorkflowPlan plan) =>
        plan.Nodes.OfType<InferenceNode>().Single();

    [Fact]
    public async Task Limits_parse_in_any_order_or_singly()
    {
        var both = await CompileAsync(WorkflowWithLimits("limits maxTokens 400 timeout 30"));
        SingleInference(both).Limits.Should().Be(new InferenceLimits(400, 30));
        SingleInference(both).Protocol.Should().BeNull();

        var reversed = await CompileAsync(WorkflowWithLimits("limits timeout 30 maxTokens 400"));
        SingleInference(reversed).Limits.Should().Be(new InferenceLimits(400, 30));
        SingleInference(reversed).Protocol.Should().BeNull();

        var tokensOnly = await CompileAsync(WorkflowWithLimits("limits maxTokens 400"));
        SingleInference(tokensOnly).Limits.Should().Be(new InferenceLimits(400, null));
        SingleInference(tokensOnly).Protocol.Should().BeNull();

        var timeoutOnly = await CompileAsync(WorkflowWithLimits("limits timeout 30"));
        SingleInference(timeoutOnly).Limits.Should().Be(new InferenceLimits(null, 30));
        SingleInference(timeoutOnly).Protocol.Should().BeNull();

        var bare = await CompileAsync(WorkflowWithLimits(string.Empty));
        SingleInference(bare).Limits.Should().BeNull();
        SingleInference(bare).Protocol.Should().BeNull();
    }

    [Fact]
    public async Task Limits_use_the_current_IR()
    {
        var plan = await CompileAsync(WorkflowWithLimits("limits maxTokens 400"));

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        SingleInference(plan).Protocol.Should().BeNull();
    }

    [Fact]
    public async Task Limits_reject_empty_malformed_and_duplicate_clauses()
    {
        foreach (var clause in new[]
        {
            "limits",
            "limits maxTokens 0",
            "limits timeout 0",
            "limits maxTokens 400 maxTokens 500",
            "limits timeout 30 timeout 40",
        })
        {
            var result = await new FuwenSourceCompiler(Catalogue())
                .CompileAsync(
                    WorkflowWithLimits(clause),
                    cancellationToken: TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeFalse($"clause '{clause}' must be rejected");
        }
    }

    [Fact]
    public async Task Limits_validation_rejects_out_of_range_values()
    {
        foreach (var clause in new[]
        {
            "limits maxTokens 2000000",
            "limits timeout 4000",
        })
        {
            var raw = await CompileRawAsync(WorkflowWithLimits(clause));
            raw.Succeeded.Should().BeFalse($"clause '{clause}' must not admit");
        }

        var admitted = await CompileRawAsync(WorkflowWithLimits("limits maxTokens 400 timeout 30"));
        admitted.Succeeded.Should().BeTrue(
            string.Join("; ", admitted.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
    }

    [Fact]
    public async Task Aggregate_limits_use_the_current_IR_without_a_protocol_revision()
    {
        var plan = await CompileAsync(
            WorkflowWithLimits(
                "limits maxTokens 400 timeout 30 aggregate turns 8 modelCalls 6 toolCalls 4 " +
                "promptTokens 12000 completionTokens 4000 totalTokens 16000 durationMs 300000 " +
                "cost \"USD\" 250000 toolArgumentBytes 65536 toolResultBytes 262144 " +
                "retainedConversationBytes 524288 retainedEvidenceBytes 524288"));

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var inference = SingleInference(plan);
        inference.Limits.Should().Be(new InferenceLimits(400, 30));
        inference.Protocol.Should().NotBeNull();
        inference.Protocol.Limits.MaxTurns.Should().Be(8);
        inference.Protocol.Limits.MaxModelCalls.Should().Be(6);
        inference.Protocol.Limits.MaxToolCalls.Should().Be(4);
        inference.Protocol.Limits.MaxPromptTokens.Should().Be(12000);
        inference.Protocol.Limits.MaxCompletionTokens.Should().Be(4000);
        inference.Protocol.Limits.MaxTotalTokens.Should().Be(16000);
        inference.Protocol.Limits.MaxDurationMilliseconds.Should().Be(300000);
        inference.Protocol.Limits.Cost.Should().Be(new InferenceCostLimit("USD", 250000));
        inference.Protocol.Limits.MaxToolArgumentBytes.Should().Be(65536);
        inference.Protocol.Limits.MaxToolResultBytes.Should().Be(262144);
        inference.Protocol.Limits.MaxRetainedConversationBytes.Should().Be(524288);
        inference.Protocol.Limits.MaxRetainedEvidenceBytes.Should().Be(524288);
    }

    [Fact]
    public async Task Aggregate_limits_require_a_dimension_and_reject_duplicate_or_malformed_values()
    {
        foreach (var clause in new[]
        {
            "limits aggregate",
            "limits aggregate turns 2 turns 3",
            "limits aggregate turns 0",
            "limits aggregate turns 1000000000000001",
            "limits aggregate cost USD 100",
            "limits aggregate cost \"USD\" 0",
            "limits aggregate cost \"USD\" 100 cost \"USD\" 200",
        })
        {
            var result = await CompileRawAsync(WorkflowWithLimits(clause));
            result.Succeeded.Should().BeFalse($"clause '{clause}' must be rejected");
        }
    }

    [Fact]
    public async Task Aggregate_only_limits_leave_per_call_limits_unset()
    {
        var plan = await CompileAsync(WorkflowWithLimits("limits aggregate turns 4"));

        var inference = SingleInference(plan);
        inference.Limits.Should().BeNull();
        inference.Protocol!.Limits.MaxTurns.Should().Be(4);
    }
}
