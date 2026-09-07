using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using System.Diagnostics;

namespace Penghou.Fuwen.Compiler.Tests;

public sealed class CatalogueTests
{
    [Fact]
    public async Task InMemoryCatalogue_ResolvesOnlyTheExactPinnedDescriptor()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.answer", "1", 'a');
        var definition = new ObjectSchemaDefinition(
            descriptor,
            [new SchemaField("text", new PrimitiveType(FuwenPrimitiveKind.String))]);
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(
                descriptor,
                definition,
                [new CapabilityRequirement("schema.read", "answers")]),
        ]);

        var resolved = await catalogue.ResolveAsync(descriptor, TestContext.Current.CancellationToken);
        var wrongDigest = await catalogue.ResolveAsync(Descriptor(DescriptorKind.Schema, descriptor.Name, descriptor.Version, 'b'), TestContext.Current.CancellationToken);
        var wrongVersion = await catalogue.ResolveAsync(Descriptor(DescriptorKind.Schema, descriptor.Name, "2", 'a'), TestContext.Current.CancellationToken);

        resolved.Status.Should().Be(DescriptorResolutionStatus.Resolved);
        resolved.Succeeded.Should().BeTrue();
        resolved.Descriptor.Should().NotBeNull();
        resolved.Descriptor!.SchemaDefinition.Should().BeOfType<ObjectSchemaDefinition>();
        resolved.Descriptor.RequiredCapabilities.Should().ContainSingle();
        wrongDigest.Status.Should().Be(DescriptorResolutionStatus.DigestMismatch);
        wrongDigest.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.CatalogueDescriptorDigestMismatch);
        wrongDigest.Resolved.Should().Be(descriptor);
        wrongVersion.Status.Should().Be(DescriptorResolutionStatus.NotFound);
        wrongVersion.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.CatalogueDescriptorNotFound);
    }

    [Theory]
    [InlineData("sha-256", "descriptor/v1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256", "descriptor/v1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256", "descriptor/v1", "aaaaaaaa")]
    public void Trusted_catalogue_rejects_noncanonical_descriptor_digest_identity(
        string algorithm,
        string contract,
        string value)
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.invalid", "1", 'a') with
        {
            ContentDigest = new ContentDigest(algorithm, contract, value),
        };

        var act = () => new TrustedCatalogueDescriptor(descriptor);

        act.Should().Throw<ArgumentException>().WithMessage("*descriptor content digest*");
    }

    [Fact]
    public async Task Catalogue_SnapshotsEntriesAndResults()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.mutable", "1", 'a');
        var fields = new List<SchemaField>
        {
            new("text", new PrimitiveType(FuwenPrimitiveKind.String)),
        };
        var capabilities = new List<CapabilityRequirement>
        {
            new("schema.read"),
        };
        var entry = new TrustedCatalogueDescriptor(
            descriptor,
            new ObjectSchemaDefinition(descriptor, fields),
            capabilities);
        var catalogue = new InMemoryTrustedCatalogue([entry]);

        fields[0] = new SchemaField("changed", new PrimitiveType(FuwenPrimitiveKind.Boolean));
        capabilities[0] = new CapabilityRequirement("changed");
        var result = await catalogue.ResolveAsync(descriptor, TestContext.Current.CancellationToken);

        var schema = result.Descriptor!.SchemaDefinition.Should().BeOfType<ObjectSchemaDefinition>().Subject;
        schema.Fields.Should().ContainSingle().Which.Name.Should().Be("text");
        result.Descriptor.RequiredCapabilities.Should().ContainSingle().Which.Name.Should().Be("schema.read");
        result.Requested.Should().NotBeSameAs(descriptor);
        result.Requested.ContentDigest.Should().NotBeSameAs(descriptor.ContentDigest);
    }

    [Fact]
    public async Task Catalogue_SnapshotsCallableContractsAndRejectsInvalidShapes()
    {
        var activity = Descriptor(DescriptorKind.Activity, "sample.callable", "1", 'a');
        var parameters = new List<CallableParameter>
        {
            new("zeta", new PrimitiveType(FuwenPrimitiveKind.String)),
            new("request", new PrimitiveType(FuwenPrimitiveKind.String)),
        };
        var entry = new TrustedCatalogueDescriptor(
            activity,
            callableContract: new CallableContract(
                new CallableSignature(parameters, new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                CallableEffect.Read,
                CallableIdempotency.Idempotent,
                CallableRetrySafety.Safe));
        var catalogue = new InMemoryTrustedCatalogue([entry]);

        parameters[0] = new CallableParameter("changed", new PrimitiveType(FuwenPrimitiveKind.Json));
        var resolved = await catalogue.ResolveAsync(activity, TestContext.Current.CancellationToken);

        resolved.Descriptor!.CallableContract!.Signature.Parameters
            .Select(static parameter => parameter.Name)
            .Should().Equal("request", "zeta");
        resolved.Descriptor.CallableContract.Signature.Parameters
            .Should().NotBeSameAs(parameters);

        var schema = Descriptor(DescriptorKind.Schema, "sample.schema", "1", 'a');
        var wrongKind = () => new TrustedCatalogueDescriptor(
            schema,
            callableContract: entry.CallableContract);
        var duplicate = () => new TrustedCatalogueDescriptor(
            activity,
            callableContract: new CallableContract(
                new CallableSignature(
                    [
                        new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String)),
                        new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String)),
                    ],
                    new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                CallableEffect.Read,
                CallableIdempotency.Idempotent,
                CallableRetrySafety.Safe));
        var undefined = () => new TrustedCatalogueDescriptor(
            activity,
            callableContract: entry.CallableContract! with { Effect = (CallableEffect)999 });
        var oversized = () => new TrustedCatalogueDescriptor(
            activity,
            callableContract: new CallableContract(
                new CallableSignature(
                    Enumerable.Range(0, 257)
                        .Select(index => new CallableParameter($"p_{index}", new PrimitiveType(FuwenPrimitiveKind.String)))
                        .ToArray(),
                    new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                CallableEffect.Read,
                CallableIdempotency.Idempotent,
                CallableRetrySafety.Safe));

        wrongKind.Should().Throw<ArgumentException>();
        duplicate.Should().Throw<ArgumentException>().WithMessage("*duplicated*");
        undefined.Should().Throw<ArgumentOutOfRangeException>();
        oversized.Should().Throw<ArgumentException>().WithMessage("*256*");
    }

    [Fact]
    public void CatalogueSnapshotRevision_IsOrderIndependentAndCoversTrustedMetadata()
    {
        var firstDescriptor = Descriptor(DescriptorKind.Activity, "sample.first", "1", 'a');
        var secondDescriptor = Descriptor(DescriptorKind.Activity, "sample.second", "1", 'b');
        var first = new TrustedCatalogueDescriptor(
            firstDescriptor,
            requiredCapabilities: [new CapabilityRequirement("read")],
            callableContract: Callable(CallableEffect.Read));
        var second = new TrustedCatalogueDescriptor(
            secondDescriptor,
            callableContract: Callable(CallableEffect.Read));

        var ordered = new InMemoryTrustedCatalogue([first, second]);
        var reversed = new InMemoryTrustedCatalogue([second, first]);
        var metadataChanged = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(
                firstDescriptor,
                requiredCapabilities: [new CapabilityRequirement("read")],
                callableContract: Callable(CallableEffect.Write)),
            second,
        ]);

        ordered.SnapshotRevision.Should().StartWith("sha256:fuwen-catalogue-snapshot/v1:");
        reversed.SnapshotRevision.Should().Be(ordered.SnapshotRevision);
        metadataChanged.SnapshotRevision.Should().NotBe(ordered.SnapshotRevision);
    }

    [Fact]
    public async Task Resolver_SortsBatchRequestsAndEnforcesLookupBudget()
    {
        var first = Descriptor(DescriptorKind.Activity, "z.activity", "1", 'a');
        var second = Descriptor(DescriptorKind.Schema, "a.schema", "1", 'a');
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(first),
            new TrustedCatalogueDescriptor(second),
        ]);
        var resolver = new TrustedCatalogueResolver(
            catalogue,
            new CompilationBudget(maxCatalogueLookups: 1));

        var batch = await resolver.ResolveManyAsync([first, second], TestContext.Current.CancellationToken);

        batch.Results.Select(static result => result.Requested.Name)
            .Should().Equal("a.schema", "z.activity");
        batch.Results[0].Status.Should().Be(DescriptorResolutionStatus.Resolved);
        batch.Results[1].Status.Should().Be(DescriptorResolutionStatus.BudgetExceeded);
        batch.Results[1].Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.BudgetCatalogueLookupsExceeded);
        batch.Usage.CatalogueLookups.Should().Be(1);
        batch.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_AccountsAggregateTrustedMetadataAgainstExistingBudgets()
    {
        var first = Descriptor(DescriptorKind.Activity, "a.callable", "1", 'a');
        var second = Descriptor(DescriptorKind.Activity, "b.callable", "1", 'b');
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(first, callableContract: CallableWithParameter()),
            new TrustedCatalogueDescriptor(second, callableContract: CallableWithParameter()),
        ]);
        var resolver = new TrustedCatalogueResolver(
            catalogue,
            new CompilationBudget(maxAstNodes: 5, maxStringBytes: 1_000_000));

        var batch = await resolver.ResolveManyAsync([second, first], TestContext.Current.CancellationToken);

        batch.Results.Select(static result => result.Requested.Name)
            .Should().Equal("a.callable", "b.callable");
        batch.Results[0].Status.Should().Be(DescriptorResolutionStatus.Resolved);
        batch.Results[1].Status.Should().Be(DescriptorResolutionStatus.BudgetExceeded);
        batch.Results[1].Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().ContainSingle().Which.Should().Be(CompilerDiagnosticCodes.BudgetAstNodesExceeded);
        batch.Usage.AstNodes.Should().Be(3);
    }

    [Fact]
    public async Task Resolver_AccountsAggregateTrustedMetadataBytesAcrossDescriptors()
    {
        var first = Descriptor(DescriptorKind.Activity, "a." + new string('x', 160), "1", 'a');
        var second = Descriptor(DescriptorKind.Activity, "b." + new string('x', 160), "1", 'b');
        var catalogue = new InMemoryTrustedCatalogue(
        [
            new TrustedCatalogueDescriptor(first, callableContract: CallableWithParameter()),
            new TrustedCatalogueDescriptor(second, callableContract: CallableWithParameter()),
        ]);
        var individualResolver = new TrustedCatalogueResolver(catalogue);
        var firstUsage = await individualResolver.ResolveAsync(first, TestContext.Current.CancellationToken);
        var secondUsage = await individualResolver.ResolveAsync(second, TestContext.Current.CancellationToken);
        var metadataBytesForBoth = checked(firstUsage.Usage.StringBytes + secondUsage.Usage.StringBytes);
        var resolver = new TrustedCatalogueResolver(
            catalogue,
            new CompilationBudget(maxStringBytes: metadataBytesForBoth - 1, maxAstNodes: 100));

        var batch = await resolver.ResolveManyAsync([first, second], TestContext.Current.CancellationToken);

        firstUsage.Succeeded.Should().BeTrue();
        secondUsage.Succeeded.Should().BeTrue();
        batch.Results[0].Succeeded.Should().BeTrue();
        batch.Results[1].Status.Should().Be(DescriptorResolutionStatus.BudgetExceeded);
        batch.Results[1].Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().ContainSingle().Which.Should().Be(CompilerDiagnosticCodes.BudgetStringBytesExceeded);
    }

    [Fact]
    public async Task Resolver_ObservesCancellationBeforeCallingCatalogue()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.activity", "1", 'a');
        var catalogue = new CountingCatalogue();
        var resolver = new TrustedCatalogueResolver(catalogue);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () => await resolver.ResolveAsync(descriptor, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        catalogue.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Resolver_NormalizesRecoverableProviderFault()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.fault", "1", 'a');
        var resolver = new TrustedCatalogueResolver(new FaultingCatalogue(
            new InvalidOperationException("provider fault")));

        var result = await resolver.ResolveAsync(descriptor, TestContext.Current.CancellationToken);

        result.Status.Should().Be(DescriptorResolutionStatus.InvalidRequest);
        result.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.CatalogueResolutionInvalidResult);
    }

    [Fact]
    public async Task Resolver_NormalizesProviderOwnedCancellation()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.cancelled", "1", 'a');
        var resolver = new TrustedCatalogueResolver(new FaultingCatalogue(
            new OperationCanceledException("provider-owned cancellation")));

        var result = await resolver.ResolveAsync(descriptor, TestContext.Current.CancellationToken);

        result.Status.Should().Be(DescriptorResolutionStatus.InvalidRequest);
        result.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.CatalogueResolutionInvalidResult);
    }

    [Fact]
    public async Task Resolver_PreservesCallerCancellationWhileProviderIsPending()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.pending", "1", 'a');
        var catalogue = new BlockingCatalogue();
        var resolver = new TrustedCatalogueResolver(catalogue);
        using var cancellation = new CancellationTokenSource();
        var pending = resolver.ResolveAsync(descriptor, cancellation.Token).AsTask();

        await catalogue.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await catalogue.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        catalogue.Release();
    }

    [Fact]
    public void Catalogue_RejectsAnUnboundedDescriptorSequence()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.activity", "1", 'a');

        IEnumerable<TrustedCatalogueDescriptor> Infinite()
        {
            while (true)
                yield return new TrustedCatalogueDescriptor(descriptor);
        }

        var act = () => new InMemoryTrustedCatalogue(Infinite());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*hard ceiling*");
    }

    [Fact]
    public void ResolutionResult_EnforcesExactIdentityForEachStatus()
    {
        var requested = Descriptor(DescriptorKind.Activity, "sample.activity", "1", 'a');
        var differentDigest = new TrustedCatalogueDescriptor(Descriptor(DescriptorKind.Activity, requested.Name, requested.Version, 'b'));
        var differentKind = new TrustedCatalogueDescriptor(Descriptor(DescriptorKind.Schema, requested.Name, requested.Version, 'b'));
        var sameDigest = new TrustedCatalogueDescriptor(requested);

        var resolved = () => new DescriptorResolutionResult(requested, DescriptorResolutionStatus.Resolved, differentDigest);
        var mismatchKind = () => new DescriptorResolutionResult(requested, DescriptorResolutionStatus.DigestMismatch, differentKind);
        var mismatchDigest = () => new DescriptorResolutionResult(requested, DescriptorResolutionStatus.DigestMismatch, sameDigest);
        var unexpectedDescriptor = () => new DescriptorResolutionResult(requested, DescriptorResolutionStatus.NotFound, differentDigest);

        resolved.Should().Throw<ArgumentException>();
        mismatchKind.Should().Throw<ArgumentException>();
        mismatchDigest.Should().Throw<ArgumentException>();
        unexpectedDescriptor.Should().Throw<ArgumentException>();
        new DescriptorResolutionResult(requested, DescriptorResolutionStatus.DigestMismatch, differentDigest)
            .Succeeded.Should().BeFalse();
    }

    [Fact]
    public void DescriptorSnapshot_ValidatesNestedNominalKindsAndSortsCapabilities()
    {
        var schema = Descriptor(DescriptorKind.Schema, "sample.schema", "1", 'a');
        var activity = Descriptor(DescriptorKind.Activity, "sample.activity", "1", 'a');
        var artifact = Descriptor(DescriptorKind.Artifact, "sample.artifact", "1", 'a');
        var wrongSchemaReference = () => new TrustedCatalogueDescriptor(
            schema,
            new ObjectSchemaDefinition(
                schema,
                [new SchemaField("value", new NamedTypeReference(activity))]));
        var wrongArtifactReference = () => new TrustedCatalogueDescriptor(
            schema,
            new ObjectSchemaDefinition(
                schema,
                [new SchemaField("value", new ArtifactType(schema))]));

        wrongSchemaReference.Should().Throw<ArgumentException>();
        wrongArtifactReference.Should().Throw<ArgumentException>();

        var entry = new TrustedCatalogueDescriptor(
            schema,
            requiredCapabilities:
            [
                new CapabilityRequirement("zeta"),
                new CapabilityRequirement("alpha", "tenant"),
                new CapabilityRequirement("alpha"),
            ]);

        entry.RequiredCapabilities.Select(static capability => (capability.Name, capability.ScopeClass))
            .Should().Equal(
                ("alpha", null),
                ("alpha", "tenant"),
                ("zeta", null));
    }

    [Fact]
    public void Catalogue_RejectsAggregateSchemaNodeAmplification()
    {
        var descriptor = Descriptor(DescriptorKind.Schema, "sample.large", "1", 'a');
        var fields = Enumerable.Range(0, 4_096)
            .Select(index => new SchemaField(
                $"field_{index}",
                new ListType(new ListType(new PrimitiveType(FuwenPrimitiveKind.String), 1), 1)))
            .ToArray();

        var act = () => new TrustedCatalogueDescriptor(
            descriptor,
            new ObjectSchemaDefinition(descriptor, fields));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*schema-node*");
    }

    [Fact]
    public async Task Resolver_EnforcesHardTimeoutAndSignalsNonCooperativeCatalogue()
    {
        var descriptor = Descriptor(DescriptorKind.Activity, "sample.blocking", "1", 'a');
        var catalogue = new BlockingCatalogue();
        var resolver = new TrustedCatalogueResolver(
            catalogue,
            new CompilationBudget(maxCatalogueLookupMilliseconds: 25));
        var stopwatch = Stopwatch.StartNew();

        var result = await resolver.ResolveAsync(descriptor, TestContext.Current.CancellationToken);

        stopwatch.Stop();
        result.Status.Should().Be(DescriptorResolutionStatus.BudgetExceeded);
        result.Diagnostics.Select(static diagnostic => diagnostic.Code)
            .Should().Equal(CompilerDiagnosticCodes.BudgetCatalogueLookupMillisecondsExceeded);
        result.Usage.CatalogueLookupMilliseconds.Should().BeGreaterThanOrEqualTo(25);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        await catalogue.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        catalogue.Release();
    }

    [Fact]
    public async Task Resolver_UsesTimeDiagnosticForRequestsAfterTimeout()
    {
        var first = Descriptor(DescriptorKind.Activity, "a.blocking", "1", 'a');
        var second = Descriptor(DescriptorKind.Activity, "b.blocking", "1", 'a');
        var catalogue = new BlockingCatalogue();
        var resolver = new TrustedCatalogueResolver(
            catalogue,
            new CompilationBudget(maxCatalogueLookupMilliseconds: 25));

        var result = await resolver.ResolveManyAsync([second, first], TestContext.Current.CancellationToken);

        result.Results.Should().HaveCount(2);
        result.Results.SelectMany(static item => item.Diagnostics)
            .Select(static diagnostic => diagnostic.Code)
            .Should().OnlyContain(code => code == CompilerDiagnosticCodes.BudgetCatalogueLookupMillisecondsExceeded);
        await catalogue.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        catalogue.Release();
    }

    private static DescriptorReference Descriptor(
        DescriptorKind kind,
        string name,
        string version,
        char digest) => new(
            kind,
            name,
            version,
            new ContentDigest("sha256", "descriptor/v1", new string(digest, 64)));

    private static CallableContract Callable(CallableEffect effect) => new(
        new CallableSignature([], new PrimitiveType(FuwenPrimitiveKind.Boolean)),
        effect,
        CallableIdempotency.Idempotent,
        CallableRetrySafety.Safe);

    private static CallableContract CallableWithParameter() => new(
        new CallableSignature(
            [new CallableParameter("request", new PrimitiveType(FuwenPrimitiveKind.String))],
            new PrimitiveType(FuwenPrimitiveKind.Boolean)),
        CallableEffect.Read,
        CallableIdempotency.Idempotent,
        CallableRetrySafety.Safe);

    private sealed class CountingCatalogue : ITrustedCatalogue
    {
        public int Calls { get; private set; }

        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.NotFound));
        }
    }

    private sealed class FaultingCatalogue(Exception exception) : ITrustedCatalogue
    {
        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DescriptorResolutionResult>(exception);
    }

    private sealed class BlockingCatalogue : ITrustedCatalogue
    {
        private readonly TaskCompletionSource<DescriptorResolutionResult> pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DescriptorResolutionResult> ResolveAsync(
            DescriptorReference descriptor,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), Cancelled);
            return new ValueTask<DescriptorResolutionResult>(pending.Task);
        }

        public void Release() => pending.TrySetResult(
            new DescriptorResolutionResult(
                Descriptor(DescriptorKind.Activity, "sample.blocking", "1", 'a'),
                DescriptorResolutionStatus.NotFound));
    }
}
