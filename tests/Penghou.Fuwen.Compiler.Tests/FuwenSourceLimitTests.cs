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

    private static async Task<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan)
    {
        var admission = await new WorkflowAdmissionService(
                new WorkflowCompiler(Catalogue(), capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return admission;
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

        var reversed = await CompileAsync(WorkflowWithLimits("limits timeout 30 maxTokens 400"));
        SingleInference(reversed).Limits.Should().Be(new InferenceLimits(400, 30));

        var tokensOnly = await CompileAsync(WorkflowWithLimits("limits maxTokens 400"));
        SingleInference(tokensOnly).Limits.Should().Be(new InferenceLimits(400, null));

        var timeoutOnly = await CompileAsync(WorkflowWithLimits("limits timeout 30"));
        SingleInference(timeoutOnly).Limits.Should().Be(new InferenceLimits(null, 30));

        var bare = await CompileAsync(WorkflowWithLimits(string.Empty));
        SingleInference(bare).Limits.Should().BeNull();
    }

    [Fact]
    public async Task Limits_select_IR_v8()
    {
        var plan = await CompileAsync(WorkflowWithLimits("limits maxTokens 400"));

        plan.IrVersion.Should().Be(FuwenContracts.IrVersionV8);
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
    public async Task Limits_require_IR_v8()
    {
        var plan = await CompileAsync(WorkflowWithLimits("limits maxTokens 400"));
        var downgraded = plan with
        {
            IrVersion = FuwenContracts.IrVersionV7,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV7,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV7,
        };

        var admission = await AdmitAsync(downgraded);

        admission.Succeeded.Should().BeFalse();
        admission.Diagnostics.Should().Contain(diagnostic => diagnostic.Message.Contains("IR v8"));
    }
}
