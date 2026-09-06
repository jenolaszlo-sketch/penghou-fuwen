using System.Text;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowPlanIdentityTests
{
    [Fact]
    public void Structural_identity_does_not_depend_on_sibling_order()
    {
        var plan = PlanFixture.Create();
        var moved = plan with { Nodes = plan.Nodes.Reverse().ToArray() };

        WorkflowPlanIdentity.GetCanonicalBytes(moved)
            .Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(plan));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(moved)
            .Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
    }

    [Fact]
    public void Inserting_a_sibling_preserves_existing_paths_but_changes_plan_identity()
    {
        var plan = PlanFixture.Create();
        var insertedPath = StructuralNodeIdentity.Create(plan.Name, "audit");
        var inserted = new ActivityNode(
            "audit",
            insertedPath,
            PlanFixture.Descriptor(DescriptorKind.Activity, "sample.audit"),
            [],
            new PrimitiveType(FuwenPrimitiveKind.Boolean));
        var changed = plan with
        {
            CatalogueBindings = [inserted.Activity, .. plan.CatalogueBindings],
            Nodes = [inserted, .. plan.Nodes],
        };

        changed.Nodes.Skip(1).Select(node => node.StructuralPath)
            .Should().Equal(plan.Nodes.Select(node => node.StructuralPath));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(changed)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
    }

    [Fact]
    public void Descriptor_and_routing_policy_are_execution_identity_inputs()
    {
        var plan = PlanFixture.Create();
        var inference = (InferenceNode)plan.Nodes.Single(node => node.Name == "infer");
        var changedDescriptor = inference.PromptTemplate with
        {
            ContentDigest = new ContentDigest("sha256", "descriptor/v1", new string('f', 64)),
        };

        var descriptorPlan = plan with
        {
            CatalogueBindings = plan.CatalogueBindings.Select(item => item == inference.PromptTemplate ? changedDescriptor : item).ToArray(),
            Nodes = plan.Nodes.Select(node => node == inference ? inference with { PromptTemplate = changedDescriptor } : node).ToArray(),
        };
        var policyPlan = plan with { RoutingPolicyRevision = "routing/2" };

        WorkflowPlanIdentity.ComputeExecutionFingerprint(descriptorPlan)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(policyPlan)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
    }

    [Fact]
    public void Plan_has_a_stable_golden_fingerprint()
    {
        var plan = PlanFixture.Create();
        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "golden", "workflow_plan_v1.json");
        var goldenFile = File.ReadAllBytes(goldenPath);
        var goldenLength = goldenFile.Length;
        while (goldenLength > 0 && goldenFile[goldenLength - 1] is (byte)'\r' or (byte)'\n')
            goldenLength--;
        var goldenBytes = goldenFile.AsSpan(0, goldenLength).ToArray();

        canonicalBytes.Should().Equal(goldenBytes);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().Be("sha256:fuwen-execution/v1:e3a76cc4128c703637fd14555e95725908f3170c69831033413f5f9a11bfad8a");
    }

    [Fact]
    public void V2_plan_has_an_explicit_execution_order_and_stable_golden_fingerprint()
    {
        var plan = PlanFixture.CreateV2();
        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "golden", "workflow_plan_v2.json");
        var goldenFile = File.ReadAllBytes(goldenPath);
        var goldenLength = goldenFile.Length;
        while (goldenLength > 0 && goldenFile[goldenLength - 1] is (byte)'\r' or (byte)'\n')
            goldenLength--;

        canonicalBytes.Should().Equal(goldenFile.AsSpan(0, goldenLength).ToArray());
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().Be("sha256:fuwen-execution/v2:2bf5c628bcecfdb0970730bc160ee10f2bcbf326875f30430e5b832fafe96571");
    }

    [Fact]
    public void V2_phase_order_cannot_move_a_data_dependency_forward()
    {
        var plan = PlanFixture.CreateV2();
        var order = plan.ExecutionOrder!;
        var phaseOrderChanged = plan with
        {
            ExecutionOrder = order with
            {
                Regions =
                [
                    order.Regions[0] with
                    {
                        Phases =
                        [
                            order.Regions[0].Phases[0],
                            order.Regions[0].Phases[2],
                            order.Regions[0].Phases[1],
                            order.Regions[0].Phases[3],
                        ],
                    },
                ],
            },
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(phaseOrderChanged);

        act.Should().Throw<ArgumentException>().WithMessage("*earlier execution phase*");
    }

    [Fact]
    public void V2_node_order_inside_a_phase_is_not_executable_semantics()
    {
        var plan = PlanFixture.CreateV2();
        var root = plan.ExecutionOrder!.Regions[0];
        var reordered = plan with
        {
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases = root.Phases.Select((phase, index) => index == 2
                            ? phase with { NodePaths = phase.NodePaths.Reverse().ToArray() }
                            : phase).ToArray(),
                    },
                ],
            },
        };

        WorkflowPlanIdentity.GetCanonicalBytes(reordered)
            .Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(plan));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(reordered)
            .Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
    }

    [Fact]
    public void Plan_round_trips_to_identical_canonical_bytes()
    {
        var bytes = WorkflowPlanIdentity.GetCanonicalBytes(PlanFixture.Create());

        var reloaded = CanonicalJson.Deserialize<WorkflowPlan>(bytes);

        WorkflowPlanIdentity.GetCanonicalBytes(reloaded).Should().Equal(bytes);
    }

    [Fact]
    public void Source_positions_do_not_change_execution_identity()
    {
        var plan = PlanFixture.Create();
        var fingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(plan);
        var sourceDigest = new ContentDigest("sha256", "fuwen-source/v1", new string('b', 64));
        var document = new SourceDocumentReference("main", "workflow.fuwen", sourceDigest, 1_000);
        var first = new WorkflowSourceMap(
            fingerprint,
            sourceDigest,
            [document],
            [new SourceMapEntry(plan.Nodes[0].StructuralPath, new SourceSpan("main", 10, 20, 1, 1))]);
        var moved = first with
        {
            Entries = [new SourceMapEntry(plan.Nodes[0].StructuralPath, new SourceSpan("main", 100, 20, 8, 4))],
        };

        WorkflowSourceMapValidator.ValidateForPlan(first, plan);
        WorkflowSourceMapValidator.ValidateForPlan(moved, plan);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan).Should().Be(fingerprint);
        CanonicalJson.Serialize(first).Should().NotEqual(CanonicalJson.Serialize(moved));
    }

    [Fact]
    public void Schema_field_order_is_not_executable_semantics()
    {
        var plan = PlanFixture.Create();
        var request = (ObjectSchemaDefinition)plan.Schemas.Single(schema => schema.Descriptor.Name == "sample.request");
        var reordered = plan with
        {
            Schemas = plan.Schemas.Select(schema => schema == request ? request with { Fields = request.Fields.Reverse().ToArray() } : schema).ToArray(),
        };

        WorkflowPlanIdentity.GetCanonicalBytes(reordered)
            .Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(plan));
    }

    [Fact]
    public void Unsupported_contract_is_rejected()
    {
        var plan = PlanFixture.Create() with { IrVersion = "fuwen-ir/v99" };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(plan);

        act.Should().Throw<NotSupportedException>().WithMessage("*fuwen-ir/v99*");
    }

    [Fact]
    public void Named_schema_without_exact_definition_is_rejected()
    {
        var plan = PlanFixture.Create();
        var request = ((NamedTypeReference)plan.InputType).Schema;
        var incomplete = plan with
        {
            Schemas = plan.Schemas.Where(schema => schema.Descriptor != request).ToArray(),
        };

        var act = () => WorkflowPlanIdentity.GetCanonicalBytes(incomplete);

        act.Should().Throw<ArgumentException>().WithMessage("*has no resolved definition*");
    }

    [Fact]
    public void Source_map_for_another_plan_is_rejected()
    {
        var plan = PlanFixture.Create();
        var digest = new ContentDigest("sha256", "fuwen-source/v1", new string('b', 64));
        var sourceMap = new WorkflowSourceMap(
            "sha256:fuwen-execution/v1:not-this-plan",
            digest,
            [new SourceDocumentReference("main", "workflow.fuwen", digest, 100)],
            [new SourceMapEntry(plan.Nodes[0].StructuralPath, new SourceSpan("main", 0, 10, 0, 0))]);

        var act = () => WorkflowSourceMapValidator.ValidateForPlan(sourceMap, plan);

        act.Should().Throw<ArgumentException>().WithMessage("*does not match*");
    }

    [Theory]
    [InlineData("source-tree")]
    [InlineData("cited-document")]
    [InlineData("dataset")]
    [InlineData("generated-video")]
    public void Artifact_contract_is_domain_neutral(string kind)
    {
        var descriptor = PlanFixture.Descriptor(DescriptorKind.Artifact, kind);
        var reference = new ArtifactReference(
            "example-provider",
            $"artifact-{kind}",
            descriptor,
            new ContentDigest("sha256", "bytes/v1", new string('a', 64)),
            42,
            kind);

        var serialized = Encoding.UTF8.GetString(CanonicalJson.Serialize(reference));

        serialized.Should().Contain(kind);
        serialized.ToLowerInvariant().Should().NotContain("path");
    }
}

