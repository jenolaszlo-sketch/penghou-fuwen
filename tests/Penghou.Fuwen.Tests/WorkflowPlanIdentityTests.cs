using System.Text;
using System.Text.Json;
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
        var root = plan.ExecutionOrder!.Regions.Single(region => region.RegionPath == plan.Name);
        var changed = plan with
        {
            CatalogueBindings = [inserted.Activity, .. plan.CatalogueBindings],
            Nodes = [inserted, .. plan.Nodes],
            ExecutionOrder = plan.ExecutionOrder with
            {
                Regions =
                [
                    root with
                    {
                        Phases = [.. root.Phases.Take(3), new WorkflowExecutionPhase([insertedPath]), root.Phases[3]],
                    },
                ],
            },
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
        var changedDescriptor = inference.PromptTemplate! with
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
            .Should().Be("sha256:fuwen-execution/v1:abefd1352b2211428a098711f49b813a97c71babe10368184b2169a9f73b7cfb");
    }

    [Fact]
    public void Plan_with_audit_node_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV2();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        plan.CompilerSemanticVersion.Should().Be(FuwenContracts.CompilerSemanticVersion);
        plan.FingerprintVersion.Should().Be(FuwenContracts.ExecutionFingerprintVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_typed_context_requirements_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV3();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var inference = plan.Nodes.OfType<InferenceNode>().Single();
        inference.ContextSnapshots.Should().BeEmpty();
        inference.ContextRequirements.Should().NotBeNull();
        inference.Protocol.Should().NotBeNull();
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_fan_out_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV4();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_conditional_merge_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV5();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_repeat_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV6();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_checkpoint_and_wait_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV7();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");
        WorkflowPlanIdentity.GetCanonicalBytes(plan).Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_with_workflow_owned_prompt_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV8();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");

        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        canonicalBytes.Should().NotBeEmpty();
        var restored = CanonicalJson.Deserialize<WorkflowPlan>(canonicalBytes);
        WorkflowPlanIdentity.GetCanonicalBytes(restored).Should().Equal(canonicalBytes);
    }

    [Fact]
    public void Plan_with_bounded_protocol_limits_has_stable_current_identity()
    {
        var plan = PlanFixture.CreateV9();

        plan.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        canonicalBytes.Should().NotBeEmpty();
        WorkflowPlanIdentity.ComputeExecutionFingerprint(plan)
            .Should().StartWith("sha256:fuwen-execution/v1:");

        var restored = CanonicalJson.Deserialize<WorkflowPlan>(canonicalBytes);
        WorkflowPlanIdentity.GetCanonicalBytes(restored).Should().Equal(canonicalBytes);
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("v3")]
    [InlineData("v4")]
    [InlineData("v5")]
    [InlineData("v6")]
    [InlineData("v7")]
    [InlineData("v8")]
    [InlineData("v9")]
    public void All_current_fixtures_round_trip_to_identical_canonical_bytes(string fixture)
    {
        var plan = fixture switch
        {
            "v2" => PlanFixture.CreateV2(),
            "v3" => PlanFixture.CreateV3(),
            "v4" => PlanFixture.CreateV4(),
            "v5" => PlanFixture.CreateV5(),
            "v6" => PlanFixture.CreateV6(),
            "v7" => PlanFixture.CreateV7(),
            "v8" => PlanFixture.CreateV8(),
            "v9" => PlanFixture.CreateV9(),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };

        var canonicalBytes = WorkflowPlanIdentity.GetCanonicalBytes(plan);
        var reloaded = CanonicalJson.Deserialize<WorkflowPlan>(canonicalBytes);

        WorkflowPlanIdentity.GetCanonicalBytes(reloaded).Should().Equal(canonicalBytes);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(reloaded).Should().StartWith("sha256:fuwen-execution/v1:");
    }

    [Fact]
    public void Reordered_execution_phases_are_rejected()
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
    public void Node_order_inside_a_phase_is_not_executable_semantics()
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
                [],
                new NamedTypeReference(answer),
                [new ContextRequirement("context", new NodeOutputBinding(contextPath, []), new ArtifactType(contextSnapshot))],
                Protocol: new InferenceProtocol(new InferenceProtocolLimits(maxTurns: 1, maxModelCalls: 1))),
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
            nodes,
            new WorkflowExecutionOrder(
                [new WorkflowExecutionRegion(
                    "answer",
                    [
                        new WorkflowExecutionPhase([contextPath]),
                        new WorkflowExecutionPhase([inferencePath]),
                        new WorkflowExecutionPhase([validatePath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ])]));
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
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
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

    internal static WorkflowPlan CreateV3()
    {
        var v2 = CreateV2();
        var context = v2.Nodes.OfType<ContextNode>().Single();
        var inference = v2.Nodes.OfType<InferenceNode>().Single();
        var requirement = new ContextRequirement(
            "answer_context",
            new NodeOutputBinding(context.StructuralPath, []),
            context.OutputType);
        return v2 with
        {
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
            Nodes = v2.Nodes.Select(node => node == inference
                ? inference with { ContextSnapshots = [], ContextRequirements = [requirement] }
                : node).ToArray(),
        };
    }

    internal static WorkflowPlan CreateV3WithTwoContexts()
    {
        var source = CreateV3();
        var context = source.Nodes.OfType<ContextNode>().Single();
        var secondPath = StructuralNodeIdentity.Create(source.Name, "context2");
        var second = context with { Name = "context2", StructuralPath = secondPath };
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var requirements = new[]
        {
            inference.ContextRequirements![0],
            new ContextRequirement("second_context", new NodeOutputBinding(secondPath, []), second.OutputType),
        };
        var phases = source.ExecutionOrder!.Regions[0].Phases;
        return source with
        {
            Nodes = source.Nodes.Select(node => node switch
            {
                ContextNode value when value.StructuralPath == context.StructuralPath => value,
                InferenceNode value => value with
                {
                    ContextRequirements = requirements,
                    ContextSnapshots = [],
                },
                _ => node,
            }).Prepend(second).ToArray(),
            ExecutionOrder = source.ExecutionOrder with
            {
                Regions = [source.ExecutionOrder.Regions[0] with { Phases = [new WorkflowExecutionPhase([context.StructuralPath, secondPath]), .. phases.Skip(1)] }],
            },
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

    internal static WorkflowPlan CreateV4()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var contextProvider = Descriptor(DescriptorKind.ContextProvider, "sample.context");
        var contextPath = StructuralNodeIdentity.Create("demo", "ctx");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [contextProvider],
            new CapabilityManifest([]),
            [
                new ContextNode("ctx", contextPath, contextProvider, [new ArgumentBinding("input", new InputBinding([]))], str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(contextPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([contextPath]), new WorkflowExecutionPhase([returnPath])]),
            ]));
    }

    internal static WorkflowPlan CreateV5()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = Descriptor(DescriptorKind.Activity, "sample.validate");
        var checkPath = StructuralNodeIdentity.Create("demo", "check");
        var thenPath = checkPath + "/$then/work";
        var elsePath = checkPath + "/$else/work";
        var mergePath = checkPath + "/$merge";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var conditional = new ConditionalNode(
            "check", checkPath,
            new ConditionExpression(ConditionOperator.Exists, new InputBinding([])),
            [new ActivityNode("work", thenPath, activity, [], str)],
            [new ActivityNode("work", elsePath, activity, [], str)],
            new ConditionalMerge(
                new NodeOutputBinding(thenPath, []),
                new NodeOutputBinding(elsePath, []),
                str));
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [activity],
            new CapabilityManifest([]),
            [conditional, new ReturnNode("return_result", returnPath, new NodeOutputBinding(checkPath, []))],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([checkPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/check/$then", [new WorkflowExecutionPhase([thenPath])]),
                new WorkflowExecutionRegion("demo/check/$else", [new WorkflowExecutionPhase([elsePath])]),
            ]));
    }

    internal static WorkflowPlan CreateV6()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = Descriptor(DescriptorKind.Activity, "sample.validate");
        var loopPath = StructuralNodeIdentity.Create("demo", "loop1");
        var stepPath = loopPath + "/$body/step";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [activity],
            new CapabilityManifest([]),
            [
                new RepeatNode(
                    "loop1", loopPath, 5, str,
                    new InputBinding([]),
                    [new ActivityNode("step", stepPath, activity, [new ArgumentBinding("value", new LoopStateBinding([]))], str)],
                    new NodeOutputBinding(stepPath, []),
                    new ConditionExpression(ConditionOperator.Equal, new LoopIterationBinding([]), new LiteralBinding(JsonDocument.Parse("3").RootElement.Clone())),
                    str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([loopPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/loop1/$body", [new WorkflowExecutionPhase([stepPath])]),
            ]));
    }

    internal static WorkflowPlan CreateV7()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var checkpointPath = StructuralNodeIdentity.Create("demo", "saved");
        var waitPath = StructuralNodeIdentity.Create("demo", "approval");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [],
            new CapabilityManifest([]),
            [
                new CheckpointNode("saved", checkpointPath, new InputBinding([]), str),
                new WaitNode("approval", waitPath, "approval_request", str, 3600),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(waitPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([checkpointPath]), new WorkflowExecutionPhase([waitPath]), new WorkflowExecutionPhase([returnPath])]),
            ]));
    }

    internal static WorkflowPlan CreateV8()
    {
        var source = CreateV2();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var prompt = new PromptDefinition(
            "answer_prompt",
            [new PromptParameter("question", new PrimitiveType(FuwenPrimitiveKind.String))],
            [new PromptMessage(PromptMessageRole.User, "Answer this question: {{ question }}")]);

        return source with
        {
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
            Prompts = [prompt],
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with
                {
                    PromptTemplate = null,
                    Arguments = [],
                    ContextSnapshots = [],
                    ContextRequirements = [],
                    PromptName = prompt.Name,
                    PromptBindings = [new PromptBinding("question", new InputBinding(["question"]))],
                    Protocol = new InferenceProtocol(new InferenceProtocolLimits(maxTurns: 1, maxModelCalls: 1)),
                }
                : node).ToArray(),
        };
    }

    internal static WorkflowPlan CreateV9()
    {
        var source = CreateV8();
        var inference = source.Nodes.OfType<InferenceNode>().Single();
        var protocol = new InferenceProtocol(
            new InferenceProtocolLimits(
                maxTurns: 3,
                maxModelCalls: 3,
                maxToolCalls: 4,
                maxPromptTokens: 12_000,
                maxCompletionTokens: 3_000,
                maxTotalTokens: 15_000,
                maxDurationMilliseconds: 90_000,
                maxToolArgumentBytes: 24_000,
                maxToolResultBytes: 24_000,
                maxRetainedConversationBytes: 32_000,
                maxRetainedEvidenceBytes: 32_000,
                cost: new InferenceCostLimit("USD", 12_500)));

        return source with
        {
            IrVersion = FuwenContracts.IrVersion,
            CompilerSemanticVersion = FuwenContracts.CompilerSemanticVersion,
            FingerprintVersion = FuwenContracts.ExecutionFingerprintVersion,
            Nodes = source.Nodes.Select(node => node == inference
                ? inference with { Protocol = protocol }
                : node).ToArray(),
        };
    }
}
