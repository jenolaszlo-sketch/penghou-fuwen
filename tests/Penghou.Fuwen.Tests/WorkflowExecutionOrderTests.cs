using FluentAssertions;
using System.Text.Json;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowExecutionOrderTests
{
    [Fact]
    public void Undefined_and_wrong_arity_condition_operators_are_rejected()
    {
        var source = PlanFixture.CreateV2WithConditional();
        var conditional = source.Nodes.OfType<ConditionalNode>().Single();

        WorkflowPlan WithCondition(ConditionExpression condition) => source with
        {
            Nodes = source.Nodes.Select(node => node == conditional
                ? conditional with { Condition = condition }
                : node).ToArray(),
        };

        var undefined = () => WorkflowPlanValidator.Validate(WithCondition(
            new ConditionExpression((ConditionOperator)999, new InputBinding([]), new InputBinding([]))));
        var unaryWithRight = () => WorkflowPlanValidator.Validate(WithCondition(
            new ConditionExpression(ConditionOperator.Not, new InputBinding([]), new InputBinding([]))));
        var binaryWithoutRight = () => WorkflowPlanValidator.Validate(WithCondition(
            new ConditionExpression(ConditionOperator.Equal, new InputBinding([]))));

        undefined.Should().Throw<ArgumentOutOfRangeException>();
        unaryWithRight.Should().Throw<ArgumentException>().WithMessage("*cannot have a right operand*");
        binaryWithoutRight.Should().Throw<ArgumentException>().WithMessage("*requires a right operand*");
    }

    [Fact]
    public void V2_requires_an_explicit_execution_order()
    {
        var plan = PlanFixture.Create() with
        {
            IrVersion = FuwenContracts.IrVersionV2,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV2,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV2,
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*requires an explicit execution order*");
    }

    [Fact]
    public void V2_requires_the_exact_compiler_semantics_contract()
    {
        var plan = PlanFixture.CreateV2() with { CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV1 };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<NotSupportedException>().WithMessage("*compiler-semantics/2*");
    }

    [Fact]
    public void V1_with_a_schedule_is_rejected_instead_of_being_reinterpreted()
    {
        var plan = PlanFixture.Create() with { ExecutionOrder = PlanFixture.CreateV2().ExecutionOrder };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*never silently upgraded*");
    }

    [Fact]
    public void Missing_and_duplicate_node_entries_are_rejected()
    {
        var plan = PlanFixture.CreateV2();
        var root = plan.ExecutionOrder!.Regions[0];

        var missing = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases = root.Phases.Take(3).ToArray(),
                    },
                ],
            },
        };
        var duplicate = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases =
                        [
                            root.Phases[0],
                            root.Phases[1],
                            root.Phases[2],
                            new WorkflowExecutionPhase(["answer/validate"]),
                            root.Phases[3],
                        ],
                    },
                ],
            },
        };

        var missingAct = () => WorkflowPlanIdentity.GetCanonicalBytes(missing);
        var duplicateAct = () => WorkflowPlanIdentity.GetCanonicalBytes(duplicate);

        missingAct.Should().Throw<ArgumentException>().WithMessage("*missing node path*");
        duplicateAct.Should().Throw<ArgumentException>().WithMessage("*appears more than once*");
    }

    [Fact]
    public void Unknown_wrongly_scoped_and_empty_phase_entries_are_rejected()
    {
        var plan = PlanFixture.CreateV2();
        var root = plan.ExecutionOrder!.Regions[0];

        var unknown = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases = root.Phases.Take(root.Phases.Count - 1)
                            .Append(new WorkflowExecutionPhase(["answer/missing"]))
                            .Append(root.Phases[^1])
                            .ToArray(),
                    },
                ],
            },
        };
        var empty = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases = root.Phases.Take(1)
                            .Append(new WorkflowExecutionPhase([]))
                            .Concat(root.Phases.Skip(1))
                            .ToArray(),
                    },
                ],
            },
        };

        var unknownAct = () => WorkflowPlanIdentity.GetCanonicalBytes(unknown);
        var emptyAct = () => WorkflowPlanIdentity.GetCanonicalBytes(empty);

        unknownAct.Should().Throw<ArgumentException>().WithMessage("*unknown node path*");
        emptyAct.Should().Throw<ArgumentException>().WithMessage("*empty phase*");
    }

    [Fact]
    public void Same_phase_and_forward_output_references_are_rejected()
    {
        var plan = PlanFixture.CreateV2();
        var root = plan.ExecutionOrder!.Regions[0];
        var samePhase = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases =
                        [
                            root.Phases[0],
                            new WorkflowExecutionPhase(["answer/infer", "answer/validate", "answer/audit"]),
                            root.Phases[3],
                        ],
                    },
                ],
            },
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(samePhase);

        act.Should().Throw<ArgumentException>().WithMessage("*earlier execution phase*");
    }

    [Fact]
    public void Branch_paths_must_be_scheduled_in_their_own_region()
    {
        var plan = PlanFixture.CreateV2WithConditional();
        var root = plan.ExecutionOrder!.Regions.Single(region => region.RegionPath == plan.Name);
        var thenPath = "answer/check/$then/then_work";
        var wronglyScoped = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions = plan.ExecutionOrder.Regions
                    .Select(region => region.RegionPath == plan.Name
                        ? region with
                        {
                            Phases = root.Phases
                                .Take(root.Phases.Count - 1)
                                .Append(new WorkflowExecutionPhase(new[] { thenPath }))
                                .Append(root.Phases[^1])
                                .ToArray(),
                        }
                        : region)
                    .ToArray(),
            },
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(wronglyScoped);

        act.Should().Throw<ArgumentException>().WithMessage("*wrongly scoped*");
    }

    [Fact]
    public void Raw_cross_region_output_references_are_rejected()
    {
        var plan = PlanFixture.CreateV2WithConditional(branchReadsRoot: true);

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*crosses execution regions*");
    }

    [Fact]
    public void Branch_local_returns_and_return_outputs_are_rejected()
    {
        var branchReturn = PlanFixture.CreateV2WithConditional(branchReturn: true);
        var outputSource = PlanFixture.CreateV2();
        var nodes = outputSource.Nodes.ToArray();
        var validate = (ActivityNode)nodes.Single(node => node.Name == "validate");
        nodes[Array.IndexOf(nodes, validate)] = validate with
        {
            Arguments = [new ArgumentBinding("return", new NodeOutputBinding("answer/return_result", []))],
        };
        outputSource = outputSource with { Nodes = nodes };

        var branchAct = () => WorkflowPlanIdentity.GetCanonicalBytes(branchReturn);
        var outputAct = () => WorkflowPlanIdentity.GetCanonicalBytes(outputSource);

        branchAct.Should().Throw<ArgumentException>().WithMessage("*Branch-local return*");
        outputAct.Should().Throw<ArgumentException>().WithMessage("*cannot be used as an output source*");
    }

    [Fact]
    public void Conditional_nodes_cannot_be_used_as_output_sources()
    {
        var plan = PlanFixture.CreateV2WithConditional();
        var nodes = plan.Nodes.ToArray();
        var validate = (ActivityNode)nodes.Single(node => node.Name == "validate");
        var changed = validate with
        {
            Arguments = [new ArgumentBinding("condition", new NodeOutputBinding("answer/check", []))],
        };
        nodes[Array.IndexOf(nodes, validate)] = changed;
        plan = plan with { Nodes = nodes };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*cannot be used as an output source*");
    }

    [Fact]
    public void Return_type_checks_cover_input_literals_node_outputs_and_projections()
    {
        var inputReturn = PlanFixture.CreateV2() with
        {
            Nodes = PlanFixture.CreateV2().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new InputBinding([]) }
                    : node)
                .ToArray(),
        };
        var literalReturn = PlanFixture.CreateV2() with
        {
            Nodes = PlanFixture.CreateV2().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new LiteralBinding(System.Text.Json.JsonDocument.Parse("true").RootElement) }
                    : node)
                .ToArray(),
        };
        var projected = PlanFixture.CreateV2() with
        {
            OutputType = new PrimitiveType(FuwenPrimitiveKind.String),
            Nodes = PlanFixture.CreateV2().Nodes
                .Select(node => node is ReturnNode @return
                    ? @return with { Value = new NodeOutputBinding("answer/infer", ["text"]) }
                    : node)
                .ToArray(),
        };

        var inputAct = () => WorkflowPlanIdentity.GetCanonicalBytes(inputReturn);
        var literalAct = () => WorkflowPlanIdentity.GetCanonicalBytes(literalReturn);

        inputAct.Should().Throw<ArgumentException>().WithMessage("*declared output type*");
        literalAct.Should().Throw<ArgumentException>().WithMessage("*literal value incompatible*");
        WorkflowPlanIdentity.GetCanonicalBytes(projected).Should().NotBeEmpty();
    }

    [Fact]
    public void Execution_order_is_deeply_snapshotted()
    {
        var plan = PlanFixture.CreateV2();
        var order = plan.ExecutionOrder!;
        var regions = (WorkflowExecutionRegion[])order.Regions;
        var phases = (WorkflowExecutionPhase[])regions[0].Phases;
        var paths = (string[])phases[0].NodePaths;
        var definition = WorkflowDefinitionDocument.Create(plan);
        var expected = definition.CanonicalBytes.ToArray();

        paths[0] = "answer/changed";

        definition.CanonicalBytes.ToArray().Should().Equal(expected);
    }

    [Fact]
    public void Snapshot_and_direct_validation_bound_aggregate_schedule_size()
    {
        var regionHeavy = PlanFixture.CreateV2() with
        {
            ExecutionOrder = new WorkflowExecutionOrder(
                Enumerable.Range(0, WorkflowPlanSnapshotLimits.MaximumExecutionRegions + 1)
                    .Select(_ => new WorkflowExecutionRegion("answer", Array.Empty<WorkflowExecutionPhase>()))
                    .ToArray()),
        };
        var phaseHeavy = PlanFixture.CreateV2() with
        {
            ExecutionOrder = new WorkflowExecutionOrder(
                new[]
                {
                    new WorkflowExecutionRegion(
                        "answer",
                        Enumerable.Range(0, WorkflowPlanSnapshotLimits.MaximumExecutionPhases + 1)
                            .Select(_ => new WorkflowExecutionPhase(new[] { "answer/context" }))
                            .ToArray()),
                }),
        };
        var entryHeavy = PlanFixture.CreateV2() with
        {
            ExecutionOrder = new WorkflowExecutionOrder(
                new[]
                {
                    new WorkflowExecutionRegion(
                        "answer",
                        new[]
                        {
                            new WorkflowExecutionPhase(
                                Enumerable.Repeat("answer/context", WorkflowPlanSnapshotLimits.MaximumExecutionEntries + 1)
                                    .ToArray()),
                        }),
                }),
        };

        var regionSnapshot = () => WorkflowDefinitionDocument.Create(regionHeavy);
        var phaseSnapshot = () => WorkflowDefinitionDocument.Create(phaseHeavy);
        var entrySnapshot = () => WorkflowDefinitionDocument.Create(entryHeavy);
        var regionDirect = () => WorkflowPlanValidator.Validate(regionHeavy);
        var phaseDirect = () => WorkflowPlanValidator.Validate(phaseHeavy);
        var entryDirect = () => WorkflowPlanValidator.Validate(entryHeavy);

        regionSnapshot.Should().Throw<InvalidOperationException>().WithMessage("*execution region count*");
        phaseSnapshot.Should().Throw<InvalidOperationException>().WithMessage("*execution phase count*");
        entrySnapshot.Should().Throw<InvalidOperationException>().WithMessage("*execution entry count*");
        regionDirect.Should().Throw<ArgumentException>().WithMessage("*region count*");
        phaseDirect.Should().Throw<ArgumentException>().WithMessage("*phase count*");
        entryDirect.Should().Throw<ArgumentException>().WithMessage("*entry count*");
    }

    [Fact]
    public void Regions_are_canonicalized_by_path_while_phase_order_is_preserved()
    {
        var plan = PlanFixture.CreateV2WithConditional();

        using var document = JsonDocument.Parse(WorkflowPlanIdentity.GetCanonicalBytes(plan));
        var regions = document.RootElement
            .GetProperty("executionOrder")
            .GetProperty("regions")
            .EnumerateArray()
            .Select(region => region.GetProperty("regionPath").GetString())
            .ToArray();
        var rootPhases = document.RootElement
            .GetProperty("executionOrder")
            .GetProperty("regions")
            .EnumerateArray()
            .Single(region => region.GetProperty("regionPath").GetString() == "answer")
            .GetProperty("phases")
            .EnumerateArray()
            .Select(phase => phase.GetProperty("nodePaths")[0].GetString())
            .ToArray();

        regions.Should().Equal("answer", "answer/check/$else", "answer/check/$then");
        rootPhases.Should().Equal(
            "answer/context",
            "answer/infer",
            "answer/audit",
            "answer/check",
            "answer/return_result");
    }
}
