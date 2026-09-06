#pragma warning disable CS1591
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>The severity of a compiler diagnostic.</summary>
public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>The compiler phase that produced a diagnostic.</summary>
public enum DiagnosticPhase
{
    General = 0,
    Lexing = 1,
    Parsing = 2,
    Binding = 3,
    Typing = 4,
    Validation = 5,
    Admission = 6,
    Budget = 7,
}

/// <summary>Stable identifiers for diagnostics emitted by the compiler.</summary>
public static class CompilerDiagnosticCodes
{
    public const string DiagnosticsTruncated = "FWN-BUDGET-002";
    public const string InvalidBudget = "FWN-BUDGET-003";
    public const string BudgetSourceBytesExceeded = "FWN-BUDGET-101";
    public const string BudgetTokensExceeded = "FWN-BUDGET-102";
    public const string BudgetAstNodesExceeded = "FWN-BUDGET-103";
    public const string BudgetNestingDepthExceeded = "FWN-BUDGET-104";
    public const string BudgetWorkflowNodesExceeded = "FWN-BUDGET-105";
    public const string BudgetSchemasExceeded = "FWN-BUDGET-106";
    public const string BudgetSchemaDepthExceeded = "FWN-BUDGET-107";
    public const string BudgetSchemaFieldsExceeded = "FWN-BUDGET-108";
    public const string BudgetExpressionsExceeded = "FWN-BUDGET-109";
    public const string BudgetStringBytesExceeded = "FWN-BUDGET-110";
    public const string BudgetCatalogueLookupsExceeded = "FWN-BUDGET-111";
    public const string BudgetCatalogueLookupMillisecondsExceeded = "FWN-BUDGET-112";
    public const string BudgetCompilationMillisecondsExceeded = "FWN-BUDGET-113";
    public const string BudgetDiagnosticsExceeded = "FWN-BUDGET-114";
    public const string CanonicalDefinitionTooLarge = "FWN-BUDGET-115";
    public const string CatalogueDescriptorNotFound = "FWN-CATALOGUE-001";
    public const string CatalogueDescriptorDigestMismatch = "FWN-CATALOGUE-002";
    public const string CatalogueResolutionInvalidResult = "FWN-CATALOGUE-003";
    public const string CatalogueSchemaPayloadMissing = "FWN-CATALOGUE-004";
    public const string CatalogueSchemaMismatch = "FWN-CATALOGUE-005";
    public const string SemanticValidationFailed = "FWN-VALIDATION-001";
    public const string BindingReferenceInvalid = "FWN-BINDING-001";
    public const string BindingTypeMismatch = "FWN-TYPING-001";
    public const string BindingProjectionInvalid = "FWN-TYPING-002";
    public const string CapabilityManifestMismatch = "FWN-ADMISSION-001";
    public const string CapabilityNotGranted = "FWN-ADMISSION-002";
    public const string ContextSnapshotInvalid = "FWN-BINDING-002";
    public const string ContextSnapshotDuplicate = "FWN-BINDING-003";
}

/// <summary>An immutable compiler diagnostic suitable for machine and human consumption.</summary>
public sealed record CompilerDiagnostic
{
    public CompilerDiagnostic(
        string code,
        DiagnosticSeverity severity,
        DiagnosticPhase phase,
        string message,
        string? path = null,
        SourceSpan? span = null,
        string? expected = null,
        string? actual = null)
    {
        Code = CompilerContractValidation.Text(code, nameof(code), 64, required: true)!;
        Message = CompilerContractValidation.Text(message, nameof(message), 4096, required: true)!;
        Path = CompilerContractValidation.Text(path, nameof(path), 1024, required: false);
        Expected = CompilerContractValidation.Text(expected, nameof(expected), 2048, required: false);
        Actual = CompilerContractValidation.Text(actual, nameof(actual), 2048, required: false);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));

        Severity = severity;
        Phase = phase;
        Span = span is null ? null : CompilerContractValidation.SnapshotSpan(span);
    }

    public string Code { get; }
    public DiagnosticSeverity Severity { get; }
    public DiagnosticPhase Phase { get; }
    public string Message { get; }
    public string? Path { get; }
    public SourceSpan? Span { get; }
    public string? Expected { get; }
    public string? Actual { get; }
}

