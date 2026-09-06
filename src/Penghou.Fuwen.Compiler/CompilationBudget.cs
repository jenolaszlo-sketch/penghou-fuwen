#pragma warning disable CS1591
namespace Penghou.Fuwen.Compiler;

/// <summary>Resource dimensions bounded by the compiler. Parser dimensions are reserved for a later parser.</summary>
public enum CompilationBudgetDimension
{
    SourceBytes = 0,
    Tokens = 1,
    AstNodes = 2,
    NestingDepth = 3,
    WorkflowNodes = 4,
    Schemas = 5,
    SchemaDepth = 6,
    SchemaFields = 7,
    Expressions = 8,
    StringBytes = 9,
    Diagnostics = 10,
    CatalogueLookups = 11,
    CatalogueLookupMilliseconds = 12,
    CompilationMilliseconds = 13,
}

/// <summary>An immutable host ceiling or caller-requested narrowing of compilation resources.</summary>
public sealed class CompilationBudget
{
    public const int DefaultMaximumDiagnostics = 100;

    public CompilationBudget(
        long maxSourceBytes = 1_048_576,
        long maxTokens = 100_000,
        long maxAstNodes = 100_000,
        int maxNestingDepth = 128,
        int maxWorkflowNodes = 1_000,
        int maxSchemas = 1_000,
        int maxSchemaDepth = 64,
        int maxSchemaFields = 10_000,
        int maxExpressions = 100_000,
        long maxStringBytes = 1_048_576,
        int maxDiagnostics = DefaultMaximumDiagnostics,
        int maxCatalogueLookups = 10_000,
        long maxCatalogueLookupMilliseconds = 5_000,
        long maxCompilationMilliseconds = 30_000)
    {
        MaxSourceBytes = Positive(maxSourceBytes, nameof(maxSourceBytes));
        MaxTokens = Positive(maxTokens, nameof(maxTokens));
        MaxAstNodes = Positive(maxAstNodes, nameof(maxAstNodes));
        MaxNestingDepth = Positive(maxNestingDepth, nameof(maxNestingDepth));
        MaxWorkflowNodes = Positive(maxWorkflowNodes, nameof(maxWorkflowNodes));
        MaxSchemas = Positive(maxSchemas, nameof(maxSchemas));
        MaxSchemaDepth = Positive(maxSchemaDepth, nameof(maxSchemaDepth));
        MaxSchemaFields = Positive(maxSchemaFields, nameof(maxSchemaFields));
        MaxExpressions = Positive(maxExpressions, nameof(maxExpressions));
        MaxStringBytes = Positive(maxStringBytes, nameof(maxStringBytes));
        MaxDiagnostics = Positive(maxDiagnostics, nameof(maxDiagnostics));
        MaxCatalogueLookups = Positive(maxCatalogueLookups, nameof(maxCatalogueLookups));
        MaxCatalogueLookupMilliseconds = Positive(maxCatalogueLookupMilliseconds, nameof(maxCatalogueLookupMilliseconds));
        MaxCompilationMilliseconds = Positive(maxCompilationMilliseconds, nameof(maxCompilationMilliseconds));
    }

    public static CompilationBudget HostDefaults { get; } = new();
    public static CompilationBudget Default => HostDefaults;

    public long MaxSourceBytes { get; }
    public long MaxTokens { get; }
    public long MaxAstNodes { get; }
    public int MaxNestingDepth { get; }
    public int MaxWorkflowNodes { get; }
    public int MaxSchemas { get; }
    public int MaxSchemaDepth { get; }
    public int MaxSchemaFields { get; }
    public int MaxExpressions { get; }
    public long MaxStringBytes { get; }
    public int MaxDiagnostics { get; }
    public int MaxCatalogueLookups { get; }
    public long MaxCatalogueLookupMilliseconds { get; }
    public long MaxCompilationMilliseconds { get; }

    /// <summary>Returns the caller's request, provided every requested limit is within this host ceiling.</summary>
    public CompilationBudget Narrow(CompilationBudget caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        foreach (var (dimension, host, requested) in Values(this, caller))
        {
            if (requested > host)
                throw new ArgumentException($"Caller cannot expand the host ceiling for {dimension}.", nameof(caller));
        }

        return caller;
    }

