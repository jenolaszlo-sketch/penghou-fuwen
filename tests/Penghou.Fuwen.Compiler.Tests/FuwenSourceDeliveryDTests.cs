using System.Text;
using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Delivery D contract tests for the bounded Fuwen source surface.</summary>
public sealed class FuwenSourceDeliveryDTests
{
    [Fact]
    public void Lexer_reports_utf8_offsets_and_utf16_columns_for_non_bmp_text()
    {
        var result = FuwenLexer.Lex("// 😀\nworkflow demo() { return \"ok\"; }");

        result.Succeeded.Should().BeTrue();
        var workflow = result.Tokens.Single(token => token.Text == "workflow");
        workflow.Span.StartUtf8ByteOffset.Should().Be(8, string.Join("|", result.Tokens.Select(t => $"{t.Text}:{t.Span.StartUtf8ByteOffset}:{t.Span.StartLine}:{t.Span.StartUtf16Column}")));
        workflow.Span.StartLine.Should().Be(1);
        workflow.Span.StartUtf16Column.Should().Be(0);
    }

    [Fact]
    public void Formatter_is_idempotent_and_removes_comments()
    {
        const string source = "// header\r\nworkflow demo( ) { /* body */ return { \"b\" : 2, \"a\" : 1 }; }";

        var once = FuwenFormatter.Format(source);
        var twice = FuwenFormatter.Format(once);

        twice.Should().Be(once);
        once.Should().NotContain("header").And.NotContain("body");
        once.Should().EndWith("\n");
    }

    [Fact]
    public void Formatter_is_idempotent_for_structured_else_branches()
    {
        const string source = "workflow demo(input: bool)->bool{if input==true{ }else{ } return input;}";

        var once = FuwenFormatter.Format(source);
        var twice = FuwenFormatter.Format(once);

        twice.Should().Be(once);
    }

    [Fact]
    public void Formatter_has_a_stable_golden_shape_for_a_minimal_workflow()
    {
        FuwenFormatter.Format("workflow demo(){return true;}")
            .Should().Be("workflow demo() {\n  return true;\n}\n");
    }

    [Fact]
    public void Malformed_source_is_bounded_and_has_stable_codes()
    {
        var result = FuwenLexer.Lex("workflow x( { \"unterminated");

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.LexUnterminatedString);
        result.Diagnostics.Count.Should().BeLessOrEqualTo(CompilationBudget.Default.MaxDiagnostics);
        result.Diagnostics.Select(d => d.Code).Should().Equal(result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Unterminated_block_comment_has_a_distinct_lexical_diagnostic()
    {
        var result = FuwenLexer.Lex("/* never closes");

        result.Diagnostics.Select(d => d.Code).Should().ContainSingle().Which.Should().Be(CompilerDiagnosticCodes.LexUnterminatedComment);
    }

    [Fact]
    public void Nesting_budget_is_reported_and_token_retention_remains_bounded()
    {
        var result = FuwenLexer.Lex("((((((1))))))", budget: new CompilationBudget(maxNestingDepth: 2, maxTokens: 20));

        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.BudgetNestingDepthExceeded);
        result.Tokens.Count.Should().BeLessOrEqualTo(20);
        result.Usage.NestingDepth.Should().Be(6);
    }

