using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>
/// FZ-1: a first-class neutral activity execution intent is carried in the
/// canonical plan, the fingerprint, and the IR version, without naming any
/// provider, Hufu, or Gagamba authority.
/// </summary>
public sealed class ActivityExecutionIntentTests
{
    private static readonly PrimitiveType Str = new(FuwenPrimitiveKind.String);
    private static readonly DescriptorReference ActivityDescriptor = new(
        DescriptorKind.Activity, "build-dotnet", "1",
        new ContentDigest("sha256", "fuwen-descriptor/v1", new string('a', 64)));

    private static WorkflowPlan Build(ActivityExecutionIntent? intent)
    {
        var activityPath = StructuralNodeIdentity.Create("demo", "build");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlanBuilder("demo", "1", Str, Str, "routing/1")
            .AddNode(new ActivityNode("build", activityPath, ActivityDescriptor, [], Str)
            {
                ExecutionIntent = intent,
            })
            .AddNode(new ReturnNode("return_result", returnPath, new InputBinding([])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
    }

    private static ActivityExecutionIntent Intent(ExecutionGuaranteeLevel unitTermination) => new(
        "dotnet-build",
        [new ExecutionGuarantee("execution.unit-termination", unitTermination)],
        [new ExecutionGuarantee("execution.owner-death-cleanup", ExecutionGuaranteeLevel.Full)]);

    [Fact]
    public void Intent_selects_the_version_and_is_bound_into_the_fingerprint()
    {
        var withIntent = Build(Intent(ExecutionGuaranteeLevel.Full));
        var withoutIntent = Build(null);

        withIntent.IrVersion.Should().Be(FuwenContracts.ExecutionIntentIrVersion);
        withoutIntent.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanValidator.Validate(withIntent);
        WorkflowPlanValidator.Validate(withoutIntent);

        WorkflowPlanIdentity.ComputeExecutionFingerprint(withIntent)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(withoutIntent));
    }

    [Fact]
    public void Changing_a_requested_guarantee_changes_the_fingerprint()
    {
        WorkflowPlanIdentity.ComputeExecutionFingerprint(Build(Intent(ExecutionGuaranteeLevel.Full)))
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(Build(Intent(ExecutionGuaranteeLevel.Partial))));
    }

    [Fact]
    public void Intent_under_the_base_IR_version_is_rejected()
    {
        var plan = Build(Intent(ExecutionGuaranteeLevel.Full)) with { IrVersion = FuwenContracts.IrVersion };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<NotSupportedException>().WithMessage("*execution-intent IR version*");
    }

    [Fact]
    public void Intent_carries_only_neutral_identifiers()
    {
        var intent = Intent(ExecutionGuaranteeLevel.Full);

        intent.Profile.Should().Be("dotnet-build");
        intent.Required.Should().ContainSingle()
            .Which.Capability.Should().Be("execution.unit-termination");
        // No property exposes a provider, grant, Hufu profile revision, executable, or handle.
        typeof(ActivityExecutionIntent).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("Profile", "Required", "Preferred");
        typeof(ExecutionGuarantee).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("Capability", "Minimum");
    }
}
