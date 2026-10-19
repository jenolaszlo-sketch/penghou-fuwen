using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class MacroSourceTests
{
    private static ContentDigest Digest(char value) => new("sha256", "descriptor/v1", new string(value, 64));

    private static ITrustedCatalogue Catalogue()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", Digest('a')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("value", text)], text),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))
        ]);
    }

    [Fact]
    public async Task Repeated_macro_calls_get_distinct_group_paths_and_preserve_runtime_bindings()
    {
        const string source = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              yield inner;
            }
            workflow demo(input: string) -> string {
              use first = Echo(value: input);
              use second = Echo(value: first);
              return second;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(Diagnostics(result));
        result.Plan!.IrVersion.Should().Be(FuwenContracts.GroupExpansionIrVersion);
        result.GroupedDefinition.Should().NotBeNull();
        var activities = result.Plan.Nodes.OfType<ActivityNode>().ToArray();
        activities.Select(node => node.StructuralPath).Should().Equal(
            "demo/$group/first/inner", "demo/$group/second/inner");
        activities[0].Arguments.Single().Value.Should().BeOfType<InputBinding>();
        activities[1].Arguments.Single().Value.Should().BeOfType<NodeOutputBinding>()
            .Which.NodePath.Should().Be("demo/$group/first/inner");

        var manifest = result.GroupedDefinition!.Groups;
        manifest.Groups.Should().HaveCount(2);
        manifest.Groups.Select(group => group.GroupKey).Should().BeEquivalentTo(
            "demo/$group/first", "demo/$group/second");
        manifest.Groups.Should().OnlyContain(group => group.Kind == WorkflowGroupKind.Macro && group.ParentGroupKey == null);
        manifest.Groups.Select(group => group.Provenance!.MacroName).Should().OnlyContain(name => name == "Echo");
        manifest.Members.Should().BeEquivalentTo([
            new WorkflowGroupMember("demo/$group/first/inner", "demo/$group/first"),
            new WorkflowGroupMember("demo/$group/second/inner", "demo/$group/second")]);
        result.SourceMap!.Entries.Select(entry => entry.StructuralPath).Should().Contain(activities.Select(node => node.StructuralPath));
    }

    [Fact]
    public async Task Nested_macro_and_logical_group_scopes_form_a_parented_hierarchy()
    {
        const string source = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              yield inner;
            }
            macro Wrapped(value: string) {
              use call = Echo(value: value);
              yield call;
            }
            workflow demo(input: string) -> string {
              group stage {
                use expanded = Wrapped(value: input);
                yield expanded;
              }
              return stage;
            }
            """;

        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(Diagnostics(result));
        var manifest = result.GroupedDefinition!.Groups;
        var logical = manifest.Groups.Single(group => group.Kind == WorkflowGroupKind.Logical);
        var wrapper = manifest.Groups.Single(group => group.Provenance?.MacroName == "Wrapped");
        var inner = manifest.Groups.Single(group => group.Provenance?.MacroName == "Echo");
        logical.GroupKey.Should().Be("demo/$group/stage");
        wrapper.ParentGroupKey.Should().Be(logical.GroupKey);
        inner.ParentGroupKey.Should().Be(wrapper.GroupKey);
        manifest.Members.Should().ContainSingle().Which.GroupKey.Should().Be(inner.GroupKey);
        result.Plan!.Nodes.OfType<ReturnNode>().Single().Value.Should().BeOfType<NodeOutputBinding>()
            .Which.NodePath.Should().Be("demo/$group/stage/$group/expanded/$group/call/inner");
    }

    [Fact]
    public async Task Group_yield_exports_only_the_named_result_and_plain_source_has_no_group_bundle()
    {
        const string grouped = """
            workflow demo(input: string) -> string {
              group stage {
                activity privateNode = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
                yield privateNode;
              }
              activity after = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: stage;) -> string;
              return after;
            }
            """;
        var compiledGroup = await new FuwenSourceCompiler(Catalogue()).CompileAsync(grouped, cancellationToken: TestContext.Current.CancellationToken);
        compiledGroup.Succeeded.Should().BeTrue(Diagnostics(compiledGroup));
        compiledGroup.Plan!.Nodes.OfType<ActivityNode>().Last().Arguments.Single().Value.Should().BeOfType<NodeOutputBinding>()
            .Which.NodePath.Should().Be("demo/$group/stage/privateNode");

        var leaked = grouped.Replace("(value: stage;)", "(value: privateNode;)", StringComparison.Ordinal);
        var failed = await new FuwenSourceCompiler(Catalogue()).CompileAsync(leaked, cancellationToken: TestContext.Current.CancellationToken);
        failed.Succeeded.Should().BeFalse();
        failed.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);

        const string plain = "workflow plain(input: string) -> string { activity echo = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: input;) -> string; return echo; }";
        var compiledPlain = await new FuwenSourceCompiler(Catalogue()).CompileAsync(plain, cancellationToken: TestContext.Current.CancellationToken);
        compiledPlain.Succeeded.Should().BeTrue(Diagnostics(compiledPlain));
        compiledPlain.GroupedDefinition.Should().BeNull();
        compiledPlain.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersion);
    }

    [Theory]
    [InlineData("macro Echo(value: string) { activity inner = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: value;) -> string; yield inner; } workflow demo(input: string) -> string { use call = Echo(); return call; }", "missing argument")]
    [InlineData("macro Echo(value: string) { activity inner = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: value;) -> string; yield inner; } workflow demo(input: string) -> string { use call = Echo(value: input, other: input); return call; }", "no parameter")]
    [InlineData("macro Echo(value: string) { use nested = Echo(value: value); yield nested; } workflow demo(input: string) -> string { use call = Echo(value: input); return call; }", "Recursive macro expansion")]
    public async Task Invalid_arguments_and_recursive_expansion_fail_closed(string source, string expectedMessage)
    {
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
        result.GroupedDefinition.Should().BeNull();
    }

    [Theory]
    [InlineData("macro Echo(value: string) { activity inner = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: input;) -> string; yield inner; } workflow demo(input: string) -> string { use call = Echo(value: input); return call; }")]
    [InlineData("macro Inner() { activity inner = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: secret;) -> string; yield inner; } macro Outer(secret: string) { use nested = Inner(); yield nested; } workflow demo(input: string) -> string { use call = Outer(secret: input); return call; }")]
    public async Task Macro_bodies_cannot_capture_caller_input_or_enclosing_parameters(string source)
    {
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == CompilerDiagnosticCodes.BindingReferenceInvalid);
        result.GroupedDefinition.Should().BeNull();
    }

    [Fact]
    public async Task Transitive_macro_fingerprinting_respects_the_configured_nesting_limit()
    {
        var declarations = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var body = index == 4
                ? "activity inner = activity \"sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" (value: value;) -> string; yield inner;"
                : $"use nested = M{index + 1}(value: value); yield nested;";
            declarations.Add($"macro M{index}(value: string) {{ {body} }}");
        }
        var source = string.Join("\n", declarations) + "\nworkflow demo(input: string) -> string { use call = M0(value: input); return call; }";
        var budget = new CompilationBudget(maxNestingDepth: 2);
        var result = await new FuwenSourceCompiler(Catalogue(), budget).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == CompilerDiagnosticCodes.BudgetNestingDepthExceeded);
        result.GroupedDefinition.Should().BeNull();
    }

    [Fact]
    public async Task Yielded_group_alias_cannot_overwrite_an_existing_workflow_binding()
    {
        const string source = """
            workflow demo(input: string) -> string {
              activity existing = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              group existing { activity inside = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string; yield inside; }
              return existing;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Message.Contains("already defined", StringComparison.Ordinal));
        result.GroupedDefinition.Should().BeNull();
    }

    [Fact]
    public async Task Macro_fingerprint_tracks_canonical_definition_tokens()
    {
        const string sourceTemplate = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: VALUE;) -> string;
              yield inner;
            }
            workflow demo(input: string) -> string { use call = Echo(value: input); return call; }
            """;
        var first = await new FuwenSourceCompiler(Catalogue()).CompileAsync(sourceTemplate.Replace("VALUE", "value", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);
        var second = await new FuwenSourceCompiler(Catalogue()).CompileAsync(
            sourceTemplate.Replace("VALUE", "value", StringComparison.Ordinal).Replace("inner", "inside", StringComparison.Ordinal),
            cancellationToken: TestContext.Current.CancellationToken);
        first.Succeeded.Should().BeTrue(Diagnostics(first));
        second.Succeeded.Should().BeTrue(Diagnostics(second));
        first.GroupedDefinition!.Groups.Groups.Single().Provenance!.DefinitionFingerprint.Should()
            .NotBe(second.GroupedDefinition!.Groups.Groups.Single().Provenance!.DefinitionFingerprint);
    }

    [Fact]
    public async Task Macro_fingerprint_includes_transitive_nested_definition_pins()
    {
        const string sourceTemplate = """
            macro Inner(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: VALUE;) -> string;
              yield inner;
            }
            macro Outer(value: string) { use nested = Inner(value: value); yield nested; }
            workflow demo(input: string) -> string { use call = Outer(value: input); return call; }
            """;
        var first = await new FuwenSourceCompiler(Catalogue()).CompileAsync(sourceTemplate.Replace("VALUE", "value", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);
        var second = await new FuwenSourceCompiler(Catalogue()).CompileAsync(
            sourceTemplate.Replace("VALUE", "value", StringComparison.Ordinal).Replace("inner", "inside", StringComparison.Ordinal),
            cancellationToken: TestContext.Current.CancellationToken);
        first.Succeeded.Should().BeTrue(Diagnostics(first));
        second.Succeeded.Should().BeTrue(Diagnostics(second));
        first.GroupedDefinition!.Groups.Groups.Single(group => group.Provenance?.MacroName == "Outer").Provenance!.DefinitionFingerprint
            .Should().NotBe(second.GroupedDefinition!.Groups.Groups.Single(group => group.Provenance?.MacroName == "Outer").Provenance!.DefinitionFingerprint);
    }

    [Fact]
    public async Task V4_group_paths_are_versioned_and_legacy_plans_reject_them()
    {
        const string source = """
            workflow demo(input: string) -> string {
              group stage {
                activity inside = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: input;) -> string;
              }
              return input;
            }
            """;
        var result = await new FuwenSourceCompiler(Catalogue()).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        result.Succeeded.Should().BeTrue(Diagnostics(result));
        var groupedPlan = result.Plan!;
        WorkflowPlanValidator.Validate(groupedPlan);
        var legacyPlan = groupedPlan with { IrVersion = FuwenContracts.IrVersion };
        var validateLegacy = () => WorkflowPlanValidator.Validate(legacyPlan);
        validateLegacy.Should().Throw<ArgumentException>().WithMessage("*must equal*");

        var malformedNode = groupedPlan.Nodes.OfType<ActivityNode>().Single() with { StructuralPath = "demo/$group/$invalid/inside" };
        var malformedPlan = groupedPlan with { Nodes = [malformedNode, groupedPlan.Nodes.OfType<ReturnNode>().Single()] };
        var validateMalformed = () => WorkflowPlanValidator.Validate(malformedPlan);
        validateMalformed.Should().Throw<ArgumentException>().WithMessage("*must equal*");
    }

    [Fact]
    public async Task Topology_parameters_and_expansion_beyond_the_node_budget_are_rejected()
    {
        const string topology = """
            macro Echo(value: list<string>) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              yield inner;
            }
            workflow demo(input: string) -> string { use call = Echo(value: input); return call; }
            """;
        var invalidType = await new FuwenSourceCompiler(Catalogue()).CompileAsync(topology, cancellationToken: TestContext.Current.CancellationToken);
        invalidType.Succeeded.Should().BeFalse();
        invalidType.Diagnostics.Should().Contain(diagnostic => diagnostic.Message.Contains("runtime data type", StringComparison.Ordinal));

        const string repeated = """
            macro Echo(value: string) {
              activity inner = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
              yield inner;
            }
            workflow demo(input: string) -> string {
              use first = Echo(value: input);
              use second = Echo(value: input);
              return second;
            }
            """;
        var bounded = new CompilationBudget(maxWorkflowNodes: 1);
        var overBudget = await new FuwenSourceCompiler(Catalogue(), bounded).CompileAsync(repeated, cancellationToken: TestContext.Current.CancellationToken);
        overBudget.Succeeded.Should().BeFalse();
        overBudget.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == CompilerDiagnosticCodes.BudgetWorkflowNodesExceeded);

        var parseBound = new CompilationBudget(maxAstNodes: 5);
        var tooMuchExpansion = await new FuwenSourceCompiler(Catalogue(), parseBound).CompileAsync(repeated, cancellationToken: TestContext.Current.CancellationToken);
        tooMuchExpansion.Succeeded.Should().BeFalse();
        tooMuchExpansion.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == CompilerDiagnosticCodes.BudgetAstNodesExceeded);
    }

    private static string Diagnostics(FuwenSourceCompilationResult result) =>
        string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}"));
}
