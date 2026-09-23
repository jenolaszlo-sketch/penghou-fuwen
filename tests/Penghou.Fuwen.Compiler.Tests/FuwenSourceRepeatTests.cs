using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class FuwenSourceRepeatTests
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
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('b')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('c')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('d'))),
        ]);
    }

    [Fact]
    public async Task Repeat_over_string_state_with_continue_and_break_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 3 state s: string = input {
                activity step = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;
              } continue step break s == "ok" -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        result.Plan.CompilerSemanticVersion.Should().Be(FuwenContracts.CompilerSemanticVersion);
        var repeat = result.Plan.Nodes.OfType<RepeatNode>().Should().ContainSingle().Subject;
        repeat.MaxIterations.Should().Be(3);
        repeat.StateType.Should().Be(new PrimitiveType(FuwenPrimitiveKind.String));
        repeat.ResultType.Should().Be(new PrimitiveType(FuwenPrimitiveKind.String));
        repeat.Body.Should().HaveCount(1);
        repeat.ContinueWith.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Contain("step");
        repeat.BreakWhen.Operator.Should().Be(ConditionOperator.Equal);
    }

    [Fact]
    public async Task Repeat_body_sees_loop_state_and_iteration()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 2 state s: string = input {
                activity step = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;
              } continue step break iter == 2 -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        // s and iter are loop bindings, should be valid inside body/break.
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
    }

    [Fact]
    public async Task Repeat_with_mismatched_state_type_is_rejected()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 3 state s: string = input {
                activity step = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;
              } continue 42 break s == "ok" -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingTypeMismatch);
    }

    [Fact]
    public async Task Repeat_initial_state_may_seed_from_outer_node_output()
    {
        // R27: the initial state is evaluated before the first iteration in
        // the parent region, so it may reference outer node outputs.
        const string source = """
            workflow demo(input: string) -> string {
              activity prep = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              repeat loop1 max 2 state s: string = prep {
                activity step = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;
              } continue step break iter == 2 -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        var repeat = result.Plan!.Nodes.OfType<RepeatNode>().Should().ContainSingle().Subject;
        repeat.InitialState.Should().BeOfType<NodeOutputBinding>().Which.NodePath.Should().Contain("prep");
    }

    [Fact]
    public async Task Repeat_body_must_not_shadow_state()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 3 state s: string = input {
                activity s = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;
              } continue s break s == "ok" -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
    }

    [Fact]
    public async Task Repeat_body_supports_context_and_inference_nodes()
    {
        // R16: review/repair loops repeat LLM inference with per-iteration
        // context; the DSL must accept context + infer inside repeat bodies.
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 2 state s: string = input {
                context cx = context "sample.context@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" (request: s;) -> string;
                infer answer = infer "sample.profile@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" using "sample.prompt@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" (request: s;) with cx -> string;
              } continue answer break iter == 2 -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var repeat = result.Plan.Nodes.OfType<RepeatNode>().Should().ContainSingle().Subject;
        repeat.Body.OfType<ContextNode>().Should().ContainSingle();
        var inference = repeat.Body.OfType<InferenceNode>().Should().ContainSingle().Subject;
        inference.ContextRequirements.Should().ContainSingle()
            .Which.Source.NodePath.Should().Contain("cx");
    }

    [Fact]
    public async Task Repeat_formatter_is_idempotent()
    {
        const string source = "workflow demo(input: string)->string{repeat loop1 max 3 state s:string=input{activity step=activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"(value:s;)->string;}continue step break s==\"ok\"->string;return loop1;}";
        var first = FuwenFormatter.Format(source);
        var second = FuwenFormatter.Format(first);
        second.Should().Be(first);
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
    }
}
