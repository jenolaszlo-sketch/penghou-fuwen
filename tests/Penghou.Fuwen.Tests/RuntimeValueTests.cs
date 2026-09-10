using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class RuntimeValueTests
{
    [Fact]
    public void JsonRuntimeValue_ClonesCallerOwnedDocument()
    {
        JsonRuntimeValue value;
        using (var document = JsonDocument.Parse("{\"items\":[1,2]}"))
            value = new JsonRuntimeValue(document.RootElement);

        value.Value.GetProperty("items").GetArrayLength().Should().Be(2);
        value.Value.GetProperty("items")[0].GetInt32().Should().Be(1);
    }

    [Fact]
    public void JsonRuntimeValue_enforces_size_node_and_depth_bounds_before_ownership()
    {
        using var largeDocument = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', JsonRuntimeValue.MaximumJsonUtf8Bytes)));
        ((Action)(() => new JsonRuntimeValue(largeDocument.RootElement)))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*UTF-8 bytes*");

        var manyNodesJson = $"[{string.Join(',', Enumerable.Repeat("0", JsonRuntimeValue.MaximumJsonNodes))}]";
        using var manyNodesDocument = JsonDocument.Parse(manyNodesJson);
        ((Action)(() => new JsonRuntimeValue(manyNodesDocument.RootElement)))
            .Should().Throw<ArgumentException>().WithMessage("*nodes*");

        var deepJson = new string('[', JsonRuntimeValue.MaximumJsonDepth + 1) + "0" + new string(']', JsonRuntimeValue.MaximumJsonDepth + 1);
        using var deepDocument = JsonDocument.Parse(deepJson, new JsonDocumentOptions { MaxDepth = JsonRuntimeValue.MaximumJsonDepth + 4 });
        ((Action)(() => new JsonRuntimeValue(deepDocument.RootElement)))
            .Should().Throw<ArgumentException>().WithMessage("*depth*");
    }

    [Fact]
    public void RuntimeEvidenceContracts_round_trip_through_canonical_json()
    {
        using var document = JsonDocument.Parse("{\"answer\":true}");
        var runtime = RuntimeValue.FromJson(document.RootElement);
        var roundTrip = CanonicalJson.Deserialize<RuntimeValue>(CanonicalJson.Serialize(runtime));
        roundTrip.Should().BeOfType<JsonRuntimeValue>();
        ((JsonRuntimeValue)roundTrip).Value.GetProperty("answer").GetBoolean().Should().BeTrue();

        var reference = new ContextSnapshotReference(
            Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p'),
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        var restored = CanonicalJson.Deserialize<ContextSnapshotReference>(CanonicalJson.Serialize(reference));
        restored.Provider.Should().Be(reference.Provider);
        restored.SnapshotId.Should().Be("snapshot/1");
    }

    [Fact]
    public void ContextSnapshotReference_DeeplySnapshotsBoundedEvidence()
    {
        var provider = Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p');
        var revisions = new List<ContextSourceRevisionReference>
        {
            new("repository", "revision/1", Digest("content/v1", 'c')),
        };
        var snapshot = new ContextSnapshotReference(
            provider,
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            revisions,
            "policy/1",
            new ContextSnapshotBudgetEvidence(true, 10, 1024, 10, 1024),
            new DateTimeOffset(2026, 9, 10, 4, 5, 6, TimeSpan.FromHours(2)),
            "receipt/1");
        revisions.Clear();

        snapshot.Provider.Kind.Should().Be(DescriptorKind.ContextProvider);
        snapshot.SourceRevisions.Should().ContainSingle();
        snapshot.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        snapshot.ProvenanceReceipt.Should().Be("receipt/1");
    }

    [Fact]
    public void ContextSnapshotReference_allows_over_budget_observations_when_truncated()
    {
        var create = () => new ContextSnapshotReference(
            Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p'),
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(true, 10, 100, 20, 200),
            DateTimeOffset.UtcNow);

        create().Budget.ObservedItems.Should().Be(20);
    }

    [Fact]
    public void RuntimeEvidence_rejects_malformed_sha256_digest_identities()
    {
        var malformed = new ContentDigest("sha256", "content/v1", new string('A', 64));
        var context = () => new ContextSnapshotReference(
            Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p'),
            "snapshot/1",
            malformed,
            Digest("content/v1", 'c'),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);

        context.Should().Throw<ArgumentException>().WithMessage("*lowercase hexadecimal*");

        var provider = new DescriptorReference(DescriptorKind.ContextProvider, "repo.context", "1", malformed);
        var providerContext = () => new ContextSnapshotReference(
            provider,
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        providerContext.Should().Throw<ArgumentException>().WithMessage("*lowercase hexadecimal*");

        var sourceContext = () => new ContextSnapshotReference(
            Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p'),
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            [new ContextSourceRevisionReference("repository", "1", malformed)],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        sourceContext.Should().Throw<ArgumentException>().WithMessage("*lowercase hexadecimal*");

        var artifact = new ArtifactReference(
            "store",
            "artifact/1",
            Descriptor(DescriptorKind.Artifact, "image/png", 'a'),
            malformed);
        var artifactValue = () => new ArtifactRuntimeValue(artifact);
        artifactValue.Should().Throw<ArgumentException>().WithMessage("*lowercase hexadecimal*");
    }

    [Fact]
    public void ContextSnapshotReference_RejectsWrongProviderAndOversizedCollections()
    {
        var schema = Descriptor(DescriptorKind.Schema, "not-context", 's');
        var act = () => new ContextSnapshotReference(
            schema,
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            [],
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        act.Should().Throw<ArgumentException>().WithMessage("*ContextProvider*");

        var revisions = Enumerable.Range(0, ContextSnapshotReference.MaximumSourceRevisions + 1)
            .Select(index => new ContextSourceRevisionReference($"source/{index}", "1", Digest("content/v1", 'c')))
            .ToArray();
        var oversized = () => new ContextSnapshotReference(
            Descriptor(DescriptorKind.ContextProvider, "repo.context", 'p'),
            "snapshot/1",
            Digest("request/v1", 'r'),
            Digest("content/v1", 'c'),
            revisions,
            "policy/1",
            new ContextSnapshotBudgetEvidence(false, null, null, null, null),
            DateTimeOffset.UtcNow);
        oversized.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static DescriptorReference Descriptor(DescriptorKind kind, string name, char digest) =>
        new(kind, name, "1", Digest("descriptor/v1", digest));

    private static ContentDigest Digest(string contract, char value) =>
        new("sha256", contract, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{contract}:{value}"))).ToLowerInvariant());
}
