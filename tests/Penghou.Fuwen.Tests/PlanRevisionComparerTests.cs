using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class PlanRevisionComparerTests
{
    [Fact]
    public void Compare_identical_revisions_is_unchanged_and_deterministic()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var revision = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());

        var comparison = PlanRevisionComparer.Compare(revision, definition, revision, definition);

        comparison.ExecutionFingerprintEqual.Should().BeTrue();
        comparison.Changes.Should().Equal(
            new PlanChange("answer/audit", PlanChangeKind.Unchanged),
            new PlanChange("answer/context", PlanChangeKind.Unchanged),
            new PlanChange("answer/infer", PlanChangeKind.Unchanged),
            new PlanChange("answer/return_result", PlanChangeKind.Unchanged),
            new PlanChange("answer/validate", PlanChangeKind.Unchanged));
        var repeated = PlanRevisionComparer.Compare(revision, definition, revision, definition);
        repeated.ExecutionFingerprintEqual.Should().Be(comparison.ExecutionFingerprintEqual);
        repeated.Changes.Should().Equal(comparison.Changes);
    }

    [Theory]
    [InlineData("objective", PlanChangeKind.ObjectiveChanged)]
    [InlineData("acceptance", PlanChangeKind.AcceptanceCriteriaChanged)]
    [InlineData("validation", PlanChangeKind.ValidationRequirementChanged)]
    public void Compare_reports_revision_semantic_changes(string changedSemantic, PlanChangeKind expectedKind)
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var before = PlanRevisionDocument.Create(definition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(definition, "revision/2", "revision/1", changedSemantic switch
        {
            "objective" => Semantics(objective: 'f'),
            "acceptance" => Semantics(acceptance: 'f'),
            "validation" => Semantics(validation: 'f'),
            _ => throw new ArgumentOutOfRangeException(nameof(changedSemantic)),
        });

        var comparison = PlanRevisionComparer.Compare(before, definition, after, definition);

        comparison.ExecutionFingerprintEqual.Should().BeTrue();
        comparison.Changes.Should().Contain(new PlanChange(null, expectedKind));
        comparison.Changes.Where(change => change.StructuralPath is null)
            .Should().ContainSingle(change => change.Kind == expectedKind);
        comparison.Changes.Where(change => change.StructuralPath is not null)
            .Should().OnlyContain(change => change.Kind == PlanChangeKind.Unchanged);
    }

    [Fact]
    public void Compare_treats_an_exact_path_rename_as_removed_and_added()
    {
        var beforePlan = PlanFixture.CreateV2();
        var oldPath = "answer/audit";
        var newPath = "answer/audit_v2";
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var renamedPlan = beforePlan with
        {
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == oldPath
                ? node with { Name = "audit_v2", StructuralPath = newPath }
                : node).ToArray(),
            ExecutionOrder = beforePlan.ExecutionOrder! with
            {
                Regions = beforePlan.ExecutionOrder.Regions.Select(region => region with
                {
                    Phases = region.Phases.Select(phase => phase with
                    {
                        NodePaths = phase.NodePaths.Select(path => path == oldPath ? newPath : path).ToArray(),
                    }).ToArray(),
                }).ToArray(),
            },
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(renamedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(oldPath, PlanChangeKind.Removed));
        comparison.Changes.Should().Contain(new PlanChange(newPath, PlanChangeKind.Added));
        comparison.Changes.Should().NotContain(new PlanChange(oldPath, PlanChangeKind.Changed));
        comparison.Changes.Should().NotContain(new PlanChange(newPath, PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_dependency_only_binding_change()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var inferencePath = "answer/infer";
        var changedPlan = beforePlan with
        {
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == inferencePath
                ? ((InferenceNode)node) with
                {
                    Arguments = [new ArgumentBinding("request", new InputBinding(["question"]))],
                }
                : node).ToArray(),
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(inferencePath, PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().NotContain(new PlanChange(inferencePath, PlanChangeKind.Changed));
        comparison.Changes.Where(change => change.StructuralPath is null)
            .Should().BeEmpty();
    }

    [Fact]
    public void Compare_reports_descriptor_change_as_node_semantic_change()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var inference = (InferenceNode)beforePlan.Nodes.Single(node => node.StructuralPath == "answer/infer");
        var changedProfile = inference.Profile with
        {
            ContentDigest = new ContentDigest("sha256", "descriptor/v1", new string('f', 64)),
        };
        var changedPlan = beforePlan with
        {
            CatalogueBindings = beforePlan.CatalogueBindings.Select(descriptor => descriptor == inference.Profile ? changedProfile : descriptor).ToArray(),
            Nodes = beforePlan.Nodes.Select(node => node.StructuralPath == inference.StructuralPath
                ? inference with { Profile = changedProfile }
                : node).ToArray(),
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.Changes.Should().Contain(new PlanChange(inference.StructuralPath, PlanChangeKind.Changed));
        comparison.Changes.Should().NotContain(new PlanChange(inference.StructuralPath, PlanChangeKind.DependencyChanged));
    }

    [Fact]
    public void Compare_reports_simultaneous_root_contract_and_execution_order_changes()
    {
        var beforePlan = PlanFixture.CreateV2();
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var rootRegion = beforePlan.ExecutionOrder!.Regions.Single();
        var changedOrder = beforePlan.ExecutionOrder with
        {
            Regions =
            [
                rootRegion with
                {
                    Phases =
                    [
                        rootRegion.Phases[0],
                        rootRegion.Phases[1],
                        new WorkflowExecutionPhase(["answer/validate"]),
                        new WorkflowExecutionPhase(["answer/audit"]),
                        rootRegion.Phases[3],
                    ],
                },
            ],
        };
        var afterPlan = beforePlan with
        {
            RoutingPolicyRevision = "routing/2",
            ExecutionOrder = changedOrder,
        };
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.Changes.Where(change => change.StructuralPath is null).Should().Equal(
            new PlanChange(null, PlanChangeKind.Changed),
            new PlanChange(null, PlanChangeKind.DependencyChanged));
    }

    [Fact]
    public void Comparison_result_defensively_snapshots_changes()
    {
        var changes = new List<PlanChange> { new("answer/context", PlanChangeKind.Unchanged) };
        var comparison = new PlanRevisionComparison(true, changes);

        changes.Clear();

        comparison.Changes.Should().ContainSingle()
            .Which.Should().Be(new PlanChange("answer/context", PlanChangeKind.Unchanged));
    }

    [Fact]
    public void Compare_rejects_an_integrity_verified_but_structurally_invalid_definition()
    {
        var validDefinition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var malformedPlan = PlanFixture.CreateV2() with
        {
            Nodes = [.. PlanFixture.CreateV2().Nodes, PlanFixture.CreateV2().Nodes[0]],
        };
        var bytes = WorkflowPlanIdentity.GetCanonicalBytesForVerification(malformedPlan);
        var fingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(bytes, malformedPlan.FingerprintVersion);
        var malformedDefinition = WorkflowDefinitionDocument.LoadVerified(fingerprint, bytes);
        var malformedRevision = PlanRevisionDocument.Create(malformedDefinition, "revision/invalid", null, Semantics());
        var validRevision = PlanRevisionDocument.Create(validDefinition, "revision/valid", null, Semantics());

        var act = () => PlanRevisionComparer.Compare(
            malformedRevision,
            malformedDefinition,
            validRevision,
            validDefinition);

        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("beforeDefinition");
    }

    [Fact]
    public void Compare_rejects_a_revision_bound_to_a_different_definition()
    {
        var beforeDefinition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var unrelatedDefinition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(beforeDefinition, "revision/2", "revision/1", Semantics());

        var act = () => PlanRevisionComparer.Compare(before, unrelatedDefinition, after, beforeDefinition);

        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("beforeDefinition");
    }

    [Fact]
    public void Compare_reports_conditional_merge_result_type_change_as_semantic_change()
    {
        // R15: altering Merge.ResultType must not compare as Unchanged.
        var beforePlan = PlanFixture.CreateV5();
        var conditional = beforePlan.Nodes.OfType<ConditionalNode>().Single();
        var changedPlan = beforePlan with
        {
            OutputType = new PrimitiveType(FuwenPrimitiveKind.Integer),
            Nodes = beforePlan.Nodes.Select(node => node is ConditionalNode value &&
                string.Equals(value.StructuralPath, conditional.StructuralPath, StringComparison.Ordinal)
                ? value with { Merge = value.Merge! with { ResultType = new PrimitiveType(FuwenPrimitiveKind.Integer) } }
                : node).ToArray(),
        };
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(conditional.StructuralPath, PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_conditional_merge_binding_change_as_dependency_change()
    {
        // R15: retargeting a merge branch binding keeps the result type, so
        // the change is a dependency change rather than a semantic change.
        var beforePlan = PlanFixture.CreateV5();
        var conditional = beforePlan.Nodes.OfType<ConditionalNode>().Single();
        using var document = System.Text.Json.JsonDocument.Parse("\"retargeted\"");
        var changedPlan = beforePlan with
        {
            Nodes = beforePlan.Nodes.Select(node => node is ConditionalNode value &&
                string.Equals(value.StructuralPath, conditional.StructuralPath, StringComparison.Ordinal)
                ? value with { Merge = value.Merge! with { ThenValue = new LiteralBinding(document.RootElement.Clone()) } }
                : node).ToArray(),
        };
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var afterDefinition = WorkflowDefinitionDocument.Create(changedPlan);
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.Changes.Should().Contain(new PlanChange(conditional.StructuralPath, PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().NotContain(new PlanChange(conditional.StructuralPath, PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_prompt_binding_literal_change_as_dependency_change()
    {
        var beforePlan = PromptPlan(
            new LiteralBinding(System.Text.Json.JsonDocument.Parse("\"before\"").RootElement.Clone()),
            new InputBinding(["question"]));
        var afterPlan = PromptPlan(
            new LiteralBinding(System.Text.Json.JsonDocument.Parse("\"after\"").RootElement.Clone()),
            new InputBinding(["question"]));
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().NotContain(new PlanChange("answer/infer", PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_treats_prompt_binding_order_as_canonical_equivalence()
    {
        var first = new LiteralBinding(System.Text.Json.JsonDocument.Parse("{\"a\":1,\"b\":2}").RootElement.Clone());
        var second = new InputBinding(["question"]);
        var beforePlan = PromptPlan(first, second);
        var afterPlan = PromptPlan(first, second, reverseBindingOrder: true);
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeTrue();
        comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.Unchanged));
    }

    [Fact]
    public void Compare_attributes_prompt_definition_change_to_referencing_inference_node()
    {
        var first = new LiteralBinding(System.Text.Json.JsonDocument.Parse("\"first\"").RootElement.Clone());
        var second = new InputBinding(["question"]);
        var beforePlan = PromptPlan(first, second, promptMessage: "{{ first }} {{ second }}");
        var afterPlan = PromptPlan(first, second, promptMessage: "Answer: {{ first }} {{ second }}");
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange(null, PlanChangeKind.Changed));
        comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_prompt_binding_input_projection_change_as_dependency_change()
    {
        var first = new LiteralBinding(System.Text.Json.JsonDocument.Parse("\"first\"").RootElement.Clone());
        var beforePlan = PromptPlan(first, new InputBinding(["question"]));
        var afterPlan = PromptPlan(first, new InputBinding(["alternate"]));
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().NotContain(new PlanChange("answer/infer", PlanChangeKind.Changed));
    }

    [Fact]
    public void Compare_reports_prompt_binding_source_node_change_without_claiming_downstream_reuse()
    {
        var beforePlan = PromptPlanWithNodeSource("prompt_source_a");
        var afterPlan = PromptPlanWithNodeSource("prompt_source_b");
        var beforeDefinition = WorkflowDefinitionDocument.Create(beforePlan);
        var afterDefinition = WorkflowDefinitionDocument.Create(afterPlan);
        var before = PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics());
        var after = PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics());

        var comparison = PlanRevisionComparer.Compare(before, beforeDefinition, after, afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(new PlanChange("answer/infer", PlanChangeKind.DependencyChanged));
        comparison.Changes.Should().Contain(new PlanChange("answer/return_result", PlanChangeKind.Unchanged));
        comparison.Changes.Should().Contain(new PlanChange("answer/prompt_source_a", PlanChangeKind.Unchanged));
        comparison.Changes.Should().Contain(new PlanChange("answer/prompt_source_b", PlanChangeKind.Unchanged));
    }

    private static WorkflowPlan PromptPlan(
        Binding first,
        Binding second,
        bool reverseBindingOrder = false,
        string promptMessage = "{{ first }} {{ second }}")
    {
        var source = PlanFixture.CreateV2();
        var inputSchema = ((NamedTypeReference)source.InputType).Schema;
        var prompt = new PromptDefinition(
            "answer_prompt",
            [
                new PromptParameter("first", new PrimitiveType(FuwenPrimitiveKind.String)),
                new PromptParameter("second", new PrimitiveType(FuwenPrimitiveKind.String)),
            ],
            [new PromptMessage(PromptMessageRole.User, promptMessage)]);
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        return source with
        {
            IrVersion = FuwenContracts.IrVersionV8,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV8,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV8,
            Schemas = source.Schemas.Select(schema => schema is ObjectSchemaDefinition value && value.Descriptor == inputSchema
                ? value with
                {
                    Fields = [.. value.Fields, new SchemaField("alternate", new PrimitiveType(FuwenPrimitiveKind.String))],
                }
                : schema).ToArray(),
            Prompts = [prompt],
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with
                {
                    PromptTemplate = null,
                    Arguments = [],
                    ContextSnapshots = [],
                    ContextRequirements = [],
                    PromptName = prompt.Name,
                    PromptBindings = reverseBindingOrder
                        ? [new PromptBinding("second", second), new PromptBinding("first", first)]
                        : [new PromptBinding("first", first), new PromptBinding("second", second)],
                }
                : node).ToArray(),
        };
    }

    private static WorkflowPlan PromptPlanWithNodeSource(string sourceName)
    {
        var source = PlanFixture.CreateV2();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var activity = source.Nodes.OfType<ActivityNode>().First().Activity;
        var sourceAPath = StructuralNodeIdentity.Create(source.Name, "prompt_source_a");
        var sourceBPath = StructuralNodeIdentity.Create(source.Name, "prompt_source_b");
        var prompt = new PromptDefinition(
            "answer_prompt",
            [new PromptParameter("value", new PrimitiveType(FuwenPrimitiveKind.String))],
            [new PromptMessage(PromptMessageRole.User, "{{ value }}")]);
        var sourceNodes = new WorkflowNode[]
        {
            new ActivityNode("prompt_source_a", sourceAPath, activity, [], new PrimitiveType(FuwenPrimitiveKind.String)),
            new ActivityNode("prompt_source_b", sourceBPath, activity, [], new PrimitiveType(FuwenPrimitiveKind.String)),
        };
        var phases = source.ExecutionOrder!.Regions.Single().Phases;
        return source with
        {
            IrVersion = FuwenContracts.IrVersionV8,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV8,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV8,
            Prompts = [prompt],
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with
                {
                    PromptTemplate = null,
                    Arguments = [],
                    ContextSnapshots = [],
                    ContextRequirements = [],
                    PromptName = prompt.Name,
                    PromptBindings = [new PromptBinding("value", new NodeOutputBinding($"answer/{sourceName}", []))],
                }
                : node).Concat(sourceNodes).ToArray(),
            ExecutionOrder = source.ExecutionOrder with
            {
                Regions =
                [
                    source.ExecutionOrder.Regions.Single() with
                    {
                        Phases = [phases[0], new WorkflowExecutionPhase([sourceAPath, sourceBPath]), .. phases.Skip(1)],
                    },
                ],
            },
        };
    }

    private static PlanRevisionSemantics Semantics(
        char objective = 'a',
        char acceptance = 'b',
        char validation = 'c') => new(
        Digest("objective/v1", objective),
        Digest("acceptance/v1", acceptance),
        Digest("validation/v1", validation));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, new string(value, 64));
}
