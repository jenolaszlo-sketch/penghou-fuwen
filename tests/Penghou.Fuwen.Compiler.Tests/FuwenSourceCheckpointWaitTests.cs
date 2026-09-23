using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class FuwenSourceCheckpointWaitTests
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

    [Fact]
    public async Task Checkpoint_node_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              checkpoint saved value input -> string;
              return saved;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var checkpoint = result.Plan.Nodes.OfType<CheckpointNode>().Should().ContainSingle().Subject;
        checkpoint.Name.Should().Be("saved");
        checkpoint.OutputType.Should().Be(new PrimitiveType(FuwenPrimitiveKind.String));
    }

    [Fact]
    public async Task Wait_node_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              wait approval signal approval_request type string timeout 3600;
              return approval;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var wait = result.Plan.Nodes.OfType<WaitNode>().Should().ContainSingle().Subject;
        wait.Name.Should().Be("approval");
        wait.SignalName.Should().Be("approval_request");
        wait.TimeoutSeconds.Should().Be(3600);
        wait.OutputType.Should().Be(new PrimitiveType(FuwenPrimitiveKind.String));
    }

    [Fact]
    public async Task Wait_node_without_timeout_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              wait approval signal approval_request type string;
              return approval;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        var wait = result.Plan!.Nodes.OfType<WaitNode>().Should().ContainSingle().Subject;
        wait.TimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Checkpoint_followed_by_wait_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              checkpoint saved value input -> string;
              wait approval signal approval_request type string;
              return approval;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan!.Nodes.Should().Contain(n => n is CheckpointNode);
        result.Plan!.Nodes.Should().Contain(n => n is WaitNode);
    }

    [Fact]
    public async Task Checkpoint_inside_repeat_body_compiles_to_current_ir()
    {
        const string source = """
            workflow demo(input: string) -> string {
              repeat loop1 max 2 state s: string = input {
                checkpoint saved value s -> string;
              } continue s break s == "ok" -> string;
              return loop1;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
        var repeat = result.Plan.Nodes.OfType<RepeatNode>().Should().ContainSingle().Subject;
        repeat.Body.Should().Contain(n => n is CheckpointNode);
    }
}
