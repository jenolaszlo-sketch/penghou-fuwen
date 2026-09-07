using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowDefinitionStoreTests
{
    [Fact]
    public async Task Store_is_idempotent_and_read_revalidates_the_plan()
    {
        IWorkflowDefinitionStore store = new InMemoryWorkflowDefinitionStore();
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.StoreAsync(definition, cancellationToken);
        var replay = await store.StoreAsync(definition, cancellationToken);
        var loaded = await store.ReadAsync(definition.ExecutionFingerprint, cancellationToken);

        first.Should().Be(WorkflowDefinitionWriteDisposition.Created);
        replay.Should().Be(WorkflowDefinitionWriteDisposition.AlreadyExists);
        loaded.Should().NotBeNull();
        loaded!.ExecutionFingerprint.Should().Be(definition.ExecutionFingerprint);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(loaded.ReadPlan())
            .Should().Be(definition.ExecutionFingerprint);
    }

    [Fact]
    public void V2_definition_loads_and_round_trips_with_its_v2_fingerprint()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());

        var loaded = WorkflowDefinitionDocument.LoadVerified(
            definition.ExecutionFingerprint,
            definition.CanonicalBytes.Span);

        loaded.ExecutionFingerprint.Should().Be(definition.ExecutionFingerprint);
        loaded.CanonicalBytes.ToArray().Should().Equal(definition.CanonicalBytes.ToArray());
        loaded.ReadPlan().IrVersion.Should().Be(FuwenContracts.IrVersionV2);
        WorkflowPlanIdentity.ComputeExecutionFingerprint(loaded.ReadPlan())
            .Should().Be(definition.ExecutionFingerprint);
    }

    [Fact]
    public void LoadVerified_preserves_historical_descriptor_digest_identity_without_semantic_admission()
    {
        var source = PlanFixture.Create();
        var historicalDescriptor = source.CatalogueBindings[0] with
        {
            ContentDigest = new ContentDigest("legacy-digest", "descriptor/legacy", "legacy-value"),
        };
        var historicalPlan = source with
        {
            CatalogueBindings = [historicalDescriptor, .. source.CatalogueBindings.Skip(1)],
        };
        var bytes = WorkflowPlanIdentity.GetCanonicalBytesForVerification(historicalPlan);
        var fingerprint = WorkflowPlanIdentity.ComputeExecutionFingerprint(bytes, historicalPlan.FingerprintVersion);

        var loaded = WorkflowDefinitionDocument.LoadVerified(
            fingerprint,
            bytes);

        loaded.ReadPlan().CatalogueBindings
            .Single(descriptor =>
                descriptor.Kind == historicalDescriptor.Kind &&
                descriptor.Name == historicalDescriptor.Name &&
                descriptor.Version == historicalDescriptor.Version)
            .ContentDigest.Should().Be(historicalDescriptor.ContentDigest);
    }

    [Fact]
    public void V1_and_v2_fingerprint_claims_cannot_cross_versions()
    {
        var v1 = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var v2 = WorkflowDefinitionDocument.Create(PlanFixture.CreateV2());
        var v1ClaimForV2 = v1.ExecutionFingerprint.Replace("/v1:", "/v2:", StringComparison.Ordinal);
        var v2ClaimForV1 = v2.ExecutionFingerprint.Replace("/v2:", "/v1:", StringComparison.Ordinal);

        var v1Act = () => WorkflowDefinitionDocument.LoadVerified(v2ClaimForV1, v1.CanonicalBytes.Span);
        var v2Act = () => WorkflowDefinitionDocument.LoadVerified(v1ClaimForV2, v2.CanonicalBytes.Span);

        v1Act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*fingerprint mismatch*");
        v2Act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*fingerprint mismatch*");
    }

    [Fact]
    public void Load_rejects_noncanonical_persisted_JSON()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var noncanonical = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(definition.CanonicalBytes.Span));

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            definition.ExecutionFingerprint,
            noncanonical);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*not canonical*");
    }

    [Fact]
    public void Load_rejects_a_false_fingerprint_claim()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            definition.CanonicalBytes.Span);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*fingerprint mismatch*");
    }

    [Fact]
    public void Load_rejects_malformed_fingerprint_keys_before_reading_JSON()
    {
        var act = () => WorkflowDefinitionDocument.LoadVerified("../../definition", []);

        act.Should().Throw<ArgumentException>().WithMessage("*canonical sha256*");
    }

    [Fact]
    public void Load_rejects_oversized_persisted_definitions()
    {
        var oversized = new byte[FuwenContracts.MaximumCanonicalPlanBytes + 1];

        var act = () => WorkflowDefinitionDocument.LoadVerified(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            oversized);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*exceeds*");
    }

    [Fact]
    public void Load_rejects_unsupported_IR_versions_without_claiming_semantic_admission()
    {
        var plan = PlanFixture.Create() with { IrVersion = "fuwen-ir/v999" };
        var bytes = CanonicalJson.Serialize(plan);
        var fingerprint = $"sha256:fuwen-execution/v1:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

        var act = () => WorkflowDefinitionDocument.LoadVerified(fingerprint, bytes);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*invalid*");
    }

    [Fact]
    public void Exposed_bytes_are_defensive_copies()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var first = definition.CanonicalBytes.ToArray();
        first[0] ^= 0xff;

        definition.CanonicalBytes.Span[0].Should().NotBe(first[0]);
        definition.ReadPlan().Should().NotBeNull();
    }

    [Fact]
    public void Create_detaches_nested_collections_before_hashing()
    {
        var plan = PlanFixture.Create();
        var schemas = (ResolvedSchemaDefinition[])plan.Schemas;
        var request = (ObjectSchemaDefinition)schemas.Single(schema => schema.Descriptor.Name == "sample.request");
        var fields = request.Fields.ToArray();
        schemas[Array.IndexOf(schemas, request)] = request with { Fields = fields };
        var definition = WorkflowDefinitionDocument.Create(plan);
        var expected = definition.CanonicalBytes.ToArray();

        fields[0] = new SchemaField("changed", new PrimitiveType(FuwenPrimitiveKind.Boolean));

        definition.CanonicalBytes.ToArray().Should().Equal(expected);
    }

    [Fact]
    public void Create_clones_literals_from_disposed_json_documents()
    {
        var plan = PlanFixture.Create();
        WorkflowDefinitionDocument definition;
        using (var json = JsonDocument.Parse("{\"answer\":\"frozen\"}"))
        {
            var nodes = (WorkflowNode[])plan.Nodes;
            var activity = (ActivityNode)nodes.Single(node => node.Name == "validate");
            nodes[2] = activity with
            {
                Arguments = [new ArgumentBinding("payload", new LiteralBinding(json.RootElement))],
            };

            definition = WorkflowDefinitionDocument.Create(plan);
        }
        var expected = definition.CanonicalBytes.ToArray();

        definition.CanonicalBytes.ToArray().Should().Equal(expected);
    }

    [Fact]
    public void Create_rejects_oversized_canonical_plans()
    {
        var plan = PlanFixture.Create() with { Revision = new string('r', FuwenContracts.MaximumCanonicalPlanBytes) };

        var act = () => WorkflowDefinitionDocument.Create(plan);

        act.Should().Throw<WorkflowDefinitionIntegrityException>().WithMessage("*exceeds*");
    }

    [Fact]
    public void Create_fingerprint_is_sha256_of_the_exact_canonical_bytes()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var expected = Convert.ToHexString(SHA256.HashData(definition.CanonicalBytes.Span)).ToLowerInvariant();

        definition.ExecutionFingerprint.Should().Be($"sha256:fuwen-execution/v1:{expected}");
    }

    [Fact]
    public void Create_rejects_null_nested_entries_before_serialization()
    {
        var nodes = PlanFixture.Create().Nodes.ToArray();
        nodes[0] = null!;
        var plan = PlanFixture.Create() with { Nodes = nodes };

        var act = () => WorkflowDefinitionDocument.Create(plan);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_rejects_cyclic_binding_graphs_with_a_bounded_failure()
    {
        var plan = PlanFixture.Create();
        var items = new List<Binding>();
        var cyclic = new ListBinding(items);
        items.Add(cyclic);
        var nodes = plan.Nodes.ToArray();
        var context = (ContextNode)nodes[0];
        nodes[0] = context with
        {
            Arguments = [new ArgumentBinding("cycle", cyclic)],
        };
        plan = plan with { Nodes = nodes };

        var act = () => WorkflowDefinitionDocument.Create(plan);

        act.Should().Throw<InvalidOperationException>().WithMessage("*reference cycle detected*");
    }

    [Fact]
    public void Create_rejects_excessive_binding_nesting_before_stack_exhaustion()
    {
        Binding nested = new InputBinding([]);
        for (var index = 0; index < WorkflowPlanSnapshotLimits.MaximumNestingDepth; index++)
            nested = new ListBinding([nested]);

        var plan = PlanFixture.Create();
        var nodes = plan.Nodes.ToArray();
        var context = (ContextNode)nodes[0];
        nodes[0] = context with { Arguments = [new ArgumentBinding("nested", nested)] };
        plan = plan with { Nodes = nodes };

        var act = () => WorkflowDefinitionDocument.Create(plan);

        act.Should().Throw<InvalidOperationException>().WithMessage("*maximum nesting depth exceeded*");
    }

    [Fact]
    public void Create_rejects_large_collections_before_snapshot_allocation()
    {
        var plan = PlanFixture.Create();
        var schemas = plan.Schemas.ToArray();
        var requestIndex = Array.FindIndex(schemas, schema => schema.Descriptor.Name == "sample.request");
        var request = (ObjectSchemaDefinition)schemas[requestIndex];
        var fields = Enumerable.Range(0, WorkflowPlanSnapshotLimits.MaximumCollectionCount + 1)
            .Select(index => new SchemaField($"field_{index}", new PrimitiveType(FuwenPrimitiveKind.String)))
            .ToArray();
        schemas[requestIndex] = request with { Fields = fields };
        plan = plan with { Schemas = schemas };

        var act = () => WorkflowDefinitionDocument.Create(plan);

        act.Should().Throw<InvalidOperationException>().WithMessage("*schema fields collection count exceeds*");
    }

    [Fact]
    public async Task Read_returns_null_for_an_unknown_fingerprint()
    {
        IWorkflowDefinitionStore store = new InMemoryWorkflowDefinitionStore();

        var loaded = await store.ReadAsync(
            "sha256:fuwen-execution/v1:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            TestContext.Current.CancellationToken);

        loaded.Should().BeNull();
    }
}
