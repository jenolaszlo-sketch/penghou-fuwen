using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class FuwenSourceInferenceFallbackTests
{
    private static readonly PrimitiveType Text = new(FuwenPrimitiveKind.String);
    private static readonly DescriptorReference Profile = new(
        DescriptorKind.InferenceProfile, "sample.profile", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('d', 64)));
    private static readonly DescriptorReference Template = new(
        DescriptorKind.PromptTemplate, "sample.template", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('c', 64)));

    private static FuwenSourceCompiler Compiler() => new(new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(Profile,
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("request", Text)], Text),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        new TrustedCatalogueDescriptor(Template),
    ]));

    private static string Source(string clause) => $$"""
        workflow demo(input: string) -> string {
          infer answer = infer "sample.profile@1#{{new string('d', 64)}}" using "sample.template@1#{{new string('c', 64)}}" (request: input;) -> string {{clause}};
          return answer;
        }
        """;

    [Fact]
    public async Task Selected_fallback_is_typed_and_changes_execution_identity()
    {
        var first = await Compiler().CompileAsync(
            Source("on failure [SchemaMismatch, TurnLimitExceeded] fallback \"manual\""),
            cancellationToken: TestContext.Current.CancellationToken);
        first.Succeeded.Should().BeTrue(string.Join("; ", first.Diagnostics.Select(d => d.Message)));
        first.Plan!.IrVersion.Should().Be(FuwenContracts.InferenceFallbackIrVersion);
        var fallback = first.Plan.Nodes.OfType<InferenceNode>().Single().FailureFallback!;
        fallback.Codes.Should().Equal(ExecutionFailureCode.SchemaMismatch, ExecutionFailureCode.TurnLimitExceeded);
        fallback.Value.Should().BeOfType<LiteralBinding>();

        var changed = await Compiler().CompileAsync(
            Source("on failure [SchemaMismatch, TurnLimitExceeded] fallback \"review\""),
            cancellationToken: TestContext.Current.CancellationToken);
        changed.Succeeded.Should().BeTrue(string.Join("; ", changed.Diagnostics.Select(d => d.Message)));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(first.Plan)
            .Should().NotBe(WorkflowPlanIdentity.ComputeExecutionFingerprint(changed.Plan!));
        var formatted = FuwenFormatter.Format(Source("on failure [SchemaMismatch] fallback \"manual\""));
        FuwenFormatter.Format(formatted).Should().Be(formatted);
        (await Compiler().CompileAsync(formatted,
            cancellationToken: TestContext.Current.CancellationToken)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_ir_cannot_silently_ignore_a_fallback()
    {
        var result = await Compiler().CompileAsync(
            Source("on failure [SchemaMismatch] fallback \"manual\""),
            cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue();
        var incompatible = result.Plan! with { IrVersion = FuwenContracts.IrVersion };
        var action = () => WorkflowPlanValidator.Validate(incompatible);
        action.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Fallback_inside_fanout_body_is_rejected_until_item_scoped_failure_is_durable()
    {
        var source = $$"""
            workflow demo(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key item max 8 {
                infer answer = infer "sample.profile@1#{{new string('d', 64)}}" using "sample.template@1#{{new string('c', 64)}}" (request: item;) -> string on failure [SchemaMismatch] fallback "manual";
              } yield answer -> list<string>[8];
              return process;
            }
            """;
        var result = await Compiler().CompileAsync(source,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("on failure [AmbiguousOperation] fallback \"manual\"")]
    [InlineData("on failure [SchemaMismatch] fallback input")]
    [InlineData("on failure [SchemaMismatch] fallback 42")]
    public async Task Unsafe_or_mistyped_fallback_is_rejected(string clause)
    {
        var result = await Compiler().CompileAsync(Source(clause),
            cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
    }
}
