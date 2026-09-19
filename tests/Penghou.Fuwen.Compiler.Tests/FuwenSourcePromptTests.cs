using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

/// <summary>Workflow-owned prompt declarations: grammar, validation, identity.</summary>
public sealed class FuwenSourcePromptTests
{
    private static ITrustedCatalogue Catalogue() => new InMemoryTrustedCatalogue([]);

    private static ITrustedCatalogue InferenceCatalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        ContentDigest DigestOf(char c) => new("sha256", "descriptor/v1", new string(c, 64));
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", DigestOf('d')),
                callableContract: new CallableContract(
                    new CallableSignature([new CallableParameter("request", str)], str),
                    CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.PromptTemplate, "sample.template", "1", DigestOf('e'))),
        ]);
    }

    private const string ProfileRef = "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string TemplateRef = "sample.template@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private const string MinimalWorkflow = "workflow demo(input: string) -> string { return input; }";

    private static string PromptSource(string prompt) => prompt + "\n" + MinimalWorkflow;

    [Fact]
    public async Task Prompt_declaration_with_typed_params_compiles_to_v8()
    {
        const string source = """
            prompt implement_component(component_name: string) {
              system "You implement one component. Follow the supplied contracts."
              user "Implement component {{ component_name }} satisfying the output contract."
            }
            workflow demo(input: string) -> string { return input; }
            """;

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan.Should().NotBeNull();
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersionV8);
        result.Plan.Prompts.Should().ContainSingle();
        var prompt = result.Plan.Prompts![0];
        prompt.Name.Should().Be("implement_component");
        prompt.Parameters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PromptParameter("component_name", new PrimitiveType(FuwenPrimitiveKind.String)));
        prompt.Messages.Select(message => message.Role).Should().Equal(PromptMessageRole.System, PromptMessageRole.User);
    }

    [Fact]
    public async Task Triple_quoted_prompt_text_preserves_newlines_verbatim()
    {
        var source = "prompt review(name: string) {\n  user \"\"\"Review component {{ name }}.\n\nBe thorough.\"\"\"\n}\n" + MinimalWorkflow;

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        var template = result.Plan!.Prompts![0].Messages.Single().Template;
        template.Should().Be("Review component {{ name }}.\n\nBe thorough.");
    }

    [Fact]
    public async Task Duplicate_prompt_names_are_rejected()
    {
        var source = PromptSource(
            "prompt a(x: string) {\n  user \"{{ x }}\"\n}\n" +
            "prompt a(y: string) {\n  user \"{{ y }}\"\n}");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("Duplicate prompt definition 'a'"));
    }

    [Fact]
    public async Task Duplicate_prompt_parameters_are_rejected()
    {
        var source = PromptSource("prompt a(x: string, x: integer) {\n  user \"{{ x }}\"\n}");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("Duplicate prompt parameter 'x'"));
    }

    [Fact]
    public async Task Unknown_placeholder_is_rejected()
    {
        var source = PromptSource("prompt a(x: string) {\n  user \"{{ x }} and {{ y }}\"\n}");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("undeclared parameter"));
    }

    [Fact]
    public async Task Malformed_placeholder_is_rejected()
    {
        var source = PromptSource("prompt a(x: string) {\n  user \"{{ }} {{ x }}\"\n}");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("Malformed prompt placeholder"));
    }

    [Fact]
    public async Task Prompt_without_messages_is_rejected()
    {
        var source = PromptSource("prompt a(x: string) {\n}");

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("at least one message"));
    }

    [Fact]
    public async Task Unterminated_triple_quoted_string_is_rejected()
    {
        var source = "prompt a(x: string) {\n  user \"\"\"{{ x }}\n}\n" + MinimalWorkflow;

        var result = await new FuwenSourceCompiler(Catalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("Unterminated triple-quoted string"));
    }

    [Fact]
    public void Semantic_digest_is_stable_and_sensitive_to_content()
    {
        var first = new PromptDefinition(
            "implement",
            [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
            [new PromptMessage(PromptMessageRole.System, "Build {{ name }}.")]);
        var same = new PromptDefinition(
            "implement",
            [new PromptParameter("name", new PrimitiveType(FuwenPrimitiveKind.String))],
            [new PromptMessage(PromptMessageRole.System, "Build {{ name }}.")]);
        var reworded = first with
        {
            Messages = [new PromptMessage(PromptMessageRole.System, "Build {{ name }} now.")],
        };

        first.GetSemanticDigest().Should().Be(same.GetSemanticDigest());
        first.GetSemanticDigest().Should().NotBe(reworded.GetSemanticDigest());
        first.GetSemanticDigest().Should().MatchRegex(@"^sha256:prompt-definition/v1:[0-9a-f]{64}$");
    }

    [Fact]
    public void Placeholder_extraction_returns_ordered_names_and_rejects_malformed()
    {
        PromptDefinition.GetPlaceholders("{{ a }} and {{b}} and {{  a  }}")
            .Should().Equal("a", "b", "a");
        var act = () => PromptDefinition.GetPlaceholders("{{ }}");
        act.Should().Throw<ArgumentException>().WithMessage("*Malformed prompt placeholder*");
    }

    [Fact]
    public void Prompts_require_v8()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var prompt = new PromptDefinition(
            "p", [], [new PromptMessage(PromptMessageRole.User, "hi")]);
        var plan = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
            .AddNode(new ReturnNode("return_result", returnPath, new InputBinding([])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([returnPath])]),
            ]))
            .AddPrompt(prompt)
            .BuildV7();

        var act = () => WorkflowPlanValidator.Validate(plan);

        act.Should().Throw<ArgumentException>().WithMessage("*IR v8*");
    }

    [Fact]
    public void V8_plan_with_prompts_validates_and_fingerprints()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        PromptDefinition Prompt(string name, string text) => new(
            name, [], [new PromptMessage(PromptMessageRole.User, text)]);
        WorkflowPlan Build(params PromptDefinition[] prompts)
        {
            var builder = new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
                .AddNode(new ReturnNode("return_result", returnPath, new InputBinding([])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([returnPath])]),
                ]));
            foreach (var definition in prompts)
                builder.AddPrompt(definition);
            return builder.BuildV8();
        }

        var first = Build(Prompt("b", "second"), Prompt("a", "first"));
        var reordered = Build(Prompt("a", "first"), Prompt("b", "second"));
        var changed = Build(Prompt("a", "first!"), Prompt("b", "second"));

        WorkflowPlanValidator.Validate(first);
        var firstFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(first);
        var reorderedFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(reordered);
        var changedFingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(changed);

        firstFingerprint.Should().MatchRegex(@"^sha256:fuwen-execution/v8:[0-9a-f]{64}$");
        reorderedFingerprint.Should().Be(firstFingerprint);
        changedFingerprint.Should().NotBe(firstFingerprint);
    }

    [Fact]
    public void Plan_comparison_detects_prompt_text_changes()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        WorkflowPlan Build(string text)
        {
            return new WorkflowPlanBuilder("demo", "1", str, str, "routing/1")
                .AddNode(new ReturnNode("return_result", returnPath, new InputBinding([])))
                .SetExecutionOrder(new WorkflowExecutionOrder([
                    new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([returnPath])]),
                ]))
                .AddPrompt(new PromptDefinition(
                    "p", [], [new PromptMessage(PromptMessageRole.User, text)]))
                .BuildV8();
        }

        var beforeDefinition = WorkflowDefinitionDocument.Create(Build("first"));
        var afterDefinition = WorkflowDefinitionDocument.Create(Build("second"));
        var comparison = PlanRevisionComparer.Compare(
            PlanRevisionDocument.Create(beforeDefinition, "revision/1", null, Semantics()),
            beforeDefinition,
            PlanRevisionDocument.Create(afterDefinition, "revision/2", "revision/1", Semantics()),
            afterDefinition);

        comparison.ExecutionFingerprintEqual.Should().BeFalse();
        comparison.Changes.Should().Contain(change => change.Kind == PlanChangeKind.Changed);
    }

    private static PlanRevisionSemantics Semantics() => new(
        new ContentDigest("sha256", "objective/v1", new string('a', 64)),
        new ContentDigest("sha256", "acceptance/v1", new string('b', 64)),
        new ContentDigest("sha256", "validation/v1", new string('c', 64)));

    [Fact]
    public async Task Inference_with_prompt_reference_compiles_to_v8()
    {
        var source =
            "prompt greet(name: string) {\n" +
            "  system \"You greet users.\"\n" +
            "  user \"Greet {{ name }}.\"\n" +
            "}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        result.Plan!.IrVersion.Should().Be(FuwenContracts.IrVersionV8);
        var inference = result.Plan.Nodes.OfType<InferenceNode>().Single();
        inference.PromptName.Should().Be("greet");
        inference.PromptTemplate.Should().BeNull();
        inference.PromptBindings.Should().ContainSingle()
            .Which.ParameterName.Should().Be("name");
    }

    [Fact]
    public async Task Prompt_alias_for_registered_template_compiles()
    {
        var source =
            "prompt standard_greeting(name: string) uses registered \"sample.template@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\";\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt standard_greeting(name: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        var prompt = result.Plan!.Prompts!.Single(p => p.Name == "standard_greeting");
        prompt.Messages.Should().BeEmpty();
        prompt.RegisteredSource.Should().NotBeNull();
        prompt.RegisteredSource!.Name.Should().Be("sample.template");
    }

    [Fact]
    public async Task Unknown_prompt_reference_is_rejected()
    {
        var source =
            "prompt greet(name: string) {\n  user \"Hi {{ name}}.\"\n}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt missing(name: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("unknown prompt 'missing'"));
    }

    [Fact]
    public async Task Missing_required_prompt_binding_is_rejected()
    {
        var source =
            "prompt greet(first: string, last: string) {\n  user \"Hi {{ first }} {{ last}}.\"\n}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(first: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("does not bind required prompt parameter 'last'"));
    }

    [Fact]
    public async Task Unknown_prompt_binding_is_rejected()
    {
        var source =
            "prompt greet(name: string) {\n  user \"Hi {{ name}}.\"\n}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(nickname: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => ((d.Message + ' ' + d.Actual)).Contains("unknown prompt parameter 'nickname'"));
    }

    [Fact]
    public async Task Prompt_binding_type_mismatch_is_rejected()
    {
        var source =
            "prompt greet(count: integer) {\n  user \"Greet {{ count }} times.\"\n}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(count: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == CompilerDiagnosticCodes.BindingTypeMismatch);
    }

    [Fact]
    public async Task Optional_prompt_parameter_may_be_omitted()
    {
        var source =
            "prompt greet(name: string, title: string?) {\n  user \"Hi {{ name}}.\"\n}\n" +
            "workflow demo(input: string) -> string {\n" +
            $"  infer hello = infer \"{ProfileRef}\" prompt greet(name: input;) -> string;\n" +
            "  return hello;\n" +
            "}";

        var result = await new FuwenSourceCompiler(InferenceCatalogue())
            .CompileAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
    }
}


