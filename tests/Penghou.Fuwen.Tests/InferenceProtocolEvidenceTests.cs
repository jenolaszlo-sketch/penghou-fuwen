using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class InferenceProtocolEvidenceTests
{
    private static DescriptorReference Tool(string name = "sample.search") =>
        new(DescriptorKind.Tool, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    private static InferenceLimitSet Limits() => new(
    [
        new InferenceLimit(InferenceLimitDimension.Turns, 4),
        new InferenceLimit(InferenceLimitDimension.TotalTokens, 4_000),
    ]);

    private static InferenceOperationEvidence Operation(int ordinal) => new(
        ordinal, InferenceOperationKind.ModelTurn, $"interaction/model/{ordinal + 1:0000}",
        InferenceOperationDisposition.Succeeded, usageQuality: InferenceUsageQuality.Exact,
        promptTokens: 5, completionTokens: 3, totalTokens: 8);

    private static InferenceProtocolEvidence Evidence(
        IReadOnlyList<InferenceOperationEvidence>? operations = null,
        IReadOnlyList<InferenceToolOutcomeEvidence>? tools = null,
        IReadOnlyList<ProtectedPayloadReference>? payloads = null,
        ExecutionFailure? failure = null,
        bool commitment = false,
        InferenceRecoveryDisposition recovery = InferenceRecoveryDisposition.Fresh) =>
        new("interaction/1", Limits(), operations ?? [Operation(0)], tools ?? [], validationAttempts: 1,
            recoveryDisposition: recovery, commitmentUncertainty: commitment, failure: failure,
            protectedPayloads: payloads);

    [Fact]
    public void Evidence_records_identity_limits_and_operations()
    {
        var evidence = Evidence();

        evidence.InteractionId.Should().Be("interaction/1");
        evidence.Semantics.Should().Be(InferenceProtocolEvidence.CurrentSemantics);
        evidence.EffectiveLimits.GetMaximum(InferenceLimitDimension.Turns).Should().Be(4);
        evidence.Operations.Should().ContainSingle();
        evidence.Operations[0].Kind.Should().Be(InferenceOperationKind.ModelTurn);
        evidence.CommitmentUncertainty.Should().BeFalse();
        evidence.ProtectedPayloads.Should().BeEmpty();
    }

    [Fact]
    public void Evidence_rejects_too_many_operations()
    {
        var operations = Enumerable.Range(0, InferenceProtocolEvidence.MaximumOperations + 1)
            .Select(Operation)
            .ToArray();
        Action act = () => Evidence(operations: operations);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Evidence_rejects_too_many_tool_outcomes_and_payloads()
    {
        var tools = Enumerable.Range(0, InferenceProtocolEvidence.MaximumToolOutcomes + 1)
            .Select(_ => new InferenceToolOutcomeEvidence(Tool(), "op", InferenceOperationDisposition.Succeeded))
            .ToArray();
        ((Action)(() => Evidence(tools: tools))).Should().Throw<ArgumentOutOfRangeException>();

        var payloads = Enumerable.Range(0, InferenceProtocolEvidence.MaximumProtectedPayloads + 1)
            .Select(index => new ProtectedPayloadReference(
                "store", $"payload-{index}", new ContentDigest("sha256", "content/v1", new string('d', 64))))
            .ToArray();
        ((Action)(() => Evidence(payloads: payloads))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Operation_and_tool_evidence_reject_invalid_bounds()
    {
        ((Action)(() => new InferenceOperationEvidence(-1, InferenceOperationKind.ModelTurn, "id",
            InferenceOperationDisposition.Succeeded))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new InferenceOperationEvidence(0, (InferenceOperationKind)99, "id",
            InferenceOperationDisposition.Succeeded))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new InferenceOperationEvidence(0, InferenceOperationKind.ModelTurn, "id",
            InferenceOperationDisposition.Succeeded, totalTokens: -1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new InferenceToolOutcomeEvidence(Tool(), "op",
            InferenceOperationDisposition.Succeeded, resultUtf8Bytes: 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new ProtectedPayloadReference("store", "id",
            new ContentDigest("sha256", "content/v1", new string('d', 64)), byteLength: -1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Human_and_json_renderers_show_identity_outcome_and_protected_payloads_without_raw_payloads()
    {
        var hidden = new ProtectedPayloadReference(
            "protected-store", "payload-1",
            new ContentDigest("sha256", "content/v1", new string('d', 64)),
            byteLength: 128, retentionPolicyRevision: "retention/1");
        var evidence = Evidence(
            tools:
            [
                new InferenceToolOutcomeEvidence(Tool(), "interaction/1/tool/0000/call-1",
                    InferenceOperationDisposition.Succeeded, resultDigest: new ContentDigest("sha256", "inference-tool-result/v1", new string('e', 64)),
                    resultUtf8Bytes: 42),
            ],
            payloads: [hidden],
            failure: new ExecutionFailure(ExecutionFailureKind.Contract, ExecutionFailureCode.TurnLimitExceeded, "turn bound reached"),
            recovery: InferenceRecoveryDisposition.Fresh);

        var human = InferenceProtocolEvidenceRenderer.RenderHuman(evidence);
        var json = InferenceProtocolEvidenceRenderer.RenderJson(evidence);

        human.Should().Contain("TurnLimitExceeded");
        human.Should().Contain("interaction/1");
        human.Should().Contain("protected-store/payload-1");
        json.Should().Contain("\"TurnLimitExceeded\"");
        json.Should().Contain("\"semantics\":\"fuwen-inference-protocol/v1\"");
        json.Should().Contain(new string('e', 64));
        // Protected payloads appear only as provider/id/digest references; raw
        // payload content never crosses this boundary.
        human.Should().Contain(new string('d', 64));
        json.Should().Contain(new string('d', 64));
        evidence.ProtectedPayloads[0].PayloadId.Should().Be("payload-1");
    }

    [Fact]
    public void Evidence_truncation_flags_default_to_false_and_render_when_set()
    {
        var plain = Evidence();
        plain.OperationsTruncated.Should().BeFalse();
        plain.ToolOutcomesTruncated.Should().BeFalse();
        InferenceProtocolEvidenceRenderer.RenderHuman(plain).Should().NotContain("truncated");
        InferenceProtocolEvidenceRenderer.RenderJson(plain).Should().Contain("\"operationsTruncated\":false");

        var truncated = new InferenceProtocolEvidence(
            "interaction/1", Limits(), [Operation(0)], [],
            operationsTruncated: true, toolOutcomesTruncated: true);
        truncated.OperationsTruncated.Should().BeTrue();
        truncated.ToolOutcomesTruncated.Should().BeTrue();
        InferenceProtocolEvidenceRenderer.RenderHuman(truncated).Should().Contain("truncated");
        InferenceProtocolEvidenceRenderer.RenderJson(truncated).Should().Contain("\"toolOutcomesTruncated\":true");
    }

    [Fact]
    public void Evidence_sink_interface_is_public_and_non_authoritative()
    {
        typeof(IInferenceEvidenceSink).Should().BeAssignableTo<IInferenceEvidenceSink>();
        var method = typeof(IInferenceEvidenceSink).GetMethod(nameof(IInferenceEvidenceSink.RecordAsync));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(ValueTask));
    }
}