    public static CompilationBudget ApplyCaller(CompilationBudget host, CompilationBudget caller)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Narrow(caller);
    }

    public static CompilationBudget CreateEffective(CompilationBudget host, CompilationBudget caller) => ApplyCaller(host, caller);

    public bool IsAtMost(CompilationBudget ceiling) => ceiling is not null && Values(ceiling, this).All(static x => x.requested <= x.host);

    internal long Limit(CompilationBudgetDimension dimension) => dimension switch
    {
        CompilationBudgetDimension.SourceBytes => MaxSourceBytes,
        CompilationBudgetDimension.Tokens => MaxTokens,
        CompilationBudgetDimension.AstNodes => MaxAstNodes,
        CompilationBudgetDimension.NestingDepth => MaxNestingDepth,
        CompilationBudgetDimension.WorkflowNodes => MaxWorkflowNodes,
        CompilationBudgetDimension.Schemas => MaxSchemas,
        CompilationBudgetDimension.SchemaDepth => MaxSchemaDepth,
        CompilationBudgetDimension.SchemaFields => MaxSchemaFields,
        CompilationBudgetDimension.Expressions => MaxExpressions,
        CompilationBudgetDimension.StringBytes => MaxStringBytes,
        CompilationBudgetDimension.Diagnostics => MaxDiagnostics,
        CompilationBudgetDimension.CatalogueLookups => MaxCatalogueLookups,
        CompilationBudgetDimension.CatalogueLookupMilliseconds => MaxCatalogueLookupMilliseconds,
        CompilationBudgetDimension.CompilationMilliseconds => MaxCompilationMilliseconds,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static int Positive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    private static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);

    private static IEnumerable<(CompilationBudgetDimension dimension, long host, long requested)> Values(CompilationBudget host, CompilationBudget requested)
    {
        yield return (CompilationBudgetDimension.SourceBytes, host.MaxSourceBytes, requested.MaxSourceBytes);
        yield return (CompilationBudgetDimension.Tokens, host.MaxTokens, requested.MaxTokens);
        yield return (CompilationBudgetDimension.AstNodes, host.MaxAstNodes, requested.MaxAstNodes);
        yield return (CompilationBudgetDimension.NestingDepth, host.MaxNestingDepth, requested.MaxNestingDepth);
        yield return (CompilationBudgetDimension.WorkflowNodes, host.MaxWorkflowNodes, requested.MaxWorkflowNodes);
        yield return (CompilationBudgetDimension.Schemas, host.MaxSchemas, requested.MaxSchemas);
        yield return (CompilationBudgetDimension.SchemaDepth, host.MaxSchemaDepth, requested.MaxSchemaDepth);
        yield return (CompilationBudgetDimension.SchemaFields, host.MaxSchemaFields, requested.MaxSchemaFields);
        yield return (CompilationBudgetDimension.Expressions, host.MaxExpressions, requested.MaxExpressions);
        yield return (CompilationBudgetDimension.StringBytes, host.MaxStringBytes, requested.MaxStringBytes);
        yield return (CompilationBudgetDimension.Diagnostics, host.MaxDiagnostics, requested.MaxDiagnostics);
        yield return (CompilationBudgetDimension.CatalogueLookups, host.MaxCatalogueLookups, requested.MaxCatalogueLookups);
        yield return (CompilationBudgetDimension.CatalogueLookupMilliseconds, host.MaxCatalogueLookupMilliseconds, requested.MaxCatalogueLookupMilliseconds);
        yield return (CompilationBudgetDimension.CompilationMilliseconds, host.MaxCompilationMilliseconds, requested.MaxCompilationMilliseconds);
    }
}

/// <summary>Observed resource use for one compilation.</summary>
public sealed record CompilationUsageSummary
{
    public CompilationUsageSummary(
        long sourceBytes = 0,
        long tokens = 0,
        long astNodes = 0,
        int nestingDepth = 0,
        int workflowNodes = 0,
        int schemas = 0,
        int schemaDepth = 0,
        int schemaFields = 0,
        int expressions = 0,
        long stringBytes = 0,
        int diagnostics = 0,
        int catalogueLookups = 0,
        long catalogueLookupMilliseconds = 0,
        long compilationMilliseconds = 0)
    {
        SourceBytes = NonNegative(sourceBytes, nameof(sourceBytes));
        Tokens = NonNegative(tokens, nameof(tokens));
        AstNodes = NonNegative(astNodes, nameof(astNodes));
        NestingDepth = NonNegative(nestingDepth, nameof(nestingDepth));
        WorkflowNodes = NonNegative(workflowNodes, nameof(workflowNodes));
        Schemas = NonNegative(schemas, nameof(schemas));
        SchemaDepth = NonNegative(schemaDepth, nameof(schemaDepth));
        SchemaFields = NonNegative(schemaFields, nameof(schemaFields));
        Expressions = NonNegative(expressions, nameof(expressions));
        StringBytes = NonNegative(stringBytes, nameof(stringBytes));
        Diagnostics = NonNegative(diagnostics, nameof(diagnostics));
        CatalogueLookups = NonNegative(catalogueLookups, nameof(catalogueLookups));
        CatalogueLookupMilliseconds = NonNegative(catalogueLookupMilliseconds, nameof(catalogueLookupMilliseconds));
        CompilationMilliseconds = NonNegative(compilationMilliseconds, nameof(compilationMilliseconds));
    }

