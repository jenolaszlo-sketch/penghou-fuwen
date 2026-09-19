using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Phase B tools: declarations, references, admission, and identity.</summary>
public sealed class FuwenSourceToolTests
{
    private static ContentDigest Digest(char c) => new("sha256", "descriptor/v1", new string(c, 64));

    private static TrustedCatalogueDescriptor ToolDescriptor(
        string name, char digest,
        CallableEffect effect = CallableEffect.Read,
        CallableIdempotency idempotency = CallableIdempotency.Idempotent,
        CallableRetrySafety safety = CallableRetrySafety.Safe)
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        return new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.Tool, name, "1", Digest(digest)),
            callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("query", str)], str),
                effect, idempotency, safety));
    }

    private static ITrustedCatalogue Catalogue() => new InMemoryTrustedCatalogue([
        new TrustedCatalogueDescriptor(
            new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d')),
            callableContract: new CallableContract(
                new CallableSignature(
                    [new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
                    new PrimitiveType(FuwenPrimitiveKind.String)),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ToolDescriptor("sample.search", 'f'),
        ToolDescriptor("sample.read", 'e'),
    ]);

    private const string ProfileRef = "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string SearchRef = "sample.search@1#ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string ReadRef = "sample.read@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private const string PromptPrelude =
        "prompt greet(name: string) {\n" +
        "  user \"Greet {{ name}}.\"\n" +
        "}\n";

    private static string WorkflowWithTools(string toolsClause) =>
        PromptPrelude +
        "workflow demo(input: string) -> string {\n" +
        $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) tools {toolsClause} -> string;\n" +
        "  return hello;\n" +
        "}";

    [Fact]
    public async Task Toolset_reference_resolves_to_exact_descriptors()
    {
        var source =
            "toolset coding_tools {\n" +
            $"  use \"{SearchRef}\";\n" +
            $"  use \"{ReadRef}\";\n" +
            "}\n" +
            PromptPrelude +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) tools coding_tools -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersionV8);
        var inference = result.Plan.Nodes.OfType<InferenceNode>().Single();
        inference.Tools!.Select(tool => tool.Name).Should().BeEquivalentTo("sample.search", "sample.read");
    }

    [Fact]
    public async Task Inline_tool_list_and_none_parse()
    {
        var inline = await new FuwenSourceCompiler(Catalogue()).CompileAsync(
            WorkflowWithTools($"[\"{SearchRef}\"]"),
            cancellationToken: TestContext.Current.CancellationToken);
        inline.Succeeded.Should().BeTrue(
            string.Join("; ", inline.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        inline.Plan!.Nodes.OfType<InferenceNode>().Single().Tools!
            .Select(tool => tool.Name).Should().Equal("sample.search");

        var none = await new FuwenSourceCompiler(Catalogue()).CompileAsync(
            WorkflowWithTools("none"),
            cancellationToken: TestContext.Current.CancellationToken);
        none.Succeeded.Should().BeTrue(
            string.Join("; ", none.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        none.Plan!.Nodes.OfType<InferenceNode>().Single().Tools.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_toolset_is_rejected()
    {
        var source = PromptPrelude +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) tools missing_set -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            (d.Message + " " + d.Actual).Contains("Unknown toolset 'missing_set'"));
    }

    [Fact]
    public async Task Duplicate_tools_are_rejected()
    {
        var source = WorkflowWithTools($"[\"{SearchRef}\", \"{SearchRef}\"]");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            (d.Message + " " + d.Actual).Contains("more than once"));
    }

    [Fact]
    public async Task Destructive_tool_is_rejected_at_admission()
    {
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d')),
                callableContract: new CallableContract(
                    new CallableSignature(
                        [new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
                        new PrimitiveType(FuwenPrimitiveKind.String)),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            ToolDescriptor("sample.deploy", 'b', effect: CallableEffect.Destructive),
        ]);
        var source = PromptPrelude +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) tools [\"sample.deploy@1#bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"] -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.CallableEffectRejected);
    }

    [Fact]
    public async Task Unregistered_tool_is_rejected_at_admission()
    {
        var source = WorkflowWithTools("[\"sample.unknown@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Tools_require_v8()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d'));
        var tool = new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('f'));
        var inferPath = StructuralNodeIdentity.Create("demo", "infer");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new InferenceNode(
                "infer", inferPath, profile, null, [], [], str, [],
                null, null, [tool]))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .BuildV7();

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*IR v8*");
    }

    [Fact]
    public void Tool_changes_are_fingerprint_and_comparison_visible()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var profile = new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d'));
        var search = new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('f'));
        var read = new DescriptorReference(DescriptorKind.Tool, "sample.read", "1", Digest('e'));
        var inferPath = StructuralNodeIdentity.Create("demo", "infer");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        WorkflowPlan Build(params DescriptorReference[] tools)
        {
            return new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
                .AddNode(new InferenceNode(
                    "infer", inferPath, profile, null, [], [], str, [],
                    "greet",
                    [new PromptBinding("name", new InputBinding([]))],
                    tools.Length == 0 ? null : tools))
                .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("demo", [
                        new WorkflowExecutionPhase([inferPath]),
                        new WorkflowExecutionPhase([returnPath]),
                    ]),
                ]))
                .AddPrompt(new PromptDefinition(
                    "greet",
                    [new PromptParameter("name", str)],
                    [new PromptMessage(PromptMessageRole.User, "Hi {{ name }}.")]))
                .BuildV8();
        }

        var withSearch = Build(search);
        var withRead = Build(read);
        var reordered = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new InferenceNode(
                "infer", inferPath, profile, null, [], [], str, [],
                "greet",
                [new PromptBinding("name", new InputBinding([]))],
                [read, search]))
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(inferPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([inferPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .AddPrompt(new PromptDefinition(
                "greet",
                [new PromptParameter("name", str)],
                [new PromptMessage(PromptMessageRole.User, "Hi {{ name }}.")]))
            .BuildV8();
        // Reordered tool list normalizes identically to [search, read] sorted order.
        var sorted = Build(search, read);

        WorkflowPlanValidator.Validate(withSearch);
        var searchFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(withSearch);
        var readFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(withRead);
        searchFingerprint.Should().NotBe(readFingerprint);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(reordered).Should().Be(
            WorkflowPlanIdentity.ComputeExecutionFingerprint(sorted));
    }
}
