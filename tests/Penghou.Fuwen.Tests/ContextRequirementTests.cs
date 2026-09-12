using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class ContextRequirementTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Historical_versions_reject_non_null_typed_requirements(int version)
    {
        var source = PlanFixture.CreateV3();
        var plan = source with
        {
            IrVersion = version == 1 ? FuwenContracts.IrVersionV1 : FuwenContracts.IrVersionV2,
            CompilerSemanticVersion = version == 1 ? FuwenContracts.CompilerSemanticVersionV1 : FuwenContracts.CompilerSemanticVersionV2,
            FingerprintVersion = version == 1 ? FuwenContracts.ExecutionFingerprintVersionV1 : FuwenContracts.ExecutionFingerprintVersionV2,
            ExecutionOrder = version == 1 ? null : source.ExecutionOrder,
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*Typed context requirements*");
    }

    [Fact]
    public void V3_rejects_legacy_context_snapshots_and_null_requirements()
    {
        var source = PlanFixture.CreateV3();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var legacy = source with
        {
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { ContextSnapshots = [new NodeOutputBinding("answer/context", [])] }
                : node).ToArray(),
        };
        var nullRequirements = source with
        {
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { ContextRequirements = null }
                : node).ToArray(),
        };

        Action legacyAct = () => WorkflowPlanIdentity.GetCanonicalBytes(legacy);
        Action nullAct = () => WorkflowPlanIdentity.GetCanonicalBytes(nullRequirements);

        legacyAct.Should().Throw<ArgumentException>().WithMessage("*legacy context snapshots*");
        nullAct.Should().Throw<ArgumentException>().WithMessage("*non-null ContextRequirements*");
    }

    [Fact]
    public void V3_rejects_duplicate_name_source_projection_non_context_and_type_mismatch()
    {
        var source = PlanFixture.CreateV3();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var contextType = source.Nodes.OfType<ContextNode>().Single().OutputType;
        var cases = new[]
        {
            inference with
            {
                ContextRequirements = [
                    new ContextRequirement("same", new NodeOutputBinding("answer/context", []), contextType),
                    new ContextRequirement("same", new NodeOutputBinding("answer/context", []), contextType),
                ],
            },
            inference with
            {
                ContextRequirements = [
                    new ContextRequirement("one", new NodeOutputBinding("answer/context", ["value"]), contextType),
                    new ContextRequirement("two", new NodeOutputBinding("answer/context", []), contextType),
                ],
            },
            inference with
            {
                ContextRequirements = [new ContextRequirement("wrong_source", new NodeOutputBinding("answer/validate", []), contextType)],
            },
            inference with
            {
                ContextRequirements = [new ContextRequirement("wrong_type", new NodeOutputBinding("answer/context", []), new PrimitiveType(FuwenPrimitiveKind.String))],
            },
        };

        foreach (var candidate in cases)
        {
            var plan = source with { Nodes = source.Nodes.Select(node => node == inference ? candidate : node).ToArray() };
            var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);
            act.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void V3_rejects_forward_phase_context_dependency()
    {
        var source = PlanFixture.CreateV3();
        var order = source.ExecutionOrder!.Regions[0];
        var plan = source with
        {
            ExecutionOrder = source.ExecutionOrder with
            {
                Regions = [order with { Phases = [order.Phases[1], order.Phases[0], order.Phases[2], order.Phases[3]] }],
            },
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*earlier execution phase*");
    }

    [Fact]
    public void V3_rejects_cross_region_context_dependency()
    {
        var source = PlanFixture.CreateV3();
        var context = source.Nodes.OfType<ContextNode>().Single();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var checkPath = StructuralNodeIdentity.Create(source.Name, "check");
        var branchContextPath = $"{checkPath}/$then/branch_context";
        var branchContext = context with { Name = "branch_context", StructuralPath = branchContextPath };
        var conditional = new ConditionalNode(
            "check",
            checkPath,
            new ConditionExpression(ConditionOperator.Exists, new InputBinding([])),
            [branchContext],
            []);
        var branchRequirement = new ContextRequirement("branch_context", new NodeOutputBinding(branchContextPath, []), context.OutputType);
        var plan = source with
        {
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { ContextRequirements = [branchRequirement], ContextSnapshots = [] }
                : node).Append(conditional).ToArray(),
            ExecutionOrder = new WorkflowExecutionOrder([
                new WorkflowExecutionRegion(source.Name, [
                    new WorkflowExecutionPhase([context.StructuralPath]),
                    new WorkflowExecutionPhase([checkPath]),
                    new WorkflowExecutionPhase([inference.StructuralPath]),
                    new WorkflowExecutionPhase(["answer/validate", "answer/audit"]),
                    new WorkflowExecutionPhase(["answer/return_result"]),
                ]),
                new WorkflowExecutionRegion($"{checkPath}/$then", [new WorkflowExecutionPhase([branchContextPath])]),
                new WorkflowExecutionRegion($"{checkPath}/$else", []),
            ]),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*crosses execution regions*");
    }

    [Fact]
    public void V4_rejects_invalid_typed_context_dependency()
    {
        var source = PlanFixture.CreateV3();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var invalid = source with
        {
            IrVersion = FuwenContracts.IrVersionV4,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV4,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV4,
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with
                {
                    ContextRequirements =
                    [
                        new ContextRequirement(
                            "invalid",
                            new NodeOutputBinding("answer/validate", []),
                            new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                    ],
                }
                : node).ToArray(),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(invalid);

        act.Should().Throw<ArgumentException>().WithMessage("*ContextNode*");
    }

    [Fact]
    public void V3_normalizes_requirement_order_but_requirement_changes_change_identity()
    {
        var source = PlanFixture.CreateV3WithTwoContexts();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var reordered = source with
        {
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { ContextRequirements = inference.ContextRequirements!.Reverse().ToArray() }
                : node).ToArray(),
        };
        var changed = source with
        {
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { ContextRequirements = [inference.ContextRequirements![0] with { Name = "changed" }, inference.ContextRequirements![1]] }
                : node).ToArray(),
        };

        WorkflowPlanIdentity.GetCanonicalBytes(reordered).Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(source));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(reordered).Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(source));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(changed).Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(source));
    }

    [Fact]
    public void V3_snapshot_detaches_requirement_graph()
    {
        var source = PlanFixture.CreateV3();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var requirements = inference.ContextRequirements!.ToArray();
        var plan = source with { Nodes = source.Nodes.Select(node => node == inference ? inference with { ContextRequirements = requirements } : node).ToArray() };
        var first = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        requirements[0] = requirements[0] with { Name = "mutated" };

        first.Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(source));
    }
}
