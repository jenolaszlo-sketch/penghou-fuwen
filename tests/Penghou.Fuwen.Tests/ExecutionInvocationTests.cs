using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class ExecutionInvocationTests
{
    private static string ExecutionFingerprint => $"sha256:fuwen-execution/v1:{new string('a', 64)}";
    private static string RequestFingerprint => $"sha256:fuwen-request/v1:{new string('b', 64)}";

    [Fact]
    public void Operation_key_is_stable_and_binds_restart_dimensions()
    {
        var first = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-1", RequestFingerprint);
        var repeated = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-1", RequestFingerprint);
        var changedInput = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-1", $"sha256:fuwen-request/v1:{new string('c', 64)}");
        var changedExecution = new ExecutionInvocation($"sha256:fuwen-execution/v1:{new string('c', 64)}", "workflow/activity", "workflow/activity", "revision-1", RequestFingerprint);
        var changedStructural = new ExecutionInvocation(ExecutionFingerprint, "workflow/other", "workflow/activity", "revision-1", RequestFingerprint);
        var changedRuntime = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity/$item/1", "revision-1", RequestFingerprint);
        var changedRevision = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-2", RequestFingerprint);

        first.OperationKey.Should().Be(repeated.OperationKey)
            .And.StartWith("sha256:fuwen-operation/v1:");
        changedInput.OperationKey.Should().NotBe(first.OperationKey);
        changedExecution.OperationKey.Should().NotBe(first.OperationKey);
        changedStructural.OperationKey.Should().NotBe(first.OperationKey);
        changedRuntime.OperationKey.Should().NotBe(first.OperationKey);
        changedRevision.OperationKey.Should().NotBe(first.OperationKey);
    }

    [Fact]
    public void Invocation_rejects_malformed_fingerprints_and_unbounded_identity_fields()
    {
        var malformed = () => new ExecutionInvocation("not-a-fingerprint", "workflow/activity", "workflow/activity", "revision-1", RequestFingerprint);
        malformed.Should().Throw<ArgumentException>();
        var emptyContract = () => new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-1", $"sha256::{new string('b', 64)}");
        emptyContract.Should().Throw<ArgumentException>();
        var oversizedContract = () => new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", "revision-1", $"sha256:{new string('r', 513)}:{new string('b', 64)}");
        oversizedContract.Should().Throw<ArgumentOutOfRangeException>();

        var oversizedPath = () => new ExecutionInvocation(ExecutionFingerprint, new string('x', ExecutionInvocation.MaximumPathUtf8Bytes + 1), "workflow/activity", "revision-1", RequestFingerprint);
        oversizedPath.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Numeric_step_revision_uses_canonical_invariant_text()
    {
        var invocation = new ExecutionInvocation(ExecutionFingerprint, "workflow/activity", "workflow/activity", 12, RequestFingerprint);

        invocation.StepRevision.Should().Be("12");
        invocation.OperationKey.Should().Be(ExecutionInvocation.ComputeOperationKey(
            ExecutionFingerprint, "workflow/activity", "workflow/activity", "12", RequestFingerprint));
    }
}
