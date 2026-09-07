using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using System.Text.Json;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class DiagnosticsAndBudgetTests
{
    [Fact]
    public void Diagnostic_SnapshotsValuesAndSpan()
    {
        var span = new SourceSpan("doc", 2, 3, 0, 4);
        var diagnostic = new CompilerDiagnostic("FWN-TEST", DiagnosticSeverity.Error, DiagnosticPhase.Binding, "bad", "main/x", span, "string", "number");

        diagnostic.Code.Should().Be("FWN-TEST");
        diagnostic.Span.Should().NotBeSameAs(span);
        diagnostic.Span.Should().Be(span);
        Action mutate = () => _ = diagnostic with { };
        mutate.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0001")]
    public void Diagnostic_RejectsHostileText(string value)
    {
        var act = () => new CompilerDiagnostic(value, DiagnosticSeverity.Error, DiagnosticPhase.General, "message");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Collection_IsOrderedAndCappedDeterministically()
    {
        var second = new CompilerDiagnostic("B", DiagnosticSeverity.Error, DiagnosticPhase.Binding, "b", "z", new SourceSpan("doc", 20, 1, 1, 0));
        var first = new CompilerDiagnostic("A", DiagnosticSeverity.Warning, DiagnosticPhase.Binding, "a", "a", new SourceSpan("doc", 1, 1, 1, 0));
        var collection = new DiagnosticCollection([second, first], 1);

        collection.Should().ContainSingle().Which.Code.Should().Be("A");
        collection.IsTruncated.Should().BeTrue();
        collection.DroppedCount.Should().Be(1);
    }

    [Fact]
    public void Collection_UsesEveryTieBreakerAndCountsFiniteDropsExactly()
    {
        var later = new CompilerDiagnostic("A", DiagnosticSeverity.Warning, DiagnosticPhase.Binding, "same", "path", new SourceSpan("doc", 1, 2, 1, 3), "z", "z");
        var earlier = new CompilerDiagnostic("A", DiagnosticSeverity.Warning, DiagnosticPhase.Binding, "same", "path", new SourceSpan("doc", 1, 1, 1, 3), "z", "z");
        var diagnostics = new[] { later, earlier, new CompilerDiagnostic("C", DiagnosticSeverity.Info, DiagnosticPhase.General, "last") };

        var collection = new DiagnosticCollection(diagnostics, 1);

        collection.Single().Span!.Utf8ByteLength.Should().Be(1);
        collection.DroppedCount.Should().Be(2);
    }

    [Fact]
    public void Builder_DoesNotRetainDiagnosticsAfterCap()
    {
        var builder = new DiagnosticBuilder(1);
        builder.Add(new CompilerDiagnostic("A", DiagnosticSeverity.Error, DiagnosticPhase.General, "a")).Should().BeTrue();
        builder.Add(new CompilerDiagnostic("B", DiagnosticSeverity.Error, DiagnosticPhase.General, "b")).Should().BeFalse();

        var snapshot = builder.ToImmutable();
        snapshot.Count.Should().Be(1);
        snapshot.DroppedCount.Should().Be(1);
    }

    [Fact]
    public void CallerBudget_CanNarrowButCannotExpandHost()
    {
        var host = new CompilationBudget(maxSourceBytes: 10, maxTokens: 20);
        var caller = new CompilationBudget(maxSourceBytes: 5, maxTokens: 10);
        CompilationBudget.ApplyCaller(host, caller).MaxSourceBytes.Should().Be(5);

        var expansion = new CompilationBudget(maxSourceBytes: 11, maxTokens: 20);
        var act = () => host.Narrow(expansion);
        act.Should().Throw<ArgumentException>();
        host.IsAtMost(new CompilationBudget(maxSourceBytes: 11, maxTokens: 20)).Should().BeTrue();
        host.IsAtMost(new CompilationBudget(maxSourceBytes: 9, maxTokens: 20)).Should().BeFalse();
    }

    [Fact]
    public void BudgetEvaluation_ReportsStableDeterministicOutcome()
    {
        var budget = new CompilationBudget(maxSourceBytes: 10, maxTokens: 10, maxDiagnostics: 10);
        var usage = new CompilationUsageSummary(sourceBytes: 11, tokens: 12);
        var result = CompilationBudgetEvaluator.Evaluate(budget, usage);

        result.IsWithinBudget.Should().BeFalse();
        result.ExceededDimensions.Should().Equal(CompilationBudgetDimension.SourceBytes, CompilationBudgetDimension.Tokens);
        result.Diagnostics.Select(static d => (d.Code, d.Expected, d.Actual)).Should().Equal(
            (CompilerDiagnosticCodes.BudgetSourceBytesExceeded, "10", "11"),
            (CompilerDiagnosticCodes.BudgetTokensExceeded, "10", "12"));
    }

    [Fact]
    public void Tracker_RefusesConsumptionThatWouldExceedHardCeiling()
    {
        var tracker = new CompilationBudgetTracker(new CompilationBudget(maxSourceBytes: 2));
        tracker.TryConsume(CompilationBudgetDimension.SourceBytes, 2).Should().BeTrue();
        tracker.TryConsume(CompilationBudgetDimension.SourceBytes).Should().BeFalse();
        tracker.Snapshot().SourceBytes.Should().Be(2);
        var invalid = () => tracker.IsExceeded((CompilationBudgetDimension)999);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Tracker_BatchConsumptionIsAtomicAcrossRepeatedDimensions()
    {
        var tracker = new CompilationBudgetTracker(new CompilationBudget(maxAstNodes: 3));

        var accepted = tracker.TryConsumeBatch(
        [
            (CompilationBudgetDimension.AstNodes, 2),
            (CompilationBudgetDimension.AstNodes, 2),
        ]);

        accepted.Should().BeFalse();
        tracker.Snapshot().AstNodes.Should().Be(0);
    }

    [Fact]
    public void CompilationResult_DoesNotRetainCallerPlanOrDisposedLiteral()
    {
        using var document = JsonDocument.Parse("\"detached\"");
        var nodes = CompilerPlanFixture.CreateNodes(new LiteralBinding(document.RootElement));
        var plan = CompilerPlanFixture.Create(nodes);
        var result = new CompilationResult(plan, [], new CompilationUsageSummary(), CompilationBudget.Default);

        nodes[0] = nodes[1];
        document.Dispose();

        result.Plan.Should().NotBeNull();
        result.Plan!.Nodes[0].Should().BeOfType<ContextNode>();
        result.Plan.Nodes[1].Should().BeOfType<InferenceNode>();
        ((InferenceNode)result.Plan.Nodes[1]).Arguments[0].Value.Should().BeOfType<LiteralBinding>();
    }

    [Fact]
    public void CompilationResult_DoesNotExposeDefinitionWhenDiagnosticsContainErrors()
    {
        var plan = CompilerPlanFixture.Create(CompilerPlanFixture.CreateNodes(new LiteralBinding(JsonDocument.Parse("\"detached\"").RootElement.Clone())));
        var result = new CompilationResult(
            plan,
            [new CompilerDiagnostic("FWN-TEST", DiagnosticSeverity.Error, DiagnosticPhase.Validation, "rejected")],
            new CompilationUsageSummary(),
            CompilationBudget.Default);

        result.Definition.Should().BeNull();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Collection_RejectsInputBeyondHardEnumerationCeiling()
    {
        IEnumerable<CompilerDiagnostic> Infinite()
        {
            while (true)
                yield return new CompilerDiagnostic("FWN-TEST", DiagnosticSeverity.Error, DiagnosticPhase.General, "x");
        }

        var act = () => new DiagnosticCollection(Infinite(), 1);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Collection_RejectsAConfiguredCapBeyondItsHardInputCeiling()
    {
        var act = () => new DiagnosticCollection([], DiagnosticCollection.MaximumInputDiagnostics + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

internal static class CompilerPlanFixture
{
    internal static WorkflowPlan Create(IReadOnlyList<WorkflowNode> nodes) => new(
        FuwenContracts.IrVersion,
        "fuwen-language/v1",
        "compiler-semantics/1",
        FuwenContracts.CanonicalJsonVersion,
        FuwenContracts.ExecutionFingerprintVersion,
        "answer",
        "1",
        new NamedTypeReference(Descriptor(DescriptorKind.Schema, "sample.request")),
        new NamedTypeReference(Descriptor(DescriptorKind.Schema, "sample.answer")),
        "routing/1",
        [
            new ObjectSchemaDefinition(Descriptor(DescriptorKind.Schema, "sample.request"), [new SchemaField("question", new PrimitiveType(FuwenPrimitiveKind.String))]),
            new ObjectSchemaDefinition(Descriptor(DescriptorKind.Schema, "sample.answer"), [new SchemaField("text", new PrimitiveType(FuwenPrimitiveKind.String))]),
        ],
        [Descriptor(DescriptorKind.Activity, "sample.validate"), Descriptor(DescriptorKind.Schema, "sample.answer"), Descriptor(DescriptorKind.Schema, "sample.request"), Descriptor(DescriptorKind.ContextProvider, "sample.context"), Descriptor(DescriptorKind.InferenceProfile, "sample.reasoning"), Descriptor(DescriptorKind.PromptTemplate, "sample.answer-template")],
        new CapabilityManifest([new CapabilityRequirement("inference"), new CapabilityRequirement("context.read")]),
        nodes);

    internal static WorkflowNode[] CreateNodes(Binding inferenceArgument)
    {
        var answer = Descriptor(DescriptorKind.Schema, "sample.answer");
        var contextPath = StructuralNodeIdentity.Create("answer", "context");
        var inferencePath = StructuralNodeIdentity.Create("answer", "infer");
        var activity = Descriptor(DescriptorKind.Activity, "sample.validate");
        return
        [
            new ContextNode("context", contextPath, Descriptor(DescriptorKind.ContextProvider, "sample.context"), [new ArgumentBinding("request", new InputBinding([]))], new PrimitiveType(FuwenPrimitiveKind.String)),
            new InferenceNode("infer", inferencePath, Descriptor(DescriptorKind.InferenceProfile, "sample.reasoning"), Descriptor(DescriptorKind.PromptTemplate, "sample.answer-template"), [new ArgumentBinding("request", inferenceArgument)], [new NodeOutputBinding(contextPath, [])], new NamedTypeReference(answer)),
            new ActivityNode("validate", StructuralNodeIdentity.Create("answer", "validate"), activity, [new ArgumentBinding("answer", new NodeOutputBinding(inferencePath, []))], new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            new ReturnNode("return_result", StructuralNodeIdentity.Create("answer", "return_result"), new NodeOutputBinding(inferencePath, [])),
        ];
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) => new(kind, name, "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
}