internal static class PlanFixture
{
    internal static WorkflowPlan Create()
    {
        var request = Descriptor(DescriptorKind.Schema, "sample.request");
        var answer = Descriptor(DescriptorKind.Schema, "sample.answer");
        var severity = Descriptor(DescriptorKind.Schema, "sample.severity");
        var contextProvider = Descriptor(DescriptorKind.ContextProvider, "sample.context");
        var profile = Descriptor(DescriptorKind.InferenceProfile, "sample.reasoning");
        var template = Descriptor(DescriptorKind.PromptTemplate, "sample.answer-template");
        var activity = Descriptor(DescriptorKind.Activity, "sample.validate");
        var contextSnapshot = Descriptor(DescriptorKind.Artifact, "context-snapshot");
        ResolvedSchemaDefinition[] schemas =
        [
            new ObjectSchemaDefinition(
                request,
                [
                    new SchemaField("question", new PrimitiveType(FuwenPrimitiveKind.String)),
                    new SchemaField("tags", new OptionalType(new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 20))),
                ]),
            new ObjectSchemaDefinition(
                answer,
                [new SchemaField("text", new PrimitiveType(FuwenPrimitiveKind.String))]),
            new EnumSchemaDefinition(
                severity,
                [new EnumMember("High", "high"), new EnumMember("Low", "low")]),
        ];

