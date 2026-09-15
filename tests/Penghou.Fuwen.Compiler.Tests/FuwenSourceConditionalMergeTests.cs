using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Stage 2 value-producing conditionals — IR v5 merge clause.</summary>
public sealed class FuwenSourceConditionalMergeTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static ITrustedCatalogue Catalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", Digest('a')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("value", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
    }

    private const string MergeSource = """
        workflow demo(input: string) -> string {
          if decide input == "go" {
            activity accept = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
          } else {
            activity repair = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
          } merge accept, repair -> string;
          return decide;
        }
        """;

    [Fact]
    public async Task Merge_clause_compiles_to_v5_with_branch_bindings()
    {
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(MergeSource, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersionV5);
        result.Plan.CompilerSemanticVersion.Should().Be(FuwenContracts.CompilerSemanticVersionV5);
        result.Plan.FingerprintVersion.Should().Be(FuwenContracts.ExecutionFingerprintVersionV5);
        var conditional = result.Plan.Nodes.OfType<ConditionalNode>().Should().ContainSingle().Subject;
        conditional.Merge.Should().NotBeNull();
        conditional.Name.Should().Be("decide");
        conditional.Merge!.ThenValue.Should().BeOfType<NodeOutputBinding>()
            .Which.NodePath.Should().Be("demo/decide/$then/accept");
        conditional.Merge!.ElseValue.Should().BeOfType<NodeOutputBinding>()
            .Which.NodePath.Should().Be("demo/decide/$else/repair");
        conditional.Merge!.ResultType.Should().Be(new PrimitiveType(FuwenPrimitiveKind.String));
    }

    [Fact]
    public async Task Merge_result_is_consumable_downstream()
    {
        const string source = """
            workflow demo(input: string) -> string {
              if decide input == "go" {
                activity accept = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } else {
                activity repair = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } merge accept, repair -> string;
              activity seal = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: decide;) -> string;
              return seal;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
    }

    [Fact]
    public async Task Merge_with_mismatched_branch_types_is_rejected()
    {
        // repair declares int output while accept declares string.
        const string typedMismatch = """
            workflow demo(input: string) -> string {
              if decide input == "go" {
                activity accept = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } else {
                activity repair = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> int;
              } merge accept, repair -> string;
              return decide;
            }
            """;

        var mismatch = await new FuwenSourceCompiler(Catalogue()).CompileAsync(typedMismatch, cancellationToken: TestContext.Current.CancellationToken);
        mismatch.Succeeded.Should().BeFalse();
        mismatch.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingTypeMismatch);
    }

    [Fact]
    public async Task Merge_referencing_unknown_branch_node_is_rejected()
    {
        const string source = """
            workflow demo(input: string) -> string {
              if decide input == "go" {
                activity accept = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } else {
                activity repair = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } merge missing, repair -> string;
              return decide;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
    }

    [Fact]
    public async Task Merge_without_else_branch_value_is_rejected()
    {
        const string source = """
            workflow demo(input: string) -> string {
              if decide input == "go" {
                activity accept = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } else {
                activity repair = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              } merge accept, accept -> string;
              return decide;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        // accept lives in $then, so the else-side reference crosses regions.
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
    }

    [Fact]
    public async Task Merge_formatter_is_idempotent()
    {
        var first = FuwenFormatter.Format(MergeSource);
        var second = FuwenFormatter.Format(first);
        second.Should().Be(first);

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
    }

    [Fact]
    public async Task Merge_on_non_v5_plan_is_rejected_by_core_validator()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", Digest('a'));
        var condPath = StructuralNodeIdentity.Create("demo", "decide");
        var thenPath = condPath + "/$then/accept";
        var elsePath = condPath + "/$else/repair";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new ConditionalNode(
                "decide",
                condPath,
                new ConditionExpression(ConditionOperator.Equal, new InputBinding([]), new LiteralBinding(System.Text.Json.JsonDocument.Parse("\"go\"").RootElement.Clone())),
                [new ActivityNode("accept", thenPath, activity, [new ArgumentBinding("value", new InputBinding([]))], str)],
                [new ActivityNode("repair", elsePath, activity, [new ArgumentBinding("value", new InputBinding([]))], str)],
                new ConditionalMerge(new NodeOutputBinding(thenPath, []), new NodeOutputBinding(elsePath, []), str)))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(condPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([condPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion("demo/decide/$then", [
                    new WorkflowExecutionPhase([thenPath]),
                ]),
                new WorkflowExecutionRegion("demo/decide/$else", [
                    new WorkflowExecutionPhase([elsePath]),
                ]),
            ]))
            .BuildV4();

        var act = () => WorkflowPlanValidator.Validate(plan);
        act.Should().Throw<ArgumentException>().WithMessage("*IR v5*");
    }
}
