using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class WorkflowCompilerTests
{
    [Fact]
    public void Builder_RequiresExplicitExecutionOrder()
    {
        var builder = new WorkflowPlanBuilder(
            "answer",
            "1",
            new PrimitiveType(FuwenPrimitiveKind.String),
            new PrimitiveType(FuwenPrimitiveKind.String),
            "routing/1");

        var act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explicit execution order*");
    }

    [Fact]
    public void CoreValidation_RejectsUndefinedPrimitiveKind()
    {
        var plan = Fixture.CreatePlan() with
        {
            InputType = new PrimitiveType((FuwenPrimitiveKind)999),
            OutputType = new PrimitiveType((FuwenPrimitiveKind)999),
        };

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compiler_RejectsUndefinedPrimitiveKindAtSnapshotBoundary()
    {
        var plan = Fixture.CreatePlan() with
        {
            InputType = new PrimitiveType((FuwenPrimitiveKind)999),
            OutputType = new PrimitiveType((FuwenPrimitiveKind)999),
        };

        var result = new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Definition.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.SemanticValidationFailed);
    }

    [Fact]
    public void Compiler_ValidatesIso8601DurationLiteralsConsistently()
    {
        using var validDocument = JsonDocument.Parse("\"PT1H\"");
        var source = Fixture.CreatePlan();
        var valid = source with
        {
            OutputType = new PrimitiveType(FuwenPrimitiveKind.Duration),
            Nodes = source.Nodes.Select(node => node is ReturnNode value
                ? value with { Value = new LiteralBinding(validDocument.RootElement.Clone()) }
                : node).ToArray(),
        };

        new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(valid, cancellationToken: TestContext.Current.CancellationToken)
            .Succeeded.Should().BeTrue();

        using var invalidDocument = JsonDocument.Parse("\"not-a-duration\"");
        var invalid = valid with
        {
            Nodes = valid.Nodes.Select(node => node is ReturnNode value
                ? value with { Value = new LiteralBinding(invalidDocument.RootElement.Clone()) }
                : node).ToArray(),
        };

        var result = new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(invalid, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.SemanticValidationFailed);
    }

    [Fact]
    public void Compiler_AccountsForLiteralStringBytesBeforeCatalogueResolution()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', 1_200_000)));
        var source = Fixture.CreatePlan();
        var plan = source with
        {
            Nodes = source.Nodes.Select(node => node is InferenceNode value
                ? value with { Arguments = [new ArgumentBinding("request", new LiteralBinding(document.RootElement.Clone()))] }
                : node).ToArray(),
        };

        var result = new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Usage.StringBytes.Should().BeGreaterThan(1_048_576);
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BudgetStringBytesExceeded);
    }

    [Fact]
    public void Compiler_NormalizesOversizedCanonicalDefinitionToDiagnostic()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', 4_500_000)));
        var source = Fixture.CreatePlan();
        var plan = source with
        {
            Nodes = source.Nodes.Select(node => node is InferenceNode value
                ? value with { Arguments = [new ArgumentBinding("request", new LiteralBinding(document.RootElement.Clone()))] }
                : node).ToArray(),
        };
        var budget = new CompilationBudget(maxStringBytes: 8_000_000);

        var result = new WorkflowCompiler(Fixture.CreateCatalogue(), budget, CapabilityGrantPolicy.AllowAll)
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Definition.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CanonicalDefinitionTooLarge);
    }

    [Fact]
    public void Compiler_NormalizesCatalogueProviderFaults()
    {
        var result = new WorkflowCompiler(new FaultingCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(Fixture.CreatePlan(), cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CatalogueResolutionInvalidResult);
    }

    [Fact]
    public void Compiler_CompilesCanonicalDefinition()
    {
        var plan = Fixture.CreatePlan();
        var compiler = new WorkflowCompiler(
            Fixture.CreateCatalogue(),
            capabilityPolicy: CapabilityGrantPolicy.AllowAll);

        var result = compiler.Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Definition.Should().NotBeNull();
        result.Definition!.ExecutionFingerprint.Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(plan));
        result.Plan.Should().NotBeSameAs(plan);
    }

    [Fact]
    public void Builder_CompilesSuccessfullyAndFreezesCallerOwnedCollections()
    {
        var source = Fixture.CreatePlan();
        var requestSchema = (ObjectSchemaDefinition)source.Schemas[0];
        var fields = requestSchema.Fields.ToList();
        var schemas = new List<ResolvedSchemaDefinition>
        {
            new ObjectSchemaDefinition(requestSchema.Descriptor, fields),
            source.Schemas[1],
        };
        var nodes = source.Nodes.ToList();
        using var document = JsonDocument.Parse("\"caller-owned\"");
        var inference = (InferenceNode)nodes.Single(node => node is InferenceNode);
        nodes[nodes.IndexOf(inference)] = inference with
        {
            Arguments = [new ArgumentBinding("request", new LiteralBinding(document.RootElement))],
        };
        var phasePaths = new List<string> { source.Nodes[0].StructuralPath };
        var order = new WorkflowExecutionOrder([
            new WorkflowExecutionRegion("answer", [
                new WorkflowExecutionPhase(phasePaths),
                new WorkflowExecutionPhase([source.Nodes[1].StructuralPath]),
                new WorkflowExecutionPhase([source.Nodes[2].StructuralPath]),
                new WorkflowExecutionPhase([source.Nodes[3].StructuralPath]),
            ]),
        ]);
        var builder = new WorkflowPlanBuilder(
            source.Name,
            source.Revision,
            source.InputType,
            source.OutputType,
            source.RoutingPolicyRevision);
        foreach (var schema in schemas)
            builder.AddSchema(schema);
        foreach (var node in nodes)
            builder.AddNode(node);
        builder.SetExecutionOrder(order);

        var built = builder.Build();
        var fingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(built);

        fields[0] = new SchemaField("mutated", new PrimitiveType(FuwenPrimitiveKind.Boolean));
        nodes.Clear();
        phasePaths[0] = "answer/mutated";
        document.Dispose();

        WorkflowPlanIdentity.ComputeExecutionFingerprint(built).Should().Be(fingerprint);
        built.Schemas[0].Should().BeOfType<ObjectSchemaDefinition>()
            .Which.Fields.Should().ContainSingle(field => field.Name == "question");
        ((InferenceNode)built.Nodes[1]).Arguments[0].Value.Should().BeOfType<LiteralBinding>();
        new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(built, cancellationToken: TestContext.Current.CancellationToken)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Builder_RejectsConflictingDescriptorPins()
    {
        var source = Fixture.CreatePlan();
        var conflict = source.CatalogueBindings[0] with
        {
            ContentDigest = new ContentDigest("sha256", "descriptor/v1", new string('b', 64)),
        };
        var builder = new WorkflowPlanBuilder(
            source.Name,
            source.Revision,
            source.InputType,
            source.OutputType,
            source.RoutingPolicyRevision)
            .AddCatalogueBinding(source.CatalogueBindings[0])
            .AddCatalogueBinding(conflict);

        var act = () => builder.Build();

        act.Should().Throw<ArgumentException>().WithMessage("*conflicting digests*");
    }

    [Fact]
    public void Compiler_RejectsUnknownBindingWithoutCreatingDefinition()
    {
        var plan = Fixture.CreatePlan() with
        {
            Nodes = Fixture.CreatePlan().Nodes.Select(node => node is ReturnNode @return
                ? @return with { Value = new NodeOutputBinding("answer/missing", []) }
                : node).ToArray(),
        };
        var compiler = new WorkflowCompiler(Fixture.CreateCatalogue());

        var result = compiler.Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Definition.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
    }

    [Fact]
    public void Compiler_RejectsForgedCapabilityAndMissingGrant()
    {
        var descriptor = Fixture.Descriptor(DescriptorKind.Activity, "sample.validate");
        var catalogue = Fixture.CreateCatalogue(required: new CapabilityRequirement("write"));
        var forged = Fixture.CreatePlan() with
        {
            CapabilityManifest = new CapabilityManifest([new CapabilityRequirement("forged")]),
        };

        var forgedResult = new WorkflowCompiler(catalogue, capabilityPolicy: CapabilityGrantPolicy.AllowAll).Compile(forged, cancellationToken: TestContext.Current.CancellationToken);
        forgedResult.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CapabilityManifestMismatch);

        var declared = Fixture.CreatePlan() with { CapabilityManifest = new CapabilityManifest([new CapabilityRequirement("write")]) };
        var missingGrant = new WorkflowCompiler(catalogue).Compile(declared, cancellationToken: TestContext.Current.CancellationToken);
        missingGrant.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CapabilityNotGranted);
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void Compiler_RejectsCallerSchemaThatDiffersFromTrustedPayload()
    {
        var trustedPlan = Fixture.CreatePlan();
        var falsified = trustedPlan with
        {
            Schemas = [
                new ObjectSchemaDefinition(
                    trustedPlan.Schemas[0].Descriptor,
                    [new SchemaField("forged", new PrimitiveType(FuwenPrimitiveKind.String))]),
                trustedPlan.Schemas[1],
            ],
        };

        var result = new WorkflowCompiler(
            Fixture.CreateCatalogue(trustedPlan),
            capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(falsified, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Definition.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CatalogueSchemaMismatch);
    }

    [Fact]
    public void Compiler_RejectsSchemaDescriptorWithoutTrustedPayload()
    {
        var plan = Fixture.CreatePlan();

        var result = new WorkflowCompiler(
            Fixture.CreateCatalogue(plan, omitSchemaPayload: true),
            capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CatalogueSchemaPayloadMissing);
    }

    [Fact]
    public void Compiler_RequiresExactCapabilityAssertionSet()
    {
        var plan = Fixture.CreatePlan() with
        {
            CapabilityManifest = new CapabilityManifest([]),
        };
        var catalogue = Fixture.CreateCatalogue(plan, required: new CapabilityRequirement("read"));

        var missing = new WorkflowCompiler(catalogue, capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(plan, cancellationToken: TestContext.Current.CancellationToken);
        missing.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CapabilityManifestMismatch);

        var extra = plan with
        {
            CapabilityManifest = new CapabilityManifest([new CapabilityRequirement("extra")]),
        };
        var extraResult = new WorkflowCompiler(catalogue, capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(extra, cancellationToken: TestContext.Current.CancellationToken);
        extraResult.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CapabilityManifestMismatch);
    }

    [Fact]
    public void Compiler_RejectsDuplicateAndNonContextInferenceSnapshots()
    {
        var plan = Fixture.CreatePlan();
        var inference = (InferenceNode)plan.Nodes.Single(node => node is InferenceNode);
        var duplicate = plan with
        {
            Nodes = plan.Nodes.Select(node => node == inference
                ? inference with
                {
                    ContextSnapshots = [
                        new NodeOutputBinding(inference.ContextSnapshots[0].NodePath, []),
                        new NodeOutputBinding(inference.ContextSnapshots[0].NodePath, []),
                    ],
                }
                : node).ToArray(),
        };
        var duplicateResult = new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(duplicate, cancellationToken: TestContext.Current.CancellationToken);
        duplicateResult.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.ContextSnapshotDuplicate);

        var activityPath = plan.Nodes.OfType<ActivityNode>().Single().StructuralPath;
        var nonContext = plan with
        {
            Nodes = plan.Nodes.Select(node => node switch
            {
                InferenceNode value => value with { ContextSnapshots = [new NodeOutputBinding(activityPath, [])] },
                ActivityNode value => value with { Arguments = [] },
                _ => node,
            }).ToArray(),
            ExecutionOrder = new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("answer", [
                    new WorkflowExecutionPhase([StructuralNodeIdentity.Create("answer", "context")]),
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([inference.StructuralPath]),
                    new WorkflowExecutionPhase([StructuralNodeIdentity.Create("answer", "return_result")]),
                ]),
            ]),
        };
        var nonContextResult = new WorkflowCompiler(Fixture.CreateCatalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll)
            .Compile(nonContext, cancellationToken: TestContext.Current.CancellationToken);
        nonContextResult.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.ContextSnapshotInvalid);
    }

    [Fact]
    public void Compiler_RejectsBudgetBeforeCatalogueResolution()
    {
        var compiler = new WorkflowCompiler(
            Fixture.CreateCatalogue(),
            new CompilationBudget(maxWorkflowNodes: 1),
            CapabilityGrantPolicy.AllowAll);

        var result = compiler.Compile(Fixture.CreatePlan(), cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BudgetWorkflowNodesExceeded);
        result.Usage.CatalogueLookups.Should().Be(0);
    }

    private static class Fixture
    {
        internal static WorkflowPlan CreatePlan()
        {
            var answer = Descriptor(DescriptorKind.Schema, "sample.answer");
            var request = Descriptor(DescriptorKind.Schema, "sample.request");
            var contextPath = StructuralNodeIdentity.Create("answer", "context");
            var inferencePath = StructuralNodeIdentity.Create("answer", "infer");
            var validatePath = StructuralNodeIdentity.Create("answer", "validate");
            var returnPath = StructuralNodeIdentity.Create("answer", "return_result");
            var nodes = new WorkflowNode[]
            {
                new ContextNode("context", contextPath, Descriptor(DescriptorKind.ContextProvider, "sample.context"), [new ArgumentBinding("request", new InputBinding([]))], new PrimitiveType(FuwenPrimitiveKind.String)),
                new InferenceNode("infer", inferencePath, Descriptor(DescriptorKind.InferenceProfile, "sample.reasoning"), Descriptor(DescriptorKind.PromptTemplate, "sample.answer-template"), [new ArgumentBinding("request", new LiteralBinding(JsonDocument.Parse("\"hello\"").RootElement.Clone()))], [new NodeOutputBinding(contextPath, [])], new NamedTypeReference(answer)),
                new ActivityNode("validate", validatePath, Descriptor(DescriptorKind.Activity, "sample.validate"), [new ArgumentBinding("answer", new NodeOutputBinding(inferencePath, []))], new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferencePath, [])),
            };
            return new WorkflowPlan(
                FuwenContracts.IrVersionV2,
                "fuwen-language/v1",
                FuwenContracts.CompilerSemanticVersionV2,
                FuwenContracts.CanonicalJsonVersion,
                FuwenContracts.ExecutionFingerprintVersionV2,
                "answer",
                "1",
                new NamedTypeReference(request),
                new NamedTypeReference(answer),
                "routing/1",
                [
                    new ObjectSchemaDefinition(request, [new SchemaField("question", new PrimitiveType(FuwenPrimitiveKind.String))]),
                    new ObjectSchemaDefinition(answer, [new SchemaField("text", new PrimitiveType(FuwenPrimitiveKind.String))]),
                ],
                [
                    request,
                    answer,
                    Descriptor(DescriptorKind.ContextProvider, "sample.context"),
                    Descriptor(DescriptorKind.InferenceProfile, "sample.reasoning"),
                    Descriptor(DescriptorKind.PromptTemplate, "sample.answer-template"),
                    Descriptor(DescriptorKind.Activity, "sample.validate"),
                ],
                new CapabilityManifest([]),
                nodes,
                new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("answer", [
                        new WorkflowExecutionPhase([contextPath]),
                        new WorkflowExecutionPhase([inferencePath]),
                        new WorkflowExecutionPhase([validatePath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                ]));
        }

        internal static InMemoryTrustedCatalogue CreateCatalogue(
            WorkflowPlan? source = null,
            CapabilityRequirement? required = null,
            bool omitSchemaPayload = false)
        {
            var plan = source ?? CreatePlan();
            var descriptors = plan.Schemas.Select(schema => new TrustedCatalogueDescriptor(schema.Descriptor, omitSchemaPayload ? null : schema))
                .Concat(plan.CatalogueBindings.Select(descriptor => new TrustedCatalogueDescriptor(descriptor, requiredCapabilities: required is null ? [] : [required])))
                .GroupBy(descriptor => descriptor.Descriptor)
                .Select(group => group.First())
                .ToArray();
            return new InMemoryTrustedCatalogue(descriptors);
        }

        internal static DescriptorReference Descriptor(DescriptorKind kind, string name) =>
            new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
    }

    private sealed class FaultingCatalogue : ITrustedCatalogue
    {
        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DescriptorResolutionResult>(new InvalidOperationException("catalogue fault"));
    }
}
