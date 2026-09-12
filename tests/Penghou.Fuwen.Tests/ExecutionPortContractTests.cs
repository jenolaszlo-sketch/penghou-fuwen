using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class ExecutionPortContractTests
{
    [Fact]
    public void Requests_snapshot_values_types_and_exact_descriptor_identities()
    {
        using var document = JsonDocument.Parse("{\"answer\":42}");
        var argument = new RuntimeArgument("input", RuntimeValue.FromJson(document.RootElement));
        var outputType = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Integer));
        var descriptor = Descriptor(DescriptorKind.Activity, "write");

        var request = new ActivityExecutionRequest(Invocation(), descriptor, [argument], outputType);

        request.Activity.Should().Be(descriptor).And.NotBeSameAs(descriptor);
        request.OutputType.Should().BeEquivalentTo(outputType).And.NotBeSameAs(outputType);
        request.Arguments.Should().ContainSingle();
        request.Arguments[0].Should().NotBeSameAs(argument);
        ((JsonRuntimeValue)request.Arguments[0].Value).Value.GetProperty("answer").GetInt32().Should().Be(42);
        ((Action)(() => ((IList<RuntimeArgument>)request.Arguments).Add(argument))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Requests_reject_wrong_descriptor_kinds_and_duplicate_names()
    {
        var argument = new RuntimeArgument("input", Json("true"));
        var output = new PrimitiveType(FuwenPrimitiveKind.Boolean);

        ((Action)(() => new ActivityExecutionRequest(Invocation(), Descriptor(DescriptorKind.ContextProvider, "wrong"), [], output)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => new ContextExecutionRequest(Invocation(), Descriptor(DescriptorKind.Activity, "wrong"), [], output)))
            .Should().Throw<ArgumentException>();
        ((Action)(() => new ActivityExecutionRequest(Invocation(), Descriptor(DescriptorKind.Activity, "activity"), [argument, argument], output)))
            .Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Fact]
    public void Inference_context_keeps_typed_value_and_snapshot_evidence_together()
    {
        var expectedType = new PrimitiveType(FuwenPrimitiveKind.String);
        var context = new InferenceContextInput("memory", expectedType, Json("\"selected\""), Snapshot());
        var request = new InferenceExecutionRequest(
            Invocation(),
            Descriptor(DescriptorKind.InferenceProfile, "profile"),
            Descriptor(DescriptorKind.PromptTemplate, "prompt"),
            [],
            [context],
            new PrimitiveType(FuwenPrimitiveKind.String));

        request.ContextInputs.Should().ContainSingle();
        request.ContextInputs[0].ExpectedType.Should().BeEquivalentTo(expectedType).And.NotBeSameAs(expectedType);
        request.ContextInputs[0].Value.Should().NotBeNull();
        request.ContextInputs[0].ContextSnapshot.Should().NotBeSameAs(context.ContextSnapshot);
        ((Action)(() => new InferenceExecutionRequest(
            Invocation(),
            Descriptor(DescriptorKind.InferenceProfile, "profile"),
            Descriptor(DescriptorKind.PromptTemplate, "prompt"),
            [],
            [context, context],
            new PrimitiveType(FuwenPrimitiveKind.String))))
            .Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Fact]
    public void Failures_are_closed_typed_outcomes_with_safe_retry_invariants()
    {
        var transient = new ExecutionFailure(
            ExecutionFailureKind.Infrastructure,
            ExecutionFailureCode.TransientInfrastructureFailure,
            "temporary",
            ExecutionRetryDisposition.InfrastructureOnly);
        var failed = ActivityExecutionResult.Failed(transient);

        failed.IsSuccess.Should().BeFalse();
        failed.Failure.Should().BeSameAs(transient);
        failed.Output.Should().BeNull();
        ((Action)(() => new ExecutionFailure(
            ExecutionFailureKind.ProviderOutput,
            ExecutionFailureCode.Cancelled,
            "mismatch"))).Should().Throw<ArgumentException>();
        ((Action)(() => new ExecutionFailure(
            ExecutionFailureKind.Infrastructure,
            ExecutionFailureCode.FencingLost,
            "stale fence",
            ExecutionRetryDisposition.InfrastructureOnly))).Should().Throw<ArgumentException>();
        ((Action)(() => new ExecutionFailure(
            ExecutionFailureKind.Infrastructure,
            ExecutionFailureCode.TransientInfrastructureFailure,
            "ambiguous",
            ExecutionRetryDisposition.InfrastructureOnly,
            mayHaveCommittedEffect: true))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Successful_results_snapshot_outputs_publications_and_context_evidence()
    {
        var receipt = new ArtifactPublicationReceipt(
            "operation-1",
            new ArtifactReference("provider", "artifact-1", Descriptor(DescriptorKind.Artifact, "image"), Digest("artifact-content"), 12),
            "receipt-1",
            PublicationDisposition.Created);
        var activity = ActivityExecutionResult.Succeeded(Json("true"), [receipt]);
        var context = ContextExecutionResult.Succeeded(Json("\"context\""), Snapshot());

        activity.IsSuccess.Should().BeTrue();
        activity.Publications.Should().ContainSingle();
        activity.Publications[0].Should().NotBeSameAs(receipt);
        activity.Publications[0].Artifact.Should().NotBeSameAs(receipt.Artifact);
        context.IsSuccess.Should().BeTrue();
        context.Publications.Should().BeEmpty();
        context.ContextSnapshot.Should().NotBeNull();
        ((Action)(() => ((IList<ArtifactPublicationReceipt>)activity.Publications).Add(receipt))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Inference_evidence_rejects_ambiguous_attempt_histories()
    {
        var profile = Descriptor(DescriptorKind.InferenceProfile, "profile");
        var prompt = Descriptor(DescriptorKind.PromptTemplate, "prompt");

        ((Action)(() => new InferenceExecutionEvidence(profile, prompt,
            [new InferenceAttemptEvidence(2, "provider", "model", null, false, "ProviderError")])))
            .Should().Throw<ArgumentException>().WithMessage("*contiguous*");
        ((Action)(() => new InferenceExecutionEvidence(profile, prompt,
            [new InferenceAttemptEvidence(1, "provider", "model", null, false)])))
            .Should().Throw<ArgumentException>().WithMessage("*failure code*");
        ((Action)(() => new InferenceExecutionEvidence(profile, prompt,
            [new InferenceAttemptEvidence(1, "provider", "model", null, true, "unexpected")])))
            .Should().Throw<ArgumentException>().WithMessage("*cannot carry failure*");
        ((Action)(() => new InferenceExecutionEvidence(profile, prompt,
            [
                new InferenceAttemptEvidence(1, "provider", "model", null, true),
                new InferenceAttemptEvidence(2, "provider", "model", null, false, "ProviderError"),
            ])))
            .Should().Throw<ArgumentException>().WithMessage("*final inference attempt*");
    }

    private static RuntimeValue Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeValue.FromJson(document.RootElement);
    }

    private static ExecutionInvocation Invocation() => new(
        $"sha256:fuwen-execution/v3:{new string('a', 64)}",
        "workflow/node",
        "workflow/node",
        "revision-1",
        $"sha256:fuwen-request/v1:{new string('b', 64)}");

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) => new(kind, name, "1", Digest("descriptor"));

    private static ContextSnapshotReference Snapshot() => new(
        Descriptor(DescriptorKind.ContextProvider, "context"),
        "snapshot-1",
        Digest("request"),
        Digest("content"),
        [],
        "policy-1",
        new ContextSnapshotBudgetEvidence(false, null, null, null, null),
        DateTimeOffset.UnixEpoch.AddDays(1));

    private static ContentDigest Digest(string contract) => new("sha256", contract, new string('d', 64));
}
