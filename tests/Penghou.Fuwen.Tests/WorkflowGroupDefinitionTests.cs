using FluentAssertions;

namespace Penghou.Fuwen.Tests;

public sealed class WorkflowGroupDefinitionTests
{
    [Fact]
    public void Manifest_is_canonical_and_detached_from_input_collections()
    {
        var plan = PlanFixture.Create();
        var groups = Definitions();
        var members = Members(plan);
        var first = WorkflowGroupManifest.Create(groups, members);
        var reordered = WorkflowGroupManifest.Create(groups.AsEnumerable().Reverse(), members.AsEnumerable().Reverse());
        var expectedBytes = first.CanonicalBytes.ToArray();

        reordered.CanonicalBytes.ToArray().Should().Equal(expectedBytes);
        reordered.Fingerprint.Should().Be(first.Fingerprint);
        groups[0] = groups[0] with { DisplayName = "mutated after create" };
        members[0] = members[0] with { GroupKey = "missing" };
        first.Groups.Should().Contain(group => group.DisplayName == "Macro review");
        first.Members.Should().NotContain(member => member.GroupKey == "missing");

        var returned = first.CanonicalBytes.ToArray();
        returned[0] ^= 0xff;
        first.CanonicalBytes.ToArray().Should().Equal(expectedBytes);
        Action mutateExposedGroups = () =>
            ((IList<WorkflowGroupDefinition>)first.Groups)[0] = first.Groups[0] with { DisplayName = "changed" };
        mutateExposedGroups.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Manifest_rejects_cycles_duplicate_members_and_invalid_hierarchy()
    {
        var cycle = new[]
        {
            new WorkflowGroupDefinition("a", "b", WorkflowGroupKind.Logical, "A", 0),
            new WorkflowGroupDefinition("b", "a", WorkflowGroupKind.Logical, "B", 0),
        };
        var cycleAct = () => WorkflowGroupManifest.Create(cycle, []);

        cycleAct.Should().Throw<ArgumentException>().WithMessage("*cyclic*");

        var duplicateMemberAct = () => WorkflowGroupManifest.Create(
            [
                new WorkflowGroupDefinition("a", null, WorkflowGroupKind.Logical, "A", 0),
                new WorkflowGroupDefinition("b", null, WorkflowGroupKind.Logical, "B", 1),
            ],
            [new WorkflowGroupMember("answer/context", "a"), new WorkflowGroupMember("answer/context", "b")]);

        duplicateMemberAct.Should().Throw<ArgumentException>().WithMessage("*duplicate group membership*");

        var missingParentAct = () => WorkflowGroupManifest.Create(
            [new WorkflowGroupDefinition("child", "absent", WorkflowGroupKind.Logical, "Child", 0)], []);
        missingParentAct.Should().Throw<ArgumentException>().WithMessage("*parent is missing*");

        var duplicateOrdinalAct = () => WorkflowGroupManifest.Create(
            [
                new WorkflowGroupDefinition("a", null, WorkflowGroupKind.Logical, "A", 0),
                new WorkflowGroupDefinition("b", null, WorkflowGroupKind.Logical, "B", 0),
            ], []);
        duplicateOrdinalAct.Should().Throw<ArgumentException>().WithMessage("*duplicate group definition*");
    }

    [Fact]
    public void Manifest_enforces_macro_provenance_and_valid_source_coordinates()
    {
        var logicalWithProvenance = () => WorkflowGroupManifest.Create(
            [new WorkflowGroupDefinition("logical", null, WorkflowGroupKind.Logical, "Logical", 0,
                Provenance("macro"))], []);
        var macroWithoutProvenance = () => WorkflowGroupManifest.Create(
            [new WorkflowGroupDefinition("macro", null, WorkflowGroupKind.Macro, "Macro", 0)], []);
        var negativeSpan = () => WorkflowGroupManifest.Create(
            [new WorkflowGroupDefinition("macro", null, WorkflowGroupKind.Macro, "Macro", 0,
                Provenance("macro") with { InvocationSource = new SourceSpan("main", -1, 1, 0, 0) })], []);

        logicalWithProvenance.Should().Throw<ArgumentException>();
        macroWithoutProvenance.Should().Throw<ArgumentException>();
        negativeSpan.Should().Throw<ArgumentException>().WithMessage("*source reference*");
    }

    [Fact]
    public void Source_span_metadata_changes_group_identity_but_not_execution_identity()
    {
        var definition = WorkflowDefinitionDocument.Create(PlanFixture.Create());
        var first = GroupedWorkflowDefinitionDocument.Create(definition,
            WorkflowGroupManifest.Create(Definitions(10), Members(PlanFixture.Create())));
        var second = GroupedWorkflowDefinitionDocument.Create(definition,
            WorkflowGroupManifest.Create(Definitions(40), Members(PlanFixture.Create())));

        first.Definition.ExecutionFingerprint.Should().Be(second.Definition.ExecutionFingerprint);
        first.Fingerprint.Should().NotBe(second.Fingerprint);
        first.Groups.Fingerprint.Should().NotBe(second.Groups.Fingerprint);
        first.Groups.Groups.Single(group => group.Kind == WorkflowGroupKind.Macro)
            .Provenance!.DefinitionSource!.StartUtf8ByteOffset.Should().Be(10);
    }

    [Fact]
    public void Grouped_definition_rejects_wrong_pin_and_tampered_bytes()
    {
        var grouped = CreateGroupedDefinition();
        var wrongPin = () => GroupedWorkflowDefinitionDocument.LoadVerified(
            "sha256:fuwen-grouped-definition/v1:" + new string('f', 64), grouped.CanonicalBytes.Span);
        var tampered = grouped.CanonicalBytes.ToArray();
        tampered[0] ^= 0xff;
        var damagedBytes = () => GroupedWorkflowDefinitionDocument.LoadVerified(grouped.Fingerprint, tampered);

        wrongPin.Should().Throw<WorkflowDefinitionIntegrityException>();
        damagedBytes.Should().Throw<WorkflowDefinitionIntegrityException>();
    }

    [Fact]
    public async Task Directory_store_reopens_and_verifies_both_ordinary_and_grouped_definitions()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var plan = WorkflowDefinitionDocument.Create(PlanFixture.Create());
            var grouped = GroupedWorkflowDefinitionDocument.Create(plan,
                WorkflowGroupManifest.Create(Definitions(), Members(PlanFixture.Create())));
            var first = new DirectoryWorkflowDefinitionStore(directory);
            (await first.StoreAsync(plan, TestContext.Current.CancellationToken)).Should().Be(WorkflowDefinitionWriteDisposition.Created);
            (await first.StoreGroupedAsync(grouped, TestContext.Current.CancellationToken)).Should().Be(WorkflowDefinitionWriteDisposition.Created);

            var reopened = new DirectoryWorkflowDefinitionStore(directory);
            var loadedPlan = await reopened.ReadAsync(plan.ExecutionFingerprint, TestContext.Current.CancellationToken);
            var loadedGrouped = await reopened.ReadGroupedAsync(grouped.Fingerprint, TestContext.Current.CancellationToken);

            loadedPlan!.CanonicalBytes.ToArray().Should().Equal(plan.CanonicalBytes.ToArray());
            loadedGrouped!.CanonicalBytes.ToArray().Should().Equal(grouped.CanonicalBytes.ToArray());
            loadedGrouped.Groups.Fingerprint.Should().Be(grouped.Groups.Fingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Directory_store_concurrent_exact_grouped_writes_are_idempotent()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var grouped = CreateGroupedDefinition();
            var stores = Enumerable.Range(0, 8).Select(_ => new DirectoryWorkflowDefinitionStore(directory)).ToArray();

            var results = await Task.WhenAll(stores.Select(store => store.StoreGroupedAsync(
                grouped, TestContext.Current.CancellationToken).AsTask()));

            results.Count(result => result == WorkflowDefinitionWriteDisposition.Created).Should().Be(1);
            results.Count(result => result == WorkflowDefinitionWriteDisposition.AlreadyExists).Should().Be(7);
            (await stores[0].ReadGroupedAsync(grouped.Fingerprint, TestContext.Current.CancellationToken))
                .Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Directory_store_rejects_corrupt_contents_and_untrusted_path_keys()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var grouped = CreateGroupedDefinition();
            var store = new DirectoryWorkflowDefinitionStore(directory);
            await store.StoreGroupedAsync(grouped, TestContext.Current.CancellationToken);
            var storedPath = Directory.GetFiles(directory, "group-*.json").Single();
            await File.WriteAllBytesAsync(storedPath, [0x7b, 0x7d], TestContext.Current.CancellationToken);

            var corruptRead = () => store.ReadGroupedAsync(grouped.Fingerprint, TestContext.Current.CancellationToken).AsTask();
            var badPlanKey = () => store.ReadAsync("../../outside", TestContext.Current.CancellationToken).AsTask();
            var badGroupKey = () => store.ReadGroupedAsync("../outside", TestContext.Current.CancellationToken).AsTask();

            await corruptRead.Should().ThrowAsync<WorkflowDefinitionIntegrityException>();
            await badPlanKey.Should().ThrowAsync<ArgumentException>();
            await badGroupKey.Should().ThrowAsync<ArgumentException>();
            Directory.GetFiles(directory, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WorkflowGroupDefinition[] Definitions(int sourceOffset = 10) =>
    [
        new WorkflowGroupDefinition("macro/review", null, WorkflowGroupKind.Macro, "Macro review", 0,
            new WorkflowMacroProvenance("review", "sha256:macro:v1", "answer/review",
                new SourceSpan("main", sourceOffset, 12, 1, 0), new SourceSpan("main", sourceOffset + 20, 8, 2, 0))),
        new WorkflowGroupDefinition("macro/review/phase", "macro/review", WorkflowGroupKind.Logical, "Phase", 0),
    ];

    private static WorkflowGroupMember[] Members(WorkflowPlan plan) =>
    [
        new WorkflowGroupMember(plan.Nodes[0].StructuralPath, "macro/review"),
        new WorkflowGroupMember(plan.Nodes[1].StructuralPath, "macro/review/phase"),
    ];

    private static WorkflowMacroProvenance Provenance(string invocationKey) =>
        new("macro", "sha256:macro:v1", invocationKey);

    private static GroupedWorkflowDefinitionDocument CreateGroupedDefinition()
    {
        var plan = PlanFixture.Create();
        return GroupedWorkflowDefinitionDocument.Create(WorkflowDefinitionDocument.Create(plan),
            WorkflowGroupManifest.Create(Definitions(), Members(plan)));
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuwen-group-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