    public long SourceBytes { get; }
    public long Tokens { get; }
    public long AstNodes { get; }
    public int NestingDepth { get; }
    public int WorkflowNodes { get; }
    public int Schemas { get; }
    public int SchemaDepth { get; }
    public int SchemaFields { get; }
    public int Expressions { get; }
    public long StringBytes { get; }
    public int Diagnostics { get; }
    public int CatalogueLookups { get; }
    public long CatalogueLookupMilliseconds { get; }
    public long CompilationMilliseconds { get; }

    private static int NonNegative(int value, string name) => value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
    private static long NonNegative(long value, string name) => value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
}

/// <summary>The result of comparing observed use with an effective budget.</summary>
public sealed class CompilationBudgetEvaluation
{
    internal CompilationBudgetEvaluation(CompilationBudget budget, CompilationUsageSummary usage, IReadOnlyList<CompilationBudgetDimension> exceeded, DiagnosticCollection diagnostics)
    {
        Budget = budget;
        Usage = usage;
        ExceededDimensions = exceeded;
        Diagnostics = diagnostics;
    }

    public CompilationBudget Budget { get; }
    public CompilationUsageSummary Usage { get; }
    public IReadOnlyList<CompilationBudgetDimension> ExceededDimensions { get; }
    public DiagnosticCollection Diagnostics { get; }
    public bool IsWithinBudget => ExceededDimensions.Count == 0;
}