/// <summary>A deterministic, immutable, bounded diagnostic snapshot.</summary>
public sealed class DiagnosticCollection : IReadOnlyList<CompilerDiagnostic>
{
    public const int MaximumInputDiagnostics = 100_000;
    private readonly IReadOnlyList<CompilerDiagnostic> _items;

    public DiagnosticCollection(IEnumerable<CompilerDiagnostic> diagnostics, int maximumCount = CompilationBudget.DefaultMaximumDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (maximumCount < 0 || maximumCount > MaximumInputDiagnostics)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        var snapshot = ReadBounded(diagnostics)
            .OrderBy(static d => d.Span is null ? 1 : 0)
            .ThenBy(static d => d.Span?.DocumentId, StringComparer.Ordinal)
            .ThenBy(static d => d.Span?.StartUtf8ByteOffset ?? long.MaxValue)
            .ThenBy(static d => d.Span?.Utf8ByteLength ?? long.MaxValue)
            .ThenBy(static d => d.Span?.StartLine ?? int.MaxValue)
            .ThenBy(static d => d.Span?.StartUtf16Column ?? int.MaxValue)
            .ThenBy(static d => d.Path, StringComparer.Ordinal)
            .ThenBy(static d => d.Phase)
            .ThenBy(static d => d.Severity)
            .ThenBy(static d => d.Code, StringComparer.Ordinal)
            .ThenBy(static d => d.Message, StringComparer.Ordinal)
            .ThenBy(static d => d.Expected, StringComparer.Ordinal)
            .ThenBy(static d => d.Actual, StringComparer.Ordinal)
            .ToArray();

        HasErrors = snapshot.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        DroppedCount = Math.Max(0, snapshot.Length - maximumCount);
        _items = Array.AsReadOnly(snapshot.Take(maximumCount).ToArray());
    }

    internal DiagnosticCollection(IEnumerable<CompilerDiagnostic> diagnostics, int maximumCount, int alreadyDropped)
    {
        var snapshot = diagnostics.OrderBy(static d => d.Span is null ? 1 : 0)
            .ThenBy(static d => d.Span?.DocumentId, StringComparer.Ordinal)
            .ThenBy(static d => d.Span?.StartUtf8ByteOffset ?? long.MaxValue)
            .ThenBy(static d => d.Span?.Utf8ByteLength ?? long.MaxValue)
            .ThenBy(static d => d.Span?.StartLine ?? int.MaxValue)
            .ThenBy(static d => d.Span?.StartUtf16Column ?? int.MaxValue)
            .ThenBy(static d => d.Path, StringComparer.Ordinal)
            .ThenBy(static d => d.Phase)
            .ThenBy(static d => d.Severity)
            .ThenBy(static d => d.Code, StringComparer.Ordinal)
            .ThenBy(static d => d.Message, StringComparer.Ordinal)
            .ThenBy(static d => d.Expected, StringComparer.Ordinal)
            .ThenBy(static d => d.Actual, StringComparer.Ordinal)
            .ToArray();
        HasErrors = snapshot.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        DroppedCount = checked(Math.Max(0, snapshot.Length - maximumCount) + alreadyDropped);
        _items = Array.AsReadOnly(snapshot.Take(maximumCount).ToArray());
    }

    public int Count => _items.Count;
    public int DroppedCount { get; }
    public bool IsTruncated => DroppedCount != 0;
    internal bool HasErrors { get; }
    public CompilerDiagnostic this[int index] => _items[index];
    public IEnumerator<CompilerDiagnostic> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private static IReadOnlyList<CompilerDiagnostic> ReadBounded(IEnumerable<CompilerDiagnostic> diagnostics)
    {
        var snapshot = new List<CompilerDiagnostic>(Math.Min(MaximumInputDiagnostics, 256));
        using var enumerator = diagnostics.GetEnumerator();
        for (var index = 0; index < MaximumInputDiagnostics; index++)
        {
            if (!enumerator.MoveNext())
                return snapshot;
            snapshot.Add(enumerator.Current ?? throw new ArgumentException("A diagnostic cannot be null.", nameof(diagnostics)));
        }

        if (enumerator.MoveNext())
            throw new InvalidOperationException($"Diagnostic input exceeds the hard ceiling of {MaximumInputDiagnostics} items.");
        return snapshot;
    }
}

