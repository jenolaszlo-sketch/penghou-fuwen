#pragma warning disable CS1591
using System.Diagnostics;

using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>How a requested descriptor was resolved by a trusted catalogue.</summary>
public enum DescriptorResolutionStatus
{
    Resolved = 0,
    NotFound = 1,
    DigestMismatch = 2,
    InvalidRequest = 3,
    BudgetExceeded = 4,
}

/// <summary>An immutable, bounded descriptor entry exposed by a trusted catalogue.</summary>
public sealed class TrustedCatalogueDescriptor
{
    private readonly DescriptorReference descriptor;
    private readonly ResolvedSchemaDefinition? schemaDefinition;
    private readonly IReadOnlyList<CapabilityRequirement> requiredCapabilities;

    public TrustedCatalogueDescriptor(
        DescriptorReference descriptor,
        ResolvedSchemaDefinition? schemaDefinition = null,
        IEnumerable<CapabilityRequirement>? requiredCapabilities = null)
    {
        this.descriptor = CatalogueContractValidation.SnapshotDescriptor(descriptor);
        this.schemaDefinition = schemaDefinition is null
            ? null
            : CatalogueContractValidation.SnapshotSchema(schemaDefinition, this.descriptor);
        requiredCapabilities ??= Array.Empty<CapabilityRequirement>();
        this.requiredCapabilities = CatalogueContractValidation.SnapshotCapabilities(requiredCapabilities);
        CatalogueContractValidation.ValidateAggregateSchemaNodes([this]);
    }

    public DescriptorReference Descriptor => descriptor;

    public ResolvedSchemaDefinition? SchemaDefinition => schemaDefinition;

    public IReadOnlyList<CapabilityRequirement> RequiredCapabilities => requiredCapabilities;
}

/// <summary>A bounded, immutable result for one exact descriptor lookup.</summary>
public sealed class DescriptorResolutionResult
{
    private readonly DescriptorReference requested;
    private readonly TrustedCatalogueDescriptor? descriptor;
    private readonly DiagnosticCollection diagnostics;

    public DescriptorResolutionResult(
        DescriptorReference requested,
        DescriptorResolutionStatus status,
        TrustedCatalogueDescriptor? descriptor = null,
        IEnumerable<CompilerDiagnostic>? diagnostics = null,
        CompilationUsageSummary? usage = null,
        int maximumDiagnostics = CompilationBudget.DefaultMaximumDiagnostics)
    {
        this.requested = CatalogueContractValidation.SnapshotDescriptor(requested);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        if (status == DescriptorResolutionStatus.Resolved &&
            (descriptor is null || !descriptor.Descriptor.Equals(this.requested)))
        {
            throw new ArgumentException(
                "A resolved result must carry a descriptor that exactly matches the requested identity.",
                nameof(descriptor));
        }

        if (status == DescriptorResolutionStatus.DigestMismatch)
        {
            if (descriptor is null ||
                !DescriptorKey.From(descriptor.Descriptor).Equals(DescriptorKey.From(this.requested)) ||
                CatalogueContractValidation.DigestsEqual(descriptor.Descriptor.ContentDigest, this.requested.ContentDigest))
            {
                throw new ArgumentException(
                    "A digest mismatch must carry a descriptor with the requested kind, name, and version and a different digest.",
                    nameof(descriptor));
            }
        }

        if (status is not DescriptorResolutionStatus.Resolved and not DescriptorResolutionStatus.DigestMismatch && descriptor is not null)
            throw new ArgumentException("Only a resolved result or digest mismatch may carry a catalogue descriptor.", nameof(descriptor));

        Status = status;
        this.descriptor = descriptor is null ? null : new TrustedCatalogueDescriptor(
            descriptor.Descriptor,
            descriptor.SchemaDefinition,
            descriptor.RequiredCapabilities);
        this.diagnostics = new DiagnosticCollection(
            diagnostics ?? Array.Empty<CompilerDiagnostic>(),
            maximumDiagnostics);
        Usage = usage ?? new CompilationUsageSummary();
    }

    public DescriptorReference Requested => requested;

    public TrustedCatalogueDescriptor? Descriptor => descriptor;

    public DescriptorReference? Resolved => descriptor?.Descriptor;

    public DescriptorResolutionStatus Status { get; }

    public DiagnosticCollection Diagnostics => diagnostics;

    public CompilationUsageSummary Usage { get; }

    public bool Succeeded => Status == DescriptorResolutionStatus.Resolved && Diagnostics.All(static item => item.Severity != DiagnosticSeverity.Error);

    public bool IsResolved => Succeeded;
}