/// <summary>Evaluates hard budgets and emits deterministic diagnostics for every exceeded dimension.</summary>
public static class CompilationBudgetEvaluator
{
    public static CompilationBudgetEvaluation Evaluate(CompilationBudget budget, CompilationUsageSummary usage)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(usage);
        var exceeded = Enum.GetValues<CompilationBudgetDimension>()
            .Where(dimension => Usage(usage, dimension) > budget.Limit(dimension))
            .OrderBy(static d => d)
            .ToArray();
        var diagnostics = exceeded.Select(dimension => new CompilerDiagnostic(
            Code(dimension),
            DiagnosticSeverity.Error,
            DiagnosticPhase.Budget,
            $"Compilation budget exceeded for {DisplayName(dimension)}.",
            expected: budget.Limit(dimension).ToString(System.Globalization.CultureInfo.InvariantCulture),
            actual: Usage(usage, dimension).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new CompilationBudgetEvaluation(budget, usage, Array.AsReadOnly(exceeded), new DiagnosticCollection(diagnostics, budget.MaxDiagnostics));
    }

    private static long Usage(CompilationUsageSummary usage, CompilationBudgetDimension dimension) => dimension switch
    {
        CompilationBudgetDimension.SourceBytes => usage.SourceBytes,
        CompilationBudgetDimension.Tokens => usage.Tokens,
        CompilationBudgetDimension.AstNodes => usage.AstNodes,
        CompilationBudgetDimension.NestingDepth => usage.NestingDepth,
        CompilationBudgetDimension.WorkflowNodes => usage.WorkflowNodes,
        CompilationBudgetDimension.Schemas => usage.Schemas,
        CompilationBudgetDimension.SchemaDepth => usage.SchemaDepth,
        CompilationBudgetDimension.SchemaFields => usage.SchemaFields,
        CompilationBudgetDimension.Expressions => usage.Expressions,
        CompilationBudgetDimension.StringBytes => usage.StringBytes,
        CompilationBudgetDimension.Diagnostics => usage.Diagnostics,
        CompilationBudgetDimension.CatalogueLookups => usage.CatalogueLookups,
        CompilationBudgetDimension.CatalogueLookupMilliseconds => usage.CatalogueLookupMilliseconds,
        CompilationBudgetDimension.CompilationMilliseconds => usage.CompilationMilliseconds,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static string DisplayName(CompilationBudgetDimension dimension) => dimension switch
    {
        CompilationBudgetDimension.SourceBytes => "source bytes",
        CompilationBudgetDimension.AstNodes => "AST nodes",
        CompilationBudgetDimension.SchemaDepth => "schema depth",
        CompilationBudgetDimension.SchemaFields => "schema fields",
        CompilationBudgetDimension.CatalogueLookupMilliseconds => "catalogue lookup milliseconds",
        CompilationBudgetDimension.CompilationMilliseconds => "compilation milliseconds",
        _ => dimension.ToString(),
    };

    private static string Code(CompilationBudgetDimension dimension) => dimension switch
    {
        CompilationBudgetDimension.SourceBytes => CompilerDiagnosticCodes.BudgetSourceBytesExceeded,
        CompilationBudgetDimension.Tokens => CompilerDiagnosticCodes.BudgetTokensExceeded,
        CompilationBudgetDimension.AstNodes => CompilerDiagnosticCodes.BudgetAstNodesExceeded,
        CompilationBudgetDimension.NestingDepth => CompilerDiagnosticCodes.BudgetNestingDepthExceeded,
        CompilationBudgetDimension.WorkflowNodes => CompilerDiagnosticCodes.BudgetWorkflowNodesExceeded,
        CompilationBudgetDimension.Schemas => CompilerDiagnosticCodes.BudgetSchemasExceeded,
        CompilationBudgetDimension.SchemaDepth => CompilerDiagnosticCodes.BudgetSchemaDepthExceeded,
        CompilationBudgetDimension.SchemaFields => CompilerDiagnosticCodes.BudgetSchemaFieldsExceeded,
        CompilationBudgetDimension.Expressions => CompilerDiagnosticCodes.BudgetExpressionsExceeded,
        CompilationBudgetDimension.StringBytes => CompilerDiagnosticCodes.BudgetStringBytesExceeded,
        CompilationBudgetDimension.Diagnostics => CompilerDiagnosticCodes.BudgetDiagnosticsExceeded,
        CompilationBudgetDimension.CatalogueLookups => CompilerDiagnosticCodes.BudgetCatalogueLookupsExceeded,
        CompilationBudgetDimension.CatalogueLookupMilliseconds => CompilerDiagnosticCodes.BudgetCatalogueLookupMillisecondsExceeded,
        CompilationBudgetDimension.CompilationMilliseconds => CompilerDiagnosticCodes.BudgetCompilationMillisecondsExceeded,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };
}

/// <summary>Simple bounded usage accumulator for compiler phases.</summary>
public sealed class CompilationBudgetTracker
{
    private readonly CompilationBudget _budget;
    private readonly long[] _usage = new long[Enum.GetValues<CompilationBudgetDimension>().Length];

    public CompilationBudgetTracker(CompilationBudget budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public bool TryConsume(CompilationBudgetDimension dimension, long amount = 1)
    {
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var index = (int)dimension;
        if (_usage[index] > _budget.Limit(dimension) - amount)
            return false;
        _usage[index] += amount;
        return true;
    }

    public bool IsExceeded(CompilationBudgetDimension dimension)
    {
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));
        return _usage[(int)dimension] > _budget.Limit(dimension);
    }

    public CompilationUsageSummary Snapshot() => new(
        sourceBytes: _usage[(int)CompilationBudgetDimension.SourceBytes],
        tokens: _usage[(int)CompilationBudgetDimension.Tokens],
        astNodes: _usage[(int)CompilationBudgetDimension.AstNodes],
        nestingDepth: CheckedInt(_usage[(int)CompilationBudgetDimension.NestingDepth]),
        workflowNodes: CheckedInt(_usage[(int)CompilationBudgetDimension.WorkflowNodes]),
        schemas: CheckedInt(_usage[(int)CompilationBudgetDimension.Schemas]),
        schemaDepth: CheckedInt(_usage[(int)CompilationBudgetDimension.SchemaDepth]),
        schemaFields: CheckedInt(_usage[(int)CompilationBudgetDimension.SchemaFields]),
        expressions: CheckedInt(_usage[(int)CompilationBudgetDimension.Expressions]),
        stringBytes: _usage[(int)CompilationBudgetDimension.StringBytes],
        diagnostics: CheckedInt(_usage[(int)CompilationBudgetDimension.Diagnostics]),
        catalogueLookups: CheckedInt(_usage[(int)CompilationBudgetDimension.CatalogueLookups]),
        catalogueLookupMilliseconds: _usage[(int)CompilationBudgetDimension.CatalogueLookupMilliseconds],
        compilationMilliseconds: _usage[(int)CompilationBudgetDimension.CompilationMilliseconds]);

    public CompilationBudgetEvaluation Evaluate() => CompilationBudgetEvaluator.Evaluate(_budget, Snapshot());

    private static int CheckedInt(long value) => checked((int)value);
}