/// <summary>Collects diagnostics without retaining more than the configured cap.</summary>
public sealed class DiagnosticBuilder
{
    private readonly int _maximumCount;
    private readonly List<CompilerDiagnostic> _items;
    private int _droppedCount;

    public DiagnosticBuilder(int maximumCount = CompilationBudget.DefaultMaximumDiagnostics)
    {
        if (maximumCount < 0 || maximumCount > DiagnosticCollection.MaximumInputDiagnostics)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        _maximumCount = maximumCount;
        _items = new List<CompilerDiagnostic>(Math.Min(maximumCount, 32));
    }

    public int Count => _items.Count;
    public int DroppedCount => _droppedCount;

    public bool Add(CompilerDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (_items.Count >= _maximumCount)
        {
            _droppedCount++;
            return false;
        }

        _items.Add(diagnostic);
        return true;
    }

    public DiagnosticCollection ToImmutable() => new(_items, _maximumCount, _droppedCount);
}

/// <summary>Immutable result shape for a compiler invocation.</summary>
public sealed class CompilationResult
{
    private readonly WorkflowDefinitionDocument? _definition;

    public CompilationResult(
        WorkflowPlan? plan,
        IEnumerable<CompilerDiagnostic> diagnostics,
        CompilationUsageSummary usage,
        CompilationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Diagnostics = new DiagnosticCollection(diagnostics, budget.MaxDiagnostics);
        _definition = plan is null || Diagnostics.HasErrors ? null : WorkflowDefinitionDocument.Create(plan);
    }

    internal CompilationResult(
        WorkflowDefinitionDocument? definition,
        IEnumerable<CompilerDiagnostic> diagnostics,
        CompilationUsageSummary usage,
        CompilationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Diagnostics = new DiagnosticCollection(diagnostics, budget.MaxDiagnostics);
        _definition = definition is null || Diagnostics.HasErrors ? null : definition;
    }

    public WorkflowPlan? Plan => _definition?.ReadPlan();
    /// <summary>The canonical immutable definition when compilation and capability checks succeeded; error diagnostics suppress it.</summary>
    public WorkflowDefinitionDocument? Definition => _definition;
    public DiagnosticCollection Diagnostics { get; }
    public CompilationUsageSummary Usage { get; }
    public CompilationBudget Budget { get; }
    public bool Succeeded => Plan is not null && !Diagnostics.HasErrors;
}

internal static class CompilerContractValidation
{
    internal static string? Text(string? value, string parameterName, int maximumLength, bool required)
    {
        if (value is null)
        {
            if (required)
                throw new ArgumentNullException(parameterName);
            return null;
        }

        if (required && value.Length == 0)
            throw new ArgumentException("The value cannot be empty.", parameterName);
        if (value.Length > maximumLength)
            throw new ArgumentOutOfRangeException(parameterName);
        if (value.Any(static c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException("The value contains a control character.", parameterName);
        return new string(value.ToCharArray());
    }

    internal static SourceSpan SnapshotSpan(SourceSpan span)
    {
        if (span.StartUtf8ByteOffset < 0 || span.Utf8ByteLength < 0 || span.StartLine < 0 || span.StartUtf16Column < 0)
            throw new ArgumentOutOfRangeException(nameof(span));
        return new SourceSpan(
            Text(span.DocumentId, nameof(span), 256, required: true)!,
            span.StartUtf8ByteOffset,
            span.Utf8ByteLength,
            span.StartLine,
            span.StartUtf16Column);
    }
}