/// <summary>A bounded, immutable result for a set of exact descriptor lookups.</summary>
public sealed class DescriptorResolutionBatchResult
{
    private readonly IReadOnlyList<DescriptorResolutionResult> results;
    private readonly DiagnosticCollection diagnostics;

    internal DescriptorResolutionBatchResult(
        IEnumerable<DescriptorResolutionResult> results,
        CompilationUsageSummary usage,
        int maximumDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(results);
        var snapshot = CatalogueContractValidation.ReadBoundedResults(results);
        this.results = Array.AsReadOnly(snapshot.ToArray());
        diagnostics = new DiagnosticCollection(
            snapshot.SelectMany(static result => result.Diagnostics),
            maximumDiagnostics);
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
    }

    public IReadOnlyList<DescriptorResolutionResult> Results => results;

    public DiagnosticCollection Diagnostics => diagnostics;

    public CompilationUsageSummary Usage { get; }

    public bool Succeeded => Results.All(static result => result.Succeeded);
}

/// <summary>Resolves trusted descriptors by exact kind, name, version, and digest.</summary>
public interface ITrustedCatalogue
{
    ValueTask<DescriptorResolutionResult> ResolveAsync(
        DescriptorReference descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>A deterministic in-memory trusted catalogue useful for hosts and tests.</summary>
public sealed class InMemoryTrustedCatalogue : ITrustedCatalogue
{
    private readonly IReadOnlyDictionary<DescriptorKey, TrustedCatalogueDescriptor> entries;
    private readonly IReadOnlyList<TrustedCatalogueDescriptor> orderedEntries;

    public InMemoryTrustedCatalogue(IEnumerable<TrustedCatalogueDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var snapshot = CatalogueContractValidation.ReadBoundedDescriptors(descriptors)
            .Select(static descriptor => new TrustedCatalogueDescriptor(
                descriptor.Descriptor,
                descriptor.SchemaDefinition,
                descriptor.RequiredCapabilities))
            .OrderBy(static descriptor => descriptor.Descriptor, DescriptorReferenceComparer.Instance)
            .ToArray();
        CatalogueContractValidation.ValidateAggregateSchemaNodes(snapshot);

        var dictionary = new Dictionary<DescriptorKey, TrustedCatalogueDescriptor>();
        foreach (var descriptor in snapshot)
        {
            var key = DescriptorKey.From(descriptor.Descriptor);
            if (!dictionary.TryAdd(key, descriptor))
            {
                throw new ArgumentException(
                    $"Trusted catalogue contains duplicate descriptor '{CatalogueContractValidation.Display(key)}'.",
                    nameof(descriptors));
            }
        }

        entries = new Dictionary<DescriptorKey, TrustedCatalogueDescriptor>(dictionary);
        orderedEntries = Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<TrustedCatalogueDescriptor> Descriptors =>
        Array.AsReadOnly(orderedEntries.Select(static descriptor => new TrustedCatalogueDescriptor(
            descriptor.Descriptor,
            descriptor.SchemaDefinition,
            descriptor.RequiredCapabilities)).ToArray());

    public bool TryGet(
        DescriptorReference descriptor,
        out TrustedCatalogueDescriptor? result)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        CatalogueContractValidation.ValidateDescriptor(descriptor);
        if (entries.TryGetValue(DescriptorKey.From(descriptor), out var found))
        {
            if (!CatalogueContractValidation.DigestsEqual(descriptor.ContentDigest, found.Descriptor.ContentDigest))
            {
                result = null;
                return false;
            }

            result = new TrustedCatalogueDescriptor(
                found.Descriptor,
                found.SchemaDefinition,
                found.RequiredCapabilities);
            return true;
        }

        result = null;
        return false;
    }

    public ValueTask<DescriptorResolutionResult> ResolveAsync(
        DescriptorReference descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        CatalogueContractValidation.ValidateDescriptor(descriptor);

        if (!entries.TryGetValue(DescriptorKey.From(descriptor), out var found))
        {
            return ValueTask.FromResult(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.NotFound,
                diagnostics: [CatalogueDiagnostics.NotFound(descriptor)]));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!CatalogueContractValidation.DigestsEqual(descriptor.ContentDigest, found.Descriptor.ContentDigest))
        {
            return ValueTask.FromResult(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.DigestMismatch,
                found,
                [CatalogueDiagnostics.DigestMismatch(descriptor, found.Descriptor)]));
        }

        return ValueTask.FromResult(new DescriptorResolutionResult(
            descriptor,
            DescriptorResolutionStatus.Resolved,
            found));
    }
}

/// <summary>Resolves exact descriptors with compiler lookup and time budgets.</summary>
public class TrustedCatalogueResolver
{
    private readonly ITrustedCatalogue catalogue;
    private readonly CompilationBudget budget;

