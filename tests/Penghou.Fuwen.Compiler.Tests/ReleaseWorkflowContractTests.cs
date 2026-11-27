using FluentAssertions;
using System.Text.Json;
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

        var headings = changelog.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        headings.Should().Contain($"## {version}", "the checked-in candidate must have its own release notes");
        for (var preview = 1; preview <= 13; preview++)
            headings.Should().Contain($"## 0.1.0-preview.{preview}");
        File.Exists(matrixPath).Should().BeTrue();
        readme.Should().Contain("docs/capability-matrix.md");
    }

    [Fact]
    public async Task Publish_workflow_validates_exact_versioned_commit_and_reuses_its_artifacts()
    {
        var root = FindRepositoryRoot();
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(root, ".github", "workflows", "publish.yml"),
            TestContext.Current.CancellationToken);
        var qualification = await File.ReadAllTextAsync(
            Path.Combine(root, ".github", "workflows", "release-qualification.yml"),
            TestContext.Current.CancellationToken);

        // The publisher delegates validation of the exact tagged commit to the
        // reusable qualification workflow and never builds or packs itself.
        workflow.Should().Contain("uses: ./.github/workflows/release-qualification.yml");
        workflow.Should().Contain("require-release-tag: true");
        workflow.Should().Contain("needs: validate");
        workflow.Should().Contain("environment: nuget");
        workflow.Should().Contain("release-packages-${{ github.sha }}-ubuntu-latest");
        workflow.Should().NotContain("dotnet pack");
        workflow.Should().NotContain("dotnet build");

        // Every downloaded archive is reverified before credentials are requested.
        workflow.IndexOf("Download validated packages", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("Reverify every archive", StringComparison.Ordinal));
        workflow.IndexOf("Reverify every archive", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("NuGet login", StringComparison.Ordinal));
        workflow.IndexOf("NuGet login", StringComparison.Ordinal).Should().BeLessThan(
            workflow.IndexOf("Push packages to NuGet", StringComparison.Ordinal));

        // The reusable qualification workflow validates the exact versioned
        // commit against the signed public Zhinu baseline. Source-rebuilt native
        // feed staging is not part of the canonical release path.
        qualification.Should().Contain("expected_tag=\"v${version}\"");
        qualification.Should().Contain("[ \"$GITHUB_REF_NAME\" != \"$expected_tag\" ]");
        qualification.Should().NotContain("version=\"${GITHUB_REF_NAME#v}\"");
        qualification.Should().Contain("Verify-PublicZhinuBaseline.ps1");
        qualification.Should().Contain("Test-PublicZhinuBaselineRejection.ps1");
        qualification.Should().NotContain("Prepare-ZhinuSourceCandidate.ps1");
        qualification.Should().NotContain("Verify-NativeFeed.ps1");
        qualification.Should().NotContain("artifacts/zhinu-candidate");
        workflow.Should().NotContain("zhinu-candidate");
        qualification.Should().Contain("dotnet build Penghou.Fuwen.slnx --configuration Release");
        qualification.Should().Contain("dotnet format Penghou.Fuwen.slnx --verify-no-changes --no-restore");
        qualification.Should().Contain("dotnet test Penghou.Fuwen.slnx --configuration Release --no-build");
        qualification.Should().Contain("python3 tests/golden/canonical_json_v1.py");
        qualification.Should().Contain("if-no-files-found: error");
        qualification.Should().Contain("release-packages-${{ github.sha }}-${{ matrix.os }}");

        // Validation order: verify the public baseline, build, test, then pack and upload.
        qualification.IndexOf("Verify signed public Zhinu baseline", StringComparison.Ordinal).Should().BeLessThan(
            qualification.IndexOf("Build", StringComparison.Ordinal));
        qualification.IndexOf("Build", StringComparison.Ordinal).Should().BeLessThan(
            qualification.IndexOf("Pack validated commit", StringComparison.Ordinal));
        qualification.IndexOf("Pack validated commit", StringComparison.Ordinal).Should().BeLessThan(
            qualification.IndexOf("Upload validated packages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canonical_dependency_resolution_uses_the_public_zhinu_baseline()
    {
        var root = FindRepositoryRoot();
        var nuget = await File.ReadAllTextAsync(
            Path.Combine(root, "NuGet.config"), TestContext.Current.CancellationToken);
        nuget.Should().Contain("https://api.nuget.org/v3/index.json");
        nuget.Should().NotContain("zhinu-candidate");

        var consumerNuget = await File.ReadAllTextAsync(
            Path.Combine(root, "eng", "NuGet.Consumer.config"),
            TestContext.Current.CancellationToken);
        consumerNuget.Should().Contain("fuwen-candidate");
        consumerNuget.Should().Contain("https://api.nuget.org/v3/index.json");
        consumerNuget.Should().Contain("<package pattern=\"Penghou.Fuwen*\" />");

        var baselinePath = Path.Combine(
            root, "docs", "qualification", "activity-groups", "zhinu-public-baseline.json");
        using var baseline = JsonDocument.Parse(
            await File.ReadAllTextAsync(baselinePath, TestContext.Current.CancellationToken));
        baseline.RootElement.GetProperty("Version").GetString().Should().Be("0.2.0-preview.10");
        baseline.RootElement.GetProperty("SourceCommit").GetString()
            .Should().Be("4ac6f238cb3734f0597356fa09e8c23554563902");
        var packages = baseline.RootElement.GetProperty("Packages").EnumerateArray().ToArray();
        packages.Should().HaveCount(7);
        packages.Should().OnlyContain(package =>
            package.GetProperty("Sha256").GetString()!.Length == 64 &&
            package.GetProperty("SnupkgSha256").GetString()!.Length == 64);

        var ci = await File.ReadAllTextAsync(
            Path.Combine(root, ".github", "workflows", "ci.yml"),
            TestContext.Current.CancellationToken);
        ci.Should().Contain("Verify-PublicZhinuBaseline.ps1");
        ci.Should().NotContain("Prepare-ZhinuCandidate");
        ci.Should().NotContain("zhinu-candidate");
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
