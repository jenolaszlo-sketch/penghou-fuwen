using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Stage 1 fan-out DSL surface — grammar + parser + compiler.</summary>
public sealed class FuwenSourceFanOutTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static ITrustedCatalogue Catalogue()
    {
        var list = new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 8);
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.uppercase", "1", Digest('a')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("value", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.decorate", "1", Digest('b')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("value", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('c')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('e'))),
        ]);
    }

    [Fact]
    public async Task Fanout_over_string_list_with_key_and_yield_compiles_to_current_ir()
    {
        const string source = """
            workflow batch(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key item max 8 concurrency 2 {
                activity decorate = activity "sample.decorate@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" (value: item;) -> string;
                activity uppercase = activity "sample.uppercase@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
              } yield decorate -> list<string>[8];
              return process;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        result.Plan.CompilerSemanticVersion.Should().Be(FuwenContracts.CompilerSemanticVersion);
        result.Plan.Nodes.OfType<FanOutNode>().Should().ContainSingle();
        var fanOut = result.Plan.Nodes.OfType<FanOutNode>().Single();
        fanOut.Name.Should().Be("process");
        fanOut.Source.Should().BeOfType<InputBinding>();
        fanOut.Item.Name.Should().Be("item");
        fanOut.Key.Should().BeOfType<FanOutItemValueBinding>();
        fanOut.Body.Should().HaveCount(2);
        fanOut.Yield.Should().BeOfType<NodeOutputBinding>();
        fanOut.MaximumItems.Should().Be(8);
        fanOut.MaximumConcurrency.Should().Be(2);
        fanOut.ResultType.Should().BeOfType<ListType>();
        // Closed body: body activity sees the item, not outer nodes.
        result.Plan.ExecutionOrder!.Regions.Should().Contain(r => r.RegionPath == "batch/process/$body");
    }

    [Fact]
    public async Task Fanout_body_supports_context_and_inference_nodes()
    {
        // R26: per-item staged work needs context and inference in fan-out bodies.
        const string source = """
            workflow batch(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key item max 8 {
                context cx = context "sample.context@1#cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" (request: item;) -> string;
                infer answer = infer "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" using "sample.prompt@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" (request: item;) with cx -> string;
              } yield answer -> list<string>[8];
              return process;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        var fanOut = result.Plan!.Nodes.OfType<FanOutNode>().Should().ContainSingle().Subject;
        fanOut.Body.OfType<ContextNode>().Should().ContainSingle();
        var inference = fanOut.Body.OfType<InferenceNode>().Should().ContainSingle().Subject;
        inference.ContextRequirements.Should().ContainSingle()
            .Which.Source.NodePath.Should().Contain("cx");
    }

    [Fact]
    public async Task Fanout_body_still_rejects_nested_fanout()
    {
        const string source = """
            workflow batch(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key item max 8 {
                fanout nested over input as sub: string key sub max 8 {
                  activity upper = activity "sample.uppercase@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: sub;) -> string;
                } yield upper -> list<string>[8];
              } yield process -> list<string>[8];
              return process;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.FanOutBodyUnsupported);
    }

    [Fact]
    public async Task Fanout_key_must_be_item_projection_and_is_validated_before_child_work()
    {
        const string source = """
            workflow batch(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key input max 8 {
                activity upper = activity "sample.uppercase@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
              } yield upper -> list<string>[8];
              return process;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        // outer input is not the current item, so key validation fails before child work
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Code == CompilerDiagnosticCodes.BindingTypeMismatch ||
            d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid ||
            d.Code == CompilerDiagnosticCodes.SemanticValidationFailed ||
            (d.Actual != null && d.Actual.Contains("key must be a projection", StringComparison.OrdinalIgnoreCase)) ||
            d.Message.Contains("key must be a projection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Fanout_formatter_is_idempotent()
    {
        const string source = "workflow batch(input: list<string>[8])->list<string>[8]{fanout process over input as item: string key item max 8{activity upper=activity \"sample.uppercase@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"(value:item;)->string;}yield upper -> list<string>[8];return process;}";
        var catalogue = Catalogue();
        var first = FuwenFormatter.Format(source);
        var second = FuwenFormatter.Format(first);
        second.Should().Be(first);

        var result = await new FuwenSourceCompiler(catalogue).CompileAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
    }

    [Fact]
    public async Task Fanout_body_item_shadowing_is_rejected_at_parse_time()
    {
        const string source = """
            workflow batch(input: list<string>[8]) -> list<string>[8] {
              fanout process over input as item: string key item max 8 {
                activity item = activity "sample.uppercase@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
              } yield item -> list<string>[8];
              return process;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
    }
}