        var contextPath = StructuralNodeIdentity.Create("answer", "context");
        var inferencePath = StructuralNodeIdentity.Create("answer", "infer");
        var validatePath = StructuralNodeIdentity.Create("answer", "validate");
        var returnPath = StructuralNodeIdentity.Create("answer", "return_result");

        WorkflowNode[] nodes =
        [
            new ContextNode(
                "context",
                contextPath,
                contextProvider,
                [new ArgumentBinding("request", new InputBinding([]))],
                new ArtifactType(contextSnapshot)),
            new InferenceNode(
                "infer",
                inferencePath,
                profile,
                template,
                [new ArgumentBinding("request", new InputBinding([]))],
                [new NodeOutputBinding(contextPath, [])],
                new NamedTypeReference(answer)),
            new ActivityNode(
                "validate",
                validatePath,
                activity,
                [new ArgumentBinding("answer", new NodeOutputBinding(inferencePath, []))],
                new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferencePath, [])),
        ];

        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            "compiler-semantics/1",
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "answer",
            "1",
            new NamedTypeReference(request),
            new NamedTypeReference(answer),
            "routing/1",
            schemas,
            [activity, answer, contextProvider, contextSnapshot, profile, request, severity, template],
            new CapabilityManifest([new CapabilityRequirement("inference"), new CapabilityRequirement("context.read")]),
            nodes);
    }

    internal static WorkflowPlan CreateV2()
    {
        var v1 = Create();
        var audit = Descriptor(DescriptorKind.Activity, "sample.audit");
        var auditPath = StructuralNodeIdentity.Create(v1.Name, "audit");
        var nodes = v1.Nodes
            .Concat(new WorkflowNode[]
            {
                new ActivityNode(
                    "audit",
                    auditPath,
                    audit,
                    [],
                    new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            })
            .ToArray();
        return v1 with
        {
            IrVersion = FuwenContracts.IrVersionV2,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersionV2,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersionV2,
            CatalogueBindings = [audit, .. v1.CatalogueBindings],
            Nodes = nodes,
            ExecutionOrder = new WorkflowExecutionOrder(
                new[]
                {
                    new WorkflowExecutionRegion(
                        "answer",
                        new[]
                        {
                            new WorkflowExecutionPhase(new[] { "answer/context" }),
                            new WorkflowExecutionPhase(new[] { "answer/infer" }),
                            new WorkflowExecutionPhase(new[] { "answer/validate", auditPath }),
                            new WorkflowExecutionPhase(new[] { "answer/return_result" }),
                        }),
                }),
        };
    }

    internal static WorkflowPlan CreateV2WithConditional(bool branchReturn = false, bool branchReadsRoot = false)
    {
        var plan = CreateV2();
        var checkPath = StructuralNodeIdentity.Create(plan.Name, "check");
        var branchPath = $"{checkPath}/$then/then_work";
        var branchNode = branchReturn
            ? (WorkflowNode)new ReturnNode("then_work", branchPath, new InputBinding([]))
            : new ActivityNode(
                "then_work",
                branchPath,
                Descriptor(DescriptorKind.Activity, "sample.validate"),
                branchReadsRoot
                    ? [new ArgumentBinding("answer", new NodeOutputBinding("answer/infer", []))]
                    : [],
                new PrimitiveType(FuwenPrimitiveKind.Boolean));
        var conditional = new ConditionalNode(
            "check",
            checkPath,
            new ConditionExpression(ConditionOperator.Exists, new InputBinding([])),
            [branchNode],
            []);
        var nodes = plan.Nodes.ToArray();
        var context = nodes.Single(node => node.Name == "context");
        var inference = nodes.Single(node => node.Name == "infer");
        var validate = nodes.Single(node => node.Name == "validate");
        var audit = nodes.Single(node => node.Name == "audit");
        var @return = nodes.Single(node => node.Name == "return_result");
        nodes = [context, inference, validate, audit, conditional, @return];
        return plan with
        {
            Nodes = nodes,
            ExecutionOrder = new WorkflowExecutionOrder(
                new[]
                {
                    new WorkflowExecutionRegion(
                        $"{checkPath}/$else",
                        Array.Empty<WorkflowExecutionPhase>()),
                    new WorkflowExecutionRegion(
                        plan.Name,
                        new[]
                        {
                            new WorkflowExecutionPhase(new[] { "answer/context" }),
                            new WorkflowExecutionPhase(new[] { "answer/infer" }),
                            new WorkflowExecutionPhase(new[] { "answer/validate", "answer/audit" }),
                            new WorkflowExecutionPhase(new[] { checkPath }),
                            new WorkflowExecutionPhase(new[] { "answer/return_result" }),
                        }),
                    new WorkflowExecutionRegion(
                        $"{checkPath}/$then",
                        new[] { new WorkflowExecutionPhase(new[] { branchPath }) }),
                }),
        };
    }

    internal static DescriptorReference Descriptor(DescriptorKind kind, string name) => new(
        kind,
        name,
        "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
}