    public TrustedCatalogueResolver(
        ITrustedCatalogue catalogue,
        CompilationBudget? budget = null)
    {
        this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        this.budget = budget ?? CompilationBudget.Default;
    }

    public ValueTask<DescriptorResolutionResult> ResolveAsync(
        DescriptorReference descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        CatalogueContractValidation.ValidateDescriptor(descriptor);
        return ResolveCoreAsync(descriptor, cancellationToken);
    }

    public async ValueTask<DescriptorResolutionBatchResult> ResolveManyAsync(
        IEnumerable<DescriptorReference> descriptors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        cancellationToken.ThrowIfCancellationRequested();
        var requests = CatalogueContractValidation.ReadBoundedDescriptorReferences(descriptors, cancellationToken)
            .Select(static descriptor => CatalogueContractValidation.SnapshotDescriptor(descriptor))
            .OrderBy(static descriptor => descriptor, DescriptorReferenceComparer.Instance)
            .ToArray();

        var results = new List<DescriptorResolutionResult>(requests.Length);
        var tracker = new CompilationBudgetTracker(budget);
        var timeoutStopwatchTicks = StopwatchMillisecondsToTicksCeiling(budget.MaxCatalogueLookupMilliseconds);
        var deadlineTicks = CreateDeadline(Stopwatch.GetTimestamp(), budget.MaxCatalogueLookupMilliseconds);
        long elapsedTotalStopwatchTicks = 0;
        var timeBudgetExceeded = false;
        foreach (var descriptor in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeBudgetExceeded)
            {
                results.Add(new DescriptorResolutionResult(
                    descriptor,
                    DescriptorResolutionStatus.BudgetExceeded,
                    diagnostics: [CatalogueDiagnostics.LookupTimeBudgetExceeded(budget.MaxCatalogueLookupMilliseconds)]));
                continue;
            }

            if (!tracker.TryConsume(CompilationBudgetDimension.CatalogueLookups))
            {
                results.Add(new DescriptorResolutionResult(
                    descriptor,
                    DescriptorResolutionStatus.BudgetExceeded,
                    diagnostics: [CatalogueDiagnostics.LookupBudgetExceeded(budget.MaxCatalogueLookups)]));
                continue;
            }

            var outcome = await ResolveCatalogueAsync(
                descriptor,
                cancellationToken,
                deadlineTicks,
                timeoutStopwatchTicks).ConfigureAwait(false);
            elapsedTotalStopwatchTicks = checked(elapsedTotalStopwatchTicks + outcome.ElapsedStopwatchTicks);
            if (outcome.TimedOut)
            {
                timeBudgetExceeded = true;
                results.Add(new DescriptorResolutionResult(
                    descriptor,
                    DescriptorResolutionStatus.BudgetExceeded,
                    diagnostics: [CatalogueDiagnostics.LookupTimeBudgetExceeded(budget.MaxCatalogueLookupMilliseconds)],
                    usage: Usage(tracker, elapsedTotalStopwatchTicks)));
                continue;
            }

            tracker.TryConsume(
                CompilationBudgetDimension.CatalogueLookupMilliseconds,
                StopwatchTicksToMillisecondsCeiling(outcome.ElapsedStopwatchTicks));
            var result = outcome.Result!;

            results.Add(new DescriptorResolutionResult(
                result.Requested,
                result.Status,
                result.Descriptor,
                result.Diagnostics,
                Usage(tracker, elapsedTotalStopwatchTicks)));
        }

