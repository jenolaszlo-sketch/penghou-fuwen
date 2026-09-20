using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class DocumentationContractTests
{
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
