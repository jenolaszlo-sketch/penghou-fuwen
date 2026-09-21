using System.Text.Json;
using FluentAssertions;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class DocumentationContractTests
{
    private static ContentDigest Digest(char value) =>
        new("sha256", "descriptor/v1", new string(value, 64));

    [Fact]
    public void Machine_readable_grammar_has_unique_object_keys_and_current_node_productions()
    {
        var grammarPath = Path.Combine(FindRepositoryRoot(), "docs", "fuwen-grammar.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(grammarPath));

        AssertUniqueObjectKeys(document.RootElement, "$", []);

        var productions = document.RootElement.GetProperty("productions");
        productions.GetProperty("node").GetString().Should().ContainAll(
            "contextNode", "activityNode", "inferenceNode", "conditional",
            "fanout", "repeat", "checkpoint", "wait", "return");
        productions.GetProperty("fanoutBody").GetString().Should().ContainAll(
            "contextNode", "activityNode", "inferenceNode", "conditional");
        productions.GetProperty("repeatBody").GetString().Should().ContainAll(
            "contextNode", "activityNode", "inferenceNode", "conditional",
            "checkpoint", "wait");
        productions.GetProperty("inferenceNode").GetString().Should().ContainAll(
            "prompt", "tools", "limits");
        productions.GetProperty("prompt").GetString().Should().Contain("promptMessage+",
            "an inline prompt must contain at least one message; only registered aliases may be empty");
    }

    [Fact]
    public async Task Checked_in_language_corpus_compiles_formats_and_covers_documented_constructs()
    {
        var fixtureDirectory = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "documentation");
        var fixturePaths = Directory.GetFiles(fixtureDirectory, "*.fuwen").Order(StringComparer.Ordinal).ToArray();
        fixturePaths.Select(Path.GetFileName).Should().Equal(
            "capability.fuwen", "conditional.fuwen", "declarations-and-nodes.fuwen", "enum.fuwen", "fanout.fuwen",
            "inference-forms.fuwen", "interactions.fuwen", "repeat.fuwen", "schema.fuwen");

        var plans = new List<WorkflowPlan>();
        foreach (var fixturePath in fixturePaths)
        {
            var source = await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken);
            var result = await new FuwenSourceCompiler(
                Catalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll).CompileAsync(
                source, cancellationToken: TestContext.Current.CancellationToken);

            result.Succeeded.Should().BeTrue(
                $"{Path.GetFileName(fixturePath)} is an executable language example: " +
                string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
            result.Plan.Should().NotBeNull();
            var formatted = FuwenFormatter.Format(source);
            FuwenFormatter.Format(formatted).Should().Be(formatted);
            plans.Add(result.Plan!);
        }

        var nodes = plans.SelectMany(plan => plan.Nodes).ToArray();
        nodes.Should().Contain(node => node is ContextNode);
        nodes.Should().Contain(node => node is ActivityNode);
        nodes.Should().Contain(node => node is InferenceNode);
        nodes.Should().Contain(node => node is ConditionalNode);
        nodes.Should().Contain(node => node is FanOutNode);
        nodes.Should().Contain(node => node is RepeatNode);
        nodes.Should().Contain(node => node is CheckpointNode);
        nodes.Should().Contain(node => node is WaitNode);
        nodes.Should().Contain(node => node is ReturnNode);

        plans.SelectMany(plan => plan.Schemas).Should().Contain(schema => schema is ObjectSchemaDefinition);
        plans.SelectMany(plan => plan.Schemas).Should().Contain(schema => schema is EnumSchemaDefinition);
        plans.SelectMany(plan => plan.CapabilityManifest.Requirements).Should().ContainSingle();

        var inferencePlan = plans.Single(plan => plan.Name == "documented_inference");
        inferencePlan.Prompts!.Any(prompt => prompt.Messages.Count > 0).Should().BeTrue();
        inferencePlan.Prompts!.Any(prompt => prompt.RegisteredSource is not null).Should().BeTrue();
        var inferences = inferencePlan.Nodes.OfType<InferenceNode>().ToArray();
        inferences.Any(node => node.PromptName == "concise_greeting" && node.Limits is not null).Should().BeTrue();
        inferences.Any(node => node.PromptName == "registered_greeting").Should().BeTrue();
        inferences.Any(node => node.PromptTemplate is not null).Should().BeTrue();
        inferences.Any(node => node.Tools is { Count: > 0 }).Should().BeTrue();
        inferences.Any(node => node.Tools is null or { Count: 0 }).Should().BeTrue();
    }

    [Fact]
    public async Task Readme_fuwen_examples_compile()
    {
        var readme = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "README.md"), TestContext.Current.CancellationToken);
        var examples = ExtractFencedExamples(readme, "fuwen");
        examples.Should().NotBeEmpty("the primary language example is part of the public contract");

        foreach (var example in examples)
        {
            var result = await new FuwenSourceCompiler(
                Catalogue(), capabilityPolicy: CapabilityGrantPolicy.AllowAll).CompileAsync(
                example, cancellationToken: TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue(
                "README Fuwen examples must compile: " +
                string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}:{d.Message} path:{d.Path}")));
        }
    }

    private static IReadOnlyList<string> ExtractFencedExamples(string markdown, string language)
    {
        var opening = $"```{language}";
        var examples = new List<string>();
        var position = 0;
        while ((position = markdown.IndexOf(opening, position, StringComparison.Ordinal)) >= 0)
        {
            var contentStart = markdown.IndexOf('\n', position + opening.Length);
            if (contentStart < 0)
                break;
            var contentEnd = markdown.IndexOf("```", contentStart + 1, StringComparison.Ordinal);
            if (contentEnd < 0)
                break;
            examples.Add(markdown[(contentStart + 1)..contentEnd]);
            position = contentEnd + 3;
        }
        return examples;
    }

    private static ITrustedCatalogue Catalogue()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var requestDescriptor = new DescriptorReference(DescriptorKind.Schema, "sample.request", "1", Digest('1'));
        var modeDescriptor = new DescriptorReference(DescriptorKind.Schema, "sample.mode", "1", Digest('2'));
        CallableContract Unary(string parameter) => new(
            new CallableSignature([new CallableParameter(parameter, str)], str),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);

        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                requestDescriptor,
                new ObjectSchemaDefinition(requestDescriptor, [new SchemaField("value", str)])),
            new TrustedCatalogueDescriptor(
                modeDescriptor,
                new EnumSchemaDefinition(modeDescriptor, [new EnumMember("fast", "fast"), new EnumMember("safe", "safe")])),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.ContextProvider, "sample.context", "1", Digest('c')),
                callableContract: Unary("request")),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.ContextProvider, "sample.secure-context", "1", Digest('b')),
                requiredCapabilities: [new CapabilityRequirement("sample.read", "workspace")],
                callableContract: Unary("request")),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", Digest('a')),
                callableContract: Unary("value")),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.InferenceProfile, "sample.profile", "1", Digest('d')),
                callableContract: Unary("request")),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.PromptTemplate, "sample.prompt", "1", Digest('e'))),
            new TrustedCatalogueDescriptor(
                new DescriptorReference(DescriptorKind.Tool, "sample.search", "1", Digest('f')),
                callableContract: Unary("query")),
        ]);
    }

    private static void AssertUniqueObjectKeys(
        JsonElement element,
        string path,
        List<string> duplicates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        duplicates.Add($"{path}.{property.Name}");
                    AssertUniqueObjectKeys(property.Value, $"{path}.{property.Name}", duplicates);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    AssertUniqueObjectKeys(item, $"{path}[{index++}]", duplicates);
                break;
        }

        if (path == "$")
            duplicates.Should().BeEmpty("JSON object keys must be unambiguous for strict and last-value-wins consumers");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Penghou.Fuwen.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Penghou.Fuwen repository root.");
    }
}