        return new DescriptorResolutionBatchResult(results, Usage(tracker, elapsedTotalStopwatchTicks), budget.MaxDiagnostics);
    }

    private async ValueTask<DescriptorResolutionResult> ResolveCoreAsync(
        DescriptorReference descriptor,
        CancellationToken cancellationToken)
    {
        var tracker = new CompilationBudgetTracker(budget);
        if (!tracker.TryConsume(CompilationBudgetDimension.CatalogueLookups))
        {
            return new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.BudgetExceeded,
                diagnostics: [CatalogueDiagnostics.LookupBudgetExceeded(budget.MaxCatalogueLookups)],
                usage: tracker.Snapshot());
        }

        var outcome = await ResolveCatalogueAsync(
            descriptor,
            cancellationToken,
            CreateDeadline(Stopwatch.GetTimestamp(), budget.MaxCatalogueLookupMilliseconds),
            StopwatchMillisecondsToTicksCeiling(budget.MaxCatalogueLookupMilliseconds)).ConfigureAwait(false);
        if (outcome.TimedOut)
        {
            return new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.BudgetExceeded,
                diagnostics: [CatalogueDiagnostics.LookupTimeBudgetExceeded(budget.MaxCatalogueLookupMilliseconds)],
                usage: Usage(tracker, outcome.ElapsedStopwatchTicks));
        }

        tracker.TryConsume(
            CompilationBudgetDimension.CatalogueLookupMilliseconds,
            StopwatchTicksToMillisecondsCeiling(outcome.ElapsedStopwatchTicks));
        var result = outcome.Result!;

        return new DescriptorResolutionResult(
            result.Requested,
            result.Status,
            result.Descriptor,
            result.Diagnostics,
            Usage(tracker, outcome.ElapsedStopwatchTicks),
            budget.MaxDiagnostics);
    }

    private async ValueTask<LookupOutcome> ResolveCatalogueAsync(
        DescriptorReference descriptor,
        CancellationToken cancellationToken,
        long deadlineTicks,
        long timeoutStopwatchTicks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedTicks = Stopwatch.GetTimestamp();
        if (startedTicks >= deadlineTicks)
            return new LookupOutcome(null, true, timeoutStopwatchTicks);

        var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var providerTask = Task.Run(
            async () => await catalogue.ResolveAsync(descriptor, lookupCancellation.Token).ConfigureAwait(false),
            CancellationToken.None);

        var timeoutTask = DelayForStopwatchTicks(deadlineTicks - startedTicks);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(providerTask, timeoutTask, cancellationTask).ConfigureAwait(false);
        var elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startedTicks);

        if (cancellationToken.IsCancellationRequested)
        {
            SignalCancellation(lookupCancellation);
            ObserveProviderCompletion(providerTask, lookupCancellation);
            throw new OperationCanceledException(cancellationToken);
        }

        if (completed != providerTask || Stopwatch.GetTimestamp() >= deadlineTicks)
        {
            SignalCancellation(lookupCancellation);
            ObserveProviderCompletion(providerTask, lookupCancellation);
            return new LookupOutcome(null, true, Math.Max(elapsedTicks, timeoutStopwatchTicks));
        }

        DescriptorResolutionResult? result;
        try
        {
            result = await providerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProviderFailure(descriptor, elapsedTicks);
        }
        catch (Exception exception) when (IsRecoverableProviderException(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProviderFailure(descriptor, elapsedTicks);
        }
        finally
        {
            lookupCancellation.Dispose();
        }

        // The caller may cancel after the pre-await check but before the
        // provider task completes. Preserve that cancellation even when the
        // provider returned a successful result.
        cancellationToken.ThrowIfCancellationRequested();
        elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, Stopwatch.GetTimestamp() - startedTicks));
        if (Stopwatch.GetTimestamp() >= deadlineTicks)
            return new LookupOutcome(null, true, elapsedTicks);

        if (result is null || !result.Requested.Equals(descriptor))
        {
            return new LookupOutcome(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.InvalidRequest,
                diagnostics: [CatalogueDiagnostics.InvalidResult()]), false, elapsedTicks);
        }

        if (result.Status == DescriptorResolutionStatus.Resolved &&
            (result.Descriptor is null || !result.Descriptor.Descriptor.Equals(descriptor)))
        {
            return new LookupOutcome(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.InvalidRequest,
                diagnostics: [CatalogueDiagnostics.InvalidResult()]), false, elapsedTicks);
        }

        if (result.Status == DescriptorResolutionStatus.DigestMismatch &&
            (result.Descriptor is null ||
             !DescriptorKey.From(result.Descriptor.Descriptor).Equals(DescriptorKey.From(descriptor)) ||
             CatalogueContractValidation.DigestsEqual(result.Descriptor.Descriptor.ContentDigest, descriptor.ContentDigest)))
        {
            return new LookupOutcome(new DescriptorResolutionResult(
                descriptor,
                DescriptorResolutionStatus.InvalidRequest,
                diagnostics: [CatalogueDiagnostics.InvalidResult()]), false, elapsedTicks);
        }

        return new LookupOutcome(result, false, elapsedTicks);
    }

    private static LookupOutcome ProviderFailure(
        DescriptorReference descriptor,
        long elapsedTicks) => new(
        new DescriptorResolutionResult(
            descriptor,
            DescriptorResolutionStatus.InvalidRequest,
            diagnostics: [CatalogueDiagnostics.InvalidResult()]),
        false,
        elapsedTicks);

    private static bool IsRecoverableProviderException(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static void ObserveProviderCompletion(
        Task<DescriptorResolutionResult> providerTask,
        CancellationTokenSource cancellation)
    {
        _ = providerTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void SignalCancellation(CancellationTokenSource cancellation)
    {
        var task = cancellation.CancelAsync();
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Task DelayForStopwatchTicks(long ticks)
    {
        var milliseconds = StopwatchTicksToMillisecondsCeiling(ticks);
        if (milliseconds <= 0)
            return Task.CompletedTask;
        return Task.Delay(TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue - 1L)));
    }

    private static long CreateDeadline(long startedTicks, long maximumMilliseconds)
    {
        var timeoutTicks = StopwatchMillisecondsToTicksCeiling(maximumMilliseconds);
        return long.MaxValue - startedTicks < timeoutTicks
            ? long.MaxValue
            : startedTicks + timeoutTicks;
    }

    private static long StopwatchMillisecondsToTicksCeiling(long milliseconds)
    {
        if (milliseconds <= 0)
            return 1;

        var seconds = milliseconds / 1_000;
        var remainderMilliseconds = milliseconds % 1_000;
        var remainderTicks = (remainderMilliseconds * (long)Stopwatch.Frequency + 999) / 1_000;
        return seconds > (long.MaxValue - remainderTicks) / Stopwatch.Frequency
            ? long.MaxValue
            : seconds * Stopwatch.Frequency + remainderTicks;
    }

    private static long StopwatchTicksToMillisecondsCeiling(long ticks)
    {
        if (ticks <= 0)
            return 0;
        var wholeSeconds = ticks / Stopwatch.Frequency;
        var remainderTicks = ticks % Stopwatch.Frequency;
        var remainderMilliseconds = (remainderTicks * 1_000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
        return wholeSeconds > (long.MaxValue - remainderMilliseconds) / 1_000
            ? long.MaxValue
            : wholeSeconds * 1_000 + remainderMilliseconds;
    }

    private CompilationUsageSummary Usage(CompilationBudgetTracker tracker, long elapsedStopwatchTicks)
    {
        var snapshot = tracker.Snapshot();
        var elapsedMilliseconds = StopwatchTicksToMillisecondsCeiling(elapsedStopwatchTicks);
        return new(
            sourceBytes: snapshot.SourceBytes,
            tokens: snapshot.Tokens,
            astNodes: snapshot.AstNodes,
            nestingDepth: snapshot.NestingDepth,
            workflowNodes: snapshot.WorkflowNodes,
            schemas: snapshot.Schemas,
            schemaDepth: snapshot.SchemaDepth,
            schemaFields: snapshot.SchemaFields,
            expressions: snapshot.Expressions,
            stringBytes: snapshot.StringBytes,
            diagnostics: snapshot.Diagnostics,
            catalogueLookups: snapshot.CatalogueLookups,
            catalogueLookupMilliseconds: elapsedMilliseconds,
            compilationMilliseconds: snapshot.CompilationMilliseconds);
    }

    private sealed record LookupOutcome(
        DescriptorResolutionResult? Result,
        bool TimedOut,
        long ElapsedStopwatchTicks);
}

internal static class CatalogueDiagnostics
{
    internal static CompilerDiagnostic NotFound(DescriptorReference descriptor) => new(
        CompilerDiagnosticCodes.CatalogueDescriptorNotFound,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Binding,
        $"Trusted catalogue descriptor '{CatalogueContractValidation.Display(descriptor)}' was not found.",
        expected: CatalogueContractValidation.Display(descriptor));

    internal static CompilerDiagnostic DigestMismatch(
        DescriptorReference requested,
        DescriptorReference actual) => new(
        CompilerDiagnosticCodes.CatalogueDescriptorDigestMismatch,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Binding,
        $"Trusted catalogue descriptor '{CatalogueContractValidation.Display(requested)}' has a different content digest.",
        expected: CatalogueContractValidation.DigestDisplay(requested.ContentDigest),
        actual: CatalogueContractValidation.DigestDisplay(actual.ContentDigest));

    internal static CompilerDiagnostic LookupBudgetExceeded(int maximum) => new(
        CompilerDiagnosticCodes.BudgetCatalogueLookupsExceeded,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Budget,
        "The trusted catalogue lookup budget was exceeded.",
        expected: maximum.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static CompilerDiagnostic LookupTimeBudgetExceeded(long maximumMilliseconds) => new(
        CompilerDiagnosticCodes.BudgetCatalogueLookupMillisecondsExceeded,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Budget,
        "The trusted catalogue lookup time budget was exceeded.",
        expected: maximumMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static CompilerDiagnostic InvalidResult() => new(
        CompilerDiagnosticCodes.CatalogueResolutionInvalidResult,
        DiagnosticSeverity.Error,
        DiagnosticPhase.Binding,
        "The trusted catalogue returned an invalid descriptor resolution result.");
}

internal readonly record struct DescriptorKey(
    DescriptorKind Kind,
    string Name,
    string Version)
{
    internal static DescriptorKey From(DescriptorReference descriptor) =>
        new(descriptor.Kind, descriptor.Name, descriptor.Version);
}

internal sealed class DescriptorReferenceComparer : IComparer<DescriptorReference>
{
    internal static DescriptorReferenceComparer Instance { get; } = new();

    public int Compare(DescriptorReference? x, DescriptorReference? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var result = x.Kind.CompareTo(y.Kind);
        if (result != 0)
            return result;
        result = StringComparer.Ordinal.Compare(x.Name, y.Name);
        if (result != 0)
            return result;
        result = StringComparer.Ordinal.Compare(x.Version, y.Version);
        if (result != 0)
            return result;
        result = StringComparer.Ordinal.Compare(x.ContentDigest.Algorithm, y.ContentDigest.Algorithm);
        if (result != 0)
            return result;
        result = StringComparer.Ordinal.Compare(x.ContentDigest.Contract, y.ContentDigest.Contract);
        if (result != 0)
            return result;
        return StringComparer.Ordinal.Compare(x.ContentDigest.Value, y.ContentDigest.Value);
    }
}

internal static class CatalogueContractValidation
{
    internal const int MaximumDescriptors = 4_096;
    internal const int MaximumCapabilities = 256;
    internal const int MaximumDescriptorNameLength = 256;
    internal const int MaximumDescriptorVersionLength = 128;
    internal const int MaximumDigestAlgorithmLength = 32;
    internal const int MaximumDigestContractLength = 64;
    internal const int MaximumDigestValueLength = 512;
    internal const int MaximumSchemaDepth = 64;
    internal const int MaximumTotalSchemaNodes = 16_384;

    internal static DescriptorReference SnapshotDescriptor(
        DescriptorReference descriptor,
        DescriptorKind? expectedKind = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);
        if (expectedKind is not null && descriptor.Kind != expectedKind.Value)
            throw new ArgumentException(
                $"Descriptor '{Display(descriptor)}' must be of kind '{expectedKind.Value}'.",
                nameof(descriptor));
        return new DescriptorReference(
            descriptor.Kind,
            descriptor.Name,
            descriptor.Version,
            new ContentDigest(
                descriptor.ContentDigest.Algorithm,
                descriptor.ContentDigest.Contract,
                descriptor.ContentDigest.Value));
    }

    internal static void ValidateDescriptor(DescriptorReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Descriptor kind is not supported.");
        Text(descriptor.Name, nameof(descriptor.Name), MaximumDescriptorNameLength);
        Text(descriptor.Version, nameof(descriptor.Version), MaximumDescriptorVersionLength);
        ArgumentNullException.ThrowIfNull(descriptor.ContentDigest);
        Text(descriptor.ContentDigest.Algorithm, nameof(descriptor.ContentDigest.Algorithm), MaximumDigestAlgorithmLength);
        Text(descriptor.ContentDigest.Contract, nameof(descriptor.ContentDigest.Contract), MaximumDigestContractLength);
        Text(descriptor.ContentDigest.Value, nameof(descriptor.ContentDigest.Value), MaximumDigestValueLength);
    }

    internal static bool DigestsEqual(ContentDigest left, ContentDigest right) =>
        left.Algorithm == right.Algorithm &&
        left.Contract == right.Contract &&
        left.Value == right.Value;

    internal static TrustedCatalogueDescriptor[] ReadBoundedDescriptors(IEnumerable<TrustedCatalogueDescriptor> values)
    {
        var result = new List<TrustedCatalogueDescriptor>(Math.Min(MaximumDescriptors, 32));
        using var enumerator = values.GetEnumerator();
        for (var index = 0; index < MaximumDescriptors; index++)
        {
            if (!enumerator.MoveNext())
                return result.ToArray();
            result.Add(enumerator.Current ?? throw new ArgumentException("A trusted catalogue descriptor cannot be null.", nameof(values)));
        }

        if (enumerator.MoveNext())
            throw new ArgumentException($"Trusted catalogue input exceeds the hard ceiling of {MaximumDescriptors} descriptors.", nameof(values));
        return result.ToArray();
    }

    internal static DescriptorReference[] ReadBoundedDescriptorReferences(
        IEnumerable<DescriptorReference> values,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DescriptorReference>(Math.Min(MaximumDescriptors, 32));
        using var enumerator = values.GetEnumerator();
        for (var index = 0; index < MaximumDescriptors; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
                return result.ToArray();
            var value = enumerator.Current ?? throw new ArgumentException("A descriptor reference cannot be null.", nameof(values));
            ValidateDescriptor(value);
            result.Add(value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (enumerator.MoveNext())
            throw new ArgumentException($"Descriptor resolution input exceeds the hard ceiling of {MaximumDescriptors} descriptors.", nameof(values));
        return result.ToArray();
    }

    internal static DescriptorResolutionResult[] ReadBoundedResults(IEnumerable<DescriptorResolutionResult> values)
    {
        var result = new List<DescriptorResolutionResult>(Math.Min(MaximumDescriptors, 32));
        using var enumerator = values.GetEnumerator();
        for (var index = 0; index < MaximumDescriptors; index++)
        {
            if (!enumerator.MoveNext())
                return result.ToArray();
            result.Add(enumerator.Current ?? throw new ArgumentException("A descriptor resolution result cannot be null.", nameof(values)));
        }

        if (enumerator.MoveNext())
            throw new ArgumentException($"Descriptor resolution results exceed the hard ceiling of {MaximumDescriptors} items.", nameof(values));
        return result.ToArray();
    }

    internal static IReadOnlyList<CapabilityRequirement> SnapshotCapabilities(IEnumerable<CapabilityRequirement> values)
    {
        var result = new List<CapabilityRequirement>(Math.Min(MaximumCapabilities, 16));
        using var enumerator = values.GetEnumerator();
        for (var index = 0; index < MaximumCapabilities; index++)
        {
            if (!enumerator.MoveNext())
                return SortCapabilities(result);
            var value = enumerator.Current ?? throw new ArgumentException("A capability requirement cannot be null.", nameof(values));
            Text(value.Name, nameof(value.Name), 256);
            if (value.ScopeClass is not null)
                Text(value.ScopeClass, nameof(value.ScopeClass), 256);
            if (result.Any(existing => existing.Name == value.Name && existing.ScopeClass == value.ScopeClass))
                throw new ArgumentException($"Duplicate capability requirement '{value.Name}'.", nameof(values));
            result.Add(new CapabilityRequirement(value.Name, value.ScopeClass));
        }

        if (enumerator.MoveNext())
            throw new ArgumentException($"Capability input exceeds the hard ceiling of {MaximumCapabilities} items.", nameof(values));
        return SortCapabilities(result);
    }

    internal static ResolvedSchemaDefinition SnapshotSchema(
        ResolvedSchemaDefinition schema,
        DescriptorReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Descriptor is null || !schema.Descriptor.Equals(descriptor))
            throw new ArgumentException("Schema definition descriptor must exactly match the catalogue descriptor.", nameof(schema));
        if (descriptor.Kind != DescriptorKind.Schema)
            throw new ArgumentException("Only schema descriptors may carry a schema definition.", nameof(schema));

        var state = new SchemaSnapshotState();
        return CloneSchema(schema, state);
    }

    internal static void ValidateAggregateSchemaNodes(IEnumerable<TrustedCatalogueDescriptor> descriptors)
    {
        var total = 0;
        foreach (var descriptor in descriptors)
        {
            if (descriptor.SchemaDefinition is not null)
                CountSchemaNodes(descriptor.SchemaDefinition, ref total);
        }
    }

    internal static string Display(DescriptorReference descriptor) =>
        $"{descriptor.Kind}:{descriptor.Name}@{descriptor.Version}";

    internal static string Display(DescriptorKey descriptor) =>
        $"{descriptor.Kind}:{descriptor.Name}@{descriptor.Version}";

    internal static string DigestDisplay(ContentDigest digest) =>
        $"{digest.Algorithm}/{digest.Contract}:{digest.Value}";

    private static ResolvedSchemaDefinition CloneSchema(ResolvedSchemaDefinition schema, SchemaSnapshotState state)
    {
        state.Enter(schema);
        try
        {
            var descriptor = SnapshotDescriptor(schema.Descriptor);
            return schema switch
            {
                ObjectSchemaDefinition value => new ObjectSchemaDefinition(
                    descriptor,
                    CloneFields(value.Fields, state)),
                EnumSchemaDefinition value => new EnumSchemaDefinition(
                    descriptor,
                    CloneMembers(value.Members, state)),
                _ => throw new NotSupportedException($"Unsupported schema definition type '{schema.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(schema);
        }
    }

    private static IReadOnlyList<SchemaField> CloneFields(IReadOnlyList<SchemaField> fields, SchemaSnapshotState state)
    {
        var result = new List<SchemaField>(ReadCount(fields, "schema fields"));
        state.Enter(fields);
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index] ?? throw new ArgumentException("A schema field cannot be null.", nameof(fields));
                if (!names.Add(field.Name))
                    throw new ArgumentException($"Duplicate schema field '{field.Name}'.", nameof(fields));
                StructuralNodeIdentity.ValidateSegment(field.Name);
                result.Add(new SchemaField(field.Name, CloneType(field.Type, state)));
            }
            return Array.AsReadOnly(result.ToArray());
        }
        finally
        {
            state.Exit(fields);
        }
    }

    private static IReadOnlyList<EnumMember> CloneMembers(IReadOnlyList<EnumMember> members, SchemaSnapshotState state)
    {
        var result = new List<EnumMember>(ReadCount(members, "enum members"));
        state.Enter(members);
        try
        {
            if (members.Count == 0)
                throw new ArgumentException("Enum schema must declare at least one member.", nameof(members));
            var names = new HashSet<string>(StringComparer.Ordinal);
            var values = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index] ?? throw new ArgumentException("An enum member cannot be null.", nameof(members));
                StructuralNodeIdentity.ValidateSegment(member.Name);
                Text(member.Value, nameof(member.Value), 256);
                if (!names.Add(member.Name) || !values.Add(member.Value))
                    throw new ArgumentException("Enum members must have unique names and values.", nameof(members));
                result.Add(new EnumMember(member.Name, member.Value));
            }
            return Array.AsReadOnly(result.ToArray());
        }
        finally
        {
            state.Exit(members);
        }
    }

    private static FuwenType CloneType(FuwenType type, SchemaSnapshotState state)
    {
        ArgumentNullException.ThrowIfNull(type);
        state.Enter(type);
        try
        {
            return type switch
            {
                PrimitiveType value when Enum.IsDefined(value.Primitive) => new PrimitiveType(value.Primitive),
                PrimitiveType => throw new ArgumentOutOfRangeException(nameof(type), "Primitive type is not supported."),
                NamedTypeReference value => new NamedTypeReference(SnapshotDescriptor(value.Schema, DescriptorKind.Schema)),
                OptionalType value => new OptionalType(CloneType(value.ValueType, state)),
                ListType value when value.MaxItems > 0 => new ListType(CloneType(value.ItemType, state), value.MaxItems),
                ListType => throw new ArgumentOutOfRangeException(nameof(type), "List maximum must be positive."),
                ArtifactType value => new ArtifactType(SnapshotDescriptor(value.ArtifactDescriptor, DescriptorKind.Artifact)),
                _ => throw new NotSupportedException($"Unsupported Fuwen type '{type.GetType().Name}'."),
            };
        }
        finally
        {
            state.Exit(type);
        }
    }

    private static int ReadCount<T>(IReadOnlyList<T> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 0 || values.Count > MaximumDescriptors)
            throw new ArgumentException($"{name} exceed the bounded limit of {MaximumDescriptors} items.", nameof(values));
        return values.Count;
    }

    private static void Text(string value, string name, int maximumLength)
    {
        CompilerContractValidation.Text(value, name, maximumLength, required: true);
    }

    private static IReadOnlyList<CapabilityRequirement> SortCapabilities(List<CapabilityRequirement> values) =>
        Array.AsReadOnly(values
            .OrderBy(static capability => capability.Name, StringComparer.Ordinal)
            .ThenBy(static capability => capability.ScopeClass, StringComparer.Ordinal)
            .ToArray());

    private static void CountSchemaNodes(FuwenType type, ref int total)
    {
        CountNode(ref total);
        switch (type)
        {
            case OptionalType optional:
                CountSchemaNodes(optional.ValueType, ref total);
                break;
            case ListType list:
                CountSchemaNodes(list.ItemType, ref total);
                break;
        }
    }

    private static void CountSchemaNodes(ResolvedSchemaDefinition schema, ref int total)
    {
        CountNode(ref total);
        switch (schema)
        {
            case ObjectSchemaDefinition @object:
                foreach (var field in @object.Fields)
                {
                    CountNode(ref total);
                    CountSchemaNodes(field.Type, ref total);
                }
                break;
            case EnumSchemaDefinition @enum:
                foreach (var member in @enum.Members)
                    CountNode(ref total);
                break;
        }
    }

    private static void CountNode(ref int total)
    {
        if (++total > MaximumTotalSchemaNodes)
        {
            throw new ArgumentException(
                $"Aggregate schema-node count exceeds the bounded limit of {MaximumTotalSchemaNodes}.");
        }
    }

    private sealed class SchemaSnapshotState
    {
        private readonly HashSet<object> active = new(ReferenceEqualityComparer.Instance);
        private int depth;
        private int nodes;

        internal void Enter(object value)
        {
            if (++depth > MaximumSchemaDepth)
                throw new ArgumentException($"Schema nesting exceeds the bounded limit of {MaximumSchemaDepth}.");
            if (!active.Add(value))
                throw new ArgumentException("Schema definition contains a reference cycle.");
            if (++nodes > MaximumTotalSchemaNodes)
                throw new ArgumentException(
                    $"Aggregate schema-node count exceeds the bounded limit of {MaximumTotalSchemaNodes}.");
        }

        internal void Exit(object value)
        {
            active.Remove(value);
            depth--;
        }
    }
}
