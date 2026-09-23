using FluentAssertions;
using System.Xml.Linq;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class ReleaseWorkflowContractTests
{
    [Fact]
    public async Task Release_documents_cover_the_checked_in_preview_and_link_the_capability_matrix()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var version = props.Descendants("Version").Single().Value;
        var changelog = await File.ReadAllTextAsync(
            Path.Combine(root, "CHANGELOG.md"), TestContext.Current.CancellationToken);
        var readme = await File.ReadAllTextAsync(
            Path.Combine(root, "README.md"), TestContext.Current.CancellationToken);
        var matrixPath = Path.Combine(root, "docs", "capability-matrix.md");

        version.Should().Be("0.1.0-preview.11");
        var headings = changelog.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        for (var preview = 1; preview <= 11; preview++)
            headings.Should().Contain($"## 0.1.0-preview.{preview}");
        File.Exists(matrixPath).Should().BeTrue();
        readme.Should().Contain("docs/capability-matrix.md");
    }

    [Fact]
    public async Task Publish_workflow_validates_exact_versioned_commit_and_reuses_its_artifacts()
    {
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), ".github", "workflows", "publish.yml"),
            TestContext.Current.CancellationToken);

        workflow.Should().Contain("expected_tag=\"v${version}\"");
        workflow.Should().Contain("[ \"$GITHUB_REF_NAME\" != \"$expected_tag\" ]");
        workflow.Should().NotContain("version=\"${GITHUB_REF_NAME#v}\"");
        workflow.Should().Contain("dotnet build Penghou.Fuwen.slnx --configuration Release");
        workflow.Should().Contain("dotnet format Penghou.Fuwen.slnx --verify-no-changes --no-restore");
        workflow.Should().Contain("dotnet test Penghou.Fuwen.slnx --configuration Release --no-build");
        workflow.Should().Contain("python3 tests/golden/canonical_json_v1.py");
        workflow.Should().Contain("needs: validate");
        workflow.Should().Contain("if-no-files-found: error");
        workflow.Should().Contain("release-packages-${{ github.sha }}");

        workflow.IndexOf("Upload validated packages", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("Download validated packages", StringComparison.Ordinal));
        workflow.IndexOf("Download validated packages", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("Push packages to NuGet", StringComparison.Ordinal));
        workflow.LastIndexOf("dotnet pack", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("publish:", StringComparison.Ordinal));
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