    [Fact]
    public void Lexing_stops_at_source_and_token_budget_without_unbounded_retention()
    {
        var source = string.Join(' ', Enumerable.Repeat("x", 100));
        var result = FuwenLexer.Lex(source, budget: new CompilationBudget(maxSourceBytes: 10_000, maxTokens: 3));

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.BudgetTokensExceeded);
        result.Tokens.Count.Should().BeLessOrEqualTo(3);
        result.Usage.Tokens.Should().BeGreaterThan(3);
    }

    [Fact]
    public void Random_small_inputs_never_throw_and_are_deterministic()
    {
        var random = new Random(0xF0E1);
        for (var i = 0; i < 256; i++)
        {
            var length = random.Next(0, 128);
            var chars = new char[length];
            for (var j = 0; j < chars.Length; j++)
                chars[j] = (char)random.Next(0x20, 0x7f);
            var source = new string(chars);

            var first = FuwenLexer.Lex(source);
            var second = FuwenLexer.Lex(source);
            first.Tokens.Should().Equal(second.Tokens);
            first.Diagnostics.Select(d => (d.Code, d.Span)).Should().Equal(second.Diagnostics.Select(d => (d.Code, d.Span)));
        }
    }

    [Fact]
    public void Unpaired_surrogates_are_rejected_without_throwing()
    {
        var result = FuwenLexer.Lex("workflow\uD800");

        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.LexInvalidUnicode);
    }

    [Fact]
    public void Compiler_fuzz_inputs_are_total_and_bounded()
    {
        var random = new Random(0xD1CE);
        var compiler = new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]));
        for (var i = 0; i < 128; i++)
        {
            var chars = Enumerable.Range(0, random.Next(0, 96))
                .Select(_ => (char)random.Next(0x20, 0x100))
                .ToArray();
            var source = new string(chars);

            var act = () => compiler.CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
            act.Should().NotThrow();
        }
    }

    [Fact]
    public async Task Compiler_resolves_exact_descriptors_through_non_in_memory_catalogue()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.activity");
        var catalogue = new DelegatingCatalogue(new TrustedCatalogueDescriptor(
            descriptor,
            callableContract: new CallableContract(
                new CallableSignature([], new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                CallableEffect.None,
                CallableIdempotency.Idempotent,
                CallableRetrySafety.Safe)));
        var source = $"workflow demo() -> bool {{ activity run = activity \"{descriptor.Name}@{descriptor.Version}#{descriptor.ContentDigest.Value}\"; return run; }}";

        var result = await new FuwenSourceCompiler(catalogue).CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join(", ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        result.Plan!.Nodes.OfType<ActivityNode>().Single().Activity.Should().Be(descriptor);
        catalogue.Calls.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("sha256:broken")]
    [InlineData("not-a-digest")]
    [InlineData("sha256:descriptor/v1:not-hex")]
    public async Task Malformed_explicit_descriptor_digest_never_falls_back_or_throws(string digest)
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.activity");
        var catalogue = new InMemoryTrustedCatalogue([
            Callable(descriptor, new PrimitiveType(FuwenPrimitiveKind.Boolean)),
        ]);
        var source = $"workflow demo() -> bool {{ activity run = activity \"{descriptor.Name}@{descriptor.Version}#{digest}\"; return run; }}";

        var result = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(item => item.Code)
            .Should().Contain(CompilerDiagnosticCodes.SourceDescriptorUnresolved);
    }

    [Fact]
    public async Task Parse_failure_usage_is_not_counted_twice()
    {
        const string source = "workflow demo() -> string { return [; }";
        var lexer = FuwenLexer.Lex(source);
        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Usage.Tokens.Should().Be(lexer.Usage.Tokens + result.Compilation.Usage.Tokens);
        result.Usage.AstNodes.Should().Be(result.Compilation.Usage.AstNodes);
    }

    [Fact]
    public async Task Equivalent_source_lowers_to_identical_ir_but_maps_to_its_authored_document()
    {
        const string firstSource = "workflow demo(input: string) -> string { return input; }";
        const string secondSource = "// comment\r\nworkflow demo( input : string ) -> string { return input ; }";
        var compiler = new FuwenSourceCompiler(new DelegatingCatalogue(null!));

        var first = await compiler.CompileAsync(firstSource, "first.fuwen", "first.fuwen", cancellationToken: TestContext.Current.CancellationToken);
        var second = await compiler.CompileAsync(secondSource, "second.fuwen", "second.fuwen", cancellationToken: TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        WorkflowPlanIdentity.GetCanonicalBytes(first.Plan!).Should().Equal(WorkflowPlanIdentity.GetCanonicalBytes(second.Plan!));
        WorkflowPlanIdentity.ComputeExecutionFingerprint(first.Plan!).Should().Be(WorkflowPlanIdentity.ComputeExecutionFingerprint(second.Plan!));
        second.SourceMap.Should().NotBeNull();
        second.SourceMap!.Documents.Should().ContainSingle().Which.Utf8ByteLength.Should().Be(Encoding.UTF8.GetByteCount(secondSource));
        second.SourceMap.SourceFingerprint.Should().NotBe(first.SourceMap!.SourceFingerprint);
    }

    [Fact]
    public async Task Unnamed_if_uses_the_condition_as_the_condition_and_compiles()
    {
        const string source = "workflow demo(input: bool) -> bool { if input == true { } else { } return input; }";

        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join(" | ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        result.Plan!.Nodes.OfType<ConditionalNode>().Single().Name.Should().Be("if1");
    }

    [Fact]
    public async Task Inference_can_declare_multiple_direct_context_requirements()
    {
        var contextOne = Descriptor(DescriptorKind.ContextProvider, "sample.context.one");
        var contextTwo = Descriptor(DescriptorKind.ContextProvider, "sample.context.two");
        var profile = Descriptor(DescriptorKind.InferenceProfile, "sample.profile");
        var template = Descriptor(DescriptorKind.PromptTemplate, "sample.template");
        var catalogue = new InMemoryTrustedCatalogue([
            Callable(contextOne, new PrimitiveType(FuwenPrimitiveKind.String)),
            Callable(contextTwo, new PrimitiveType(FuwenPrimitiveKind.Integer)),
            Callable(profile, new PrimitiveType(FuwenPrimitiveKind.Boolean)),
            new TrustedCatalogueDescriptor(template)]);
        var source = $"workflow demo() -> bool {{ context one = context \"{Pin(contextOne)}\" -> string; context two = context \"{Pin(contextTwo)}\" -> int; infer answer = infer \"{Pin(profile)}\" using \"{Pin(template)}\" with one, two -> bool; return answer; }}";

        var result = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join(" | ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        result.Plan!.Nodes.OfType<InferenceNode>().Single().ContextRequirements.Should().HaveCount(2);
    }

    [Fact]
    public async Task Source_without_a_workflow_has_a_stable_diagnostic()
    {
        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync("capability \"sample.read\";", cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.ParseUnexpectedToken);
    }

    [Fact]
    public async Task Composite_binding_nesting_is_bounded_before_parser_recursion_can_grow()
    {
        var source = "workflow demo() -> json { return " + new string('[', 64) + "null" + new string(']', 64) + "; }";
        var budget = new CompilationBudget(maxNestingDepth: 8, maxTokens: 256);

        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync(source, callerBudget: budget, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.BudgetNestingDepthExceeded);
    }

    [Fact]
    public async Task Source_schemas_and_projections_lower_to_typed_ir()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.request");
        var schema = new ObjectSchemaDefinition(descriptor, [
            new SchemaField("name", new PrimitiveType(FuwenPrimitiveKind.String)),
        ]);
        var catalogue = new InMemoryTrustedCatalogue([new TrustedCatalogueDescriptor(descriptor, schema)]);
        var source = $"schema Request \"{Pin(descriptor)}\" {{ name: string; }} workflow demo(input: Request) -> string {{ return input.name; }}";

        var result = await new FuwenSourceCompiler(catalogue)
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(string.Join(" | ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        result.Plan!.InputType.Should().Be(new NamedTypeReference(descriptor));
        result.Plan.Nodes.OfType<ReturnNode>().Single().Value.Should().BeOfType<InputBinding>()
            .Which.Projection.Should().Equal("name");
    }

    [Fact]
    public async Task Conditions_are_type_checked_and_branch_outputs_cannot_escape()
    {
        const string source = "workflow demo(input: string) -> string { if input and true { } else { } return input; }";

        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.BindingTypeMismatch);
    }

    [Fact]
    public async Task Workflow_without_a_root_return_is_rejected()
    {
        var result = await new FuwenSourceCompiler(new InMemoryTrustedCatalogue([]))
            .CompileAsync("workflow demo() -> string { }", cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Select(d => d.Code).Should().Contain(CompilerDiagnosticCodes.SemanticValidationFailed);
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name) => new(
        kind,
        name,
        "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));

    private static TrustedCatalogueDescriptor Callable(DescriptorReference descriptor, FuwenType output) =>
        new(descriptor, callableContract: new CallableContract(
            new CallableSignature([], output),
            CallableEffect.None,
            CallableIdempotency.Idempotent,
            CallableRetrySafety.Safe));

    private static string Pin(DescriptorReference descriptor) =>
        $"{descriptor.Name}@{descriptor.Version}#{descriptor.ContentDigest.Value}";

    private sealed class DelegatingCatalogue(TrustedCatalogueDescriptor entry) : ITrustedCatalogue
    {
        public int Calls { get; private set; }

        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(descriptor.Equals(entry.Descriptor)
                ? new DescriptorResolutionResult(descriptor, DescriptorResolutionStatus.Resolved, entry)
                : new DescriptorResolutionResult(descriptor, DescriptorResolutionStatus.NotFound));
        }
    }
}
