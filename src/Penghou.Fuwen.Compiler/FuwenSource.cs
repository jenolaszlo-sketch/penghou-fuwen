#pragma warning disable RS0016
#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.Json;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

/// <summary>Token kinds emitted by the bounded Fuwen lexer.</summary>
public enum FuwenTokenKind { Identifier, String, Number, Symbol, EndOfFile }

/// <summary>A detached token and its source location.</summary>
public sealed record FuwenToken(FuwenTokenKind Kind, string Text, SourceSpan Span);

/// <summary>The bounded result of lexical analysis.</summary>
public sealed class FuwenLexResult
{
    public FuwenLexResult(IReadOnlyList<FuwenToken> tokens, IEnumerable<CompilerDiagnostic> diagnostics, CompilationUsageSummary usage)
    {
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        Diagnostics = new DiagnosticCollection(diagnostics ?? throw new ArgumentNullException(nameof(diagnostics)));
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
    }
    public IReadOnlyList<FuwenToken> Tokens { get; }
    public DiagnosticCollection Diagnostics { get; }
    public CompilationUsageSummary Usage { get; }
    public bool Succeeded => !Diagnostics.HasErrors;
}

/// <summary>Resource-bounded lexer for the minimal Fuwen source language.</summary>
public static class FuwenLexer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static FuwenLexResult Lex(string source, string documentId = "source.fuwen", CompilationBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var limits = budget ?? CompilationBudget.Default;
        var output = new List<FuwenToken>();
        var diagnostics = new DiagnosticBuilder(limits.MaxDiagnostics);
        long bytes;
        try { bytes = StrictUtf8.GetByteCount(source); }
        catch (EncoderFallbackException)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.LexInvalidUnicode, DiagnosticSeverity.Error, DiagnosticPhase.Lexing,
                "Source contains an unpaired UTF-16 surrogate.",
                span: new SourceSpan(documentId, 0, 0, 0, 0)));
            return new FuwenLexResult(Array.Empty<FuwenToken>(), diagnostics.ToImmutable(), new CompilationUsageSummary());
        }
        if (bytes > limits.MaxSourceBytes)
        {
            diagnostics.Add(new CompilerDiagnostic(
                CompilerDiagnosticCodes.BudgetSourceBytesExceeded, DiagnosticSeverity.Error, DiagnosticPhase.Budget,
                "Source exceeds the configured UTF-8 byte limit.",
                span: new SourceSpan(documentId, 0, bytes, 0, 0),
                expected: limits.MaxSourceBytes.ToString(CultureInfo.InvariantCulture),
                actual: bytes.ToString(CultureInfo.InvariantCulture)));
            return new FuwenLexResult(Array.Empty<FuwenToken>(), diagnostics.ToImmutable(), new CompilationUsageSummary(sourceBytes: bytes));
        }
        var lexer = new Lexer(source, documentId, limits, diagnostics, output);
        lexer.Run();
        return new FuwenLexResult(
            Array.AsReadOnly(output.ToArray()), diagnostics.ToImmutable(),
            new CompilationUsageSummary(sourceBytes: bytes, tokens: lexer.TokenCount,
                nestingDepth: lexer.MaximumNestingDepth, stringBytes: lexer.StringBytes));
    }

    private sealed class Lexer
    {
        private readonly string source;
        private readonly string documentId;
        private readonly CompilationBudget limits;
        private readonly DiagnosticBuilder diagnostics;
        private readonly List<FuwenToken> output;
        private int index;
        private int line;
        private int column;
        private long utf8;
        private int nesting;
        internal Lexer(string source, string documentId, CompilationBudget limits, DiagnosticBuilder diagnostics, List<FuwenToken> output)
        {
            this.source = source; this.documentId = documentId; this.limits = limits;
            this.diagnostics = diagnostics; this.output = output;
        }
        internal long TokenCount { get; private set; }
        internal long StringBytes { get; private set; }
        internal int MaximumNestingDepth { get; private set; }

        internal void Run()
        {
            while (index < source.Length)
            {
                var c = source[index];
                if (char.IsWhiteSpace(c)) { Advance(c); continue; }
                if (c == '/' && Peek(1) == '/')
                {
                    Advance('/'); Advance('/');
                    while (index < source.Length && source[index] is not '\r' and not '\n') Advance(source[index]);
                    continue;
                }
                if (c == '/' && Peek(1) == '*')
                {
                    var mark = Mark(); Advance('/'); Advance('*'); var closed = false;
                    while (index < source.Length)
                    {
                        if (source[index] == '*' && Peek(1) == '/') { Advance('*'); Advance('/'); closed = true; break; }
                        Advance(source[index]);
                    }
                    if (!closed) Add(CompilerDiagnosticCodes.LexUnterminatedComment, "Unterminated block comment.", mark);
                    continue;
                }
                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var mark = Mark();
                    var identifierText = ReadIdentifier();
                    Emit(FuwenTokenKind.Identifier, identifierText, mark);
                    continue;
                }
                if (c == '"') { EmitString(); continue; }
                if (char.IsDigit(c) || (c == '-' && char.IsDigit(Peek(1))))
                {
                    var mark = Mark();
                    var numberText = ReadNumber();
                    Emit(FuwenTokenKind.Number, numberText, mark);
                    continue;
                }
                var mark2 = Mark();
                var text = c.ToString();
                if (c == '-' && Peek(1) == '>') { text = "->"; Advance('-'); Advance('>'); }
                else if ((c is '=' or '!' or '<' or '>') && Peek(1) == '=') { text += '='; Advance(c); Advance('='); }
                else if ("{}[]();,:.@#?<>=".Contains(c)) Advance(c);
                else
                {
                    var display = char.IsControl(c)
                        ? "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                        : c.ToString();
                    Add(CompilerDiagnosticCodes.LexUnexpectedCharacter, "Unexpected character '" + display + "'.", mark2);
                    Advance(c);
                    continue;
                }
                if (text is "{" or "[" or "(")
                {
                    nesting++;
                    MaximumNestingDepth = Math.Max(MaximumNestingDepth, nesting);
                    if (nesting == limits.MaxNestingDepth + 1)
                        Add(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Nesting depth exceeds the configured limit.", mark2);
                }
                else if (text is "}" or "]" or ")") nesting = Math.Max(0, nesting - 1);
                Emit(FuwenTokenKind.Symbol, text, mark2);
            }
            Emit(FuwenTokenKind.EndOfFile, string.Empty, Mark());
        }

        private string ReadIdentifier()
        {
            var start = index;
            while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '$' or '-')) Advance(source[index]);
            return source[start..index];
        }
        private string ReadNumber()
        {
            var start = index;
            if (source[index] == '-') Advance('-');
            while (index < source.Length && char.IsDigit(source[index])) Advance(source[index]);
            if (index < source.Length && source[index] == '.') { Advance('.'); while (index < source.Length && char.IsDigit(source[index])) Advance(source[index]); }
            if (index < source.Length && source[index] is 'e' or 'E')
            {
                Advance(source[index]);
                if (index < source.Length && source[index] is '+' or '-') Advance(source[index]);
                while (index < source.Length && char.IsDigit(source[index])) Advance(source[index]);
            }
            return source[start..index];
        }
        private void EmitString()
        {
            var mark = Mark(); var start = index; Advance('"'); var closed = false;
            while (index < source.Length)
            {
                var c = source[index];
                if (c == '"') { Advance(c); closed = true; break; }
                if (c == '\\') { Advance(c); if (index < source.Length) Advance(source[index]); continue; }
                if (c is '\r' or '\n') break;
                Advance(c);
            }
            var raw = source[start..index]; StringBytes += Encoding.UTF8.GetByteCount(raw);
            if (StringBytes > limits.MaxStringBytes) Add(CompilerDiagnosticCodes.BudgetStringBytesExceeded, "String literals exceed the configured byte limit.", mark);
            if (!closed) { Add(CompilerDiagnosticCodes.LexUnterminatedString, "Unterminated string literal.", mark); return; }
            try { using var _ = JsonDocument.Parse(raw); } catch (JsonException) { Add(CompilerDiagnosticCodes.LexInvalidEscape, "String literal contains an invalid JSON escape.", mark); }
            Emit(FuwenTokenKind.String, raw, mark);
        }
        private void Emit(FuwenTokenKind kind, string text, Position? mark = null)
        {
            var location = mark ?? Mark(); TokenCount++;
            if (TokenCount > limits.MaxTokens)
            {
                if (TokenCount == limits.MaxTokens + 1) Add(CompilerDiagnosticCodes.BudgetTokensExceeded, "Token count exceeds the configured limit.", location);
                return;
            }
            output.Add(new FuwenToken(kind, text, new SourceSpan(documentId, location.Utf8, Math.Max(0, utf8 - location.Utf8), location.Line, location.Column)));
        }
        private char Peek(int offset) => index + offset < source.Length ? source[index + offset] : '\0';
        private Position Mark() => new(utf8, line, column);
        private void Advance(char c)
        {
            if (index >= source.Length) return;
            if (char.IsHighSurrogate(c) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
            {
                index += 2;
                utf8 += 4;
                column += 2;
                return;
            }
            index++;
            utf8 += Encoding.UTF8.GetByteCount(new[] { c });
            if (c == '\n') { line++; column = 0; } else column++;
        }
        private void Add(string code, string message, Position mark) => diagnostics.Add(new CompilerDiagnostic(
            code, DiagnosticSeverity.Error, code.StartsWith("FWN-LEX", StringComparison.Ordinal) ? DiagnosticPhase.Lexing : DiagnosticPhase.Budget,
            message, span: new SourceSpan(documentId, mark.Utf8, 1, mark.Line, mark.Column)));
        private readonly record struct Position(long Utf8, int Line, int Column);
    }
}

/// <summary>Canonical idempotent formatter for Fuwen source.</summary>
public static class FuwenFormatter
{
    public static string Format(string source, CompilationBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return FormatTokens(FuwenLexer.Lex(source, budget: budget).Tokens);
    }
    internal static string FormatTokens(IReadOnlyList<FuwenToken> tokens)
    {
        var result = new StringBuilder(); var indent = 0; var lineStart = true; FuwenToken? previous = null;
        foreach (var token in tokens)
        {
            if (token.Kind == FuwenTokenKind.EndOfFile) break;
            var wasLineStart = lineStart;
            if (token.Text == "}")
            {
                if (!lineStart) result.AppendLine();
                indent = Math.Max(0, indent - 1); result.Append(new string(' ', indent * 2)); lineStart = false;
            }
            else if (lineStart) { result.Append(new string(' ', indent * 2)); lineStart = false; }
            if (!wasLineStart && NeedsSpace(previous, token)) result.Append(' ');
            result.Append(token.Text);
            if (token.Text == "{") { indent++; result.AppendLine(); lineStart = true; }
            else if (token.Text == ";") { result.AppendLine(); lineStart = true; }
            else if (token.Text == "}") { if (previous?.Text != "{") result.AppendLine(); lineStart = true; }
            else if (token.Text == ",") result.Append(' ');
            previous = token;
        }
        return SourceIdentity.NormalizeFormattedSource(result.ToString());
    }
    private static bool NeedsSpace(FuwenToken? previous, FuwenToken current)
    {
        if (previous is null) return false;
        if (current.Text is ")" or "]" or ";" or "," or "." or "?" or ":" or "#" or "@") return false;
        if (previous.Text is "(" or "[" or "." or "@" or "#") return false;
        if (current.Text == "(" && previous.Kind == FuwenTokenKind.Identifier) return false;
        if (current.Text is "<" or ">" || previous.Text is "<" or ">") return false;
        return true;
    }
}

/// <summary>Compatibility formatter name for hosts that use workflow terminology.</summary>
public static class WorkflowFormatter
{
    public static string Format(string source, CompilationBudget? budget = null) => FuwenFormatter.Format(source, budget);
}

/// <summary>Source compilation result, including canonical text and source map.</summary>
public sealed class FuwenSourceCompilationResult
{
    internal FuwenSourceCompilationResult(CompilationResult compilation, WorkflowSourceMap? sourceMap, string formattedSource, IEnumerable<CompilerDiagnostic> diagnostics, CompilationUsageSummary usage)
    {
        Compilation = compilation; SourceMap = sourceMap; FormattedSource = formattedSource;
        Diagnostics = new DiagnosticCollection(diagnostics.Concat(compilation.Diagnostics).Distinct(), compilation.Budget.MaxDiagnostics);
        Usage = usage;
    }
    public CompilationResult Compilation { get; }
    public WorkflowPlan? Plan => Compilation.Plan;
    public WorkflowSourceMap? SourceMap { get; }
    public string FormattedSource { get; }
    public DiagnosticCollection Diagnostics { get; }
    public CompilationUsageSummary Usage { get; }
    public bool Succeeded => Plan is not null && !Diagnostics.HasErrors;
}

/// <summary>Parses source and lowers it through the existing trusted compiler.</summary>
public sealed class FuwenSourceCompiler
{
    private readonly ITrustedCatalogue catalogue;
    private readonly CompilationBudget hostBudget;
    private readonly CapabilityGrantPolicy capabilityPolicy;

    public FuwenSourceCompiler(ITrustedCatalogue catalogue, CompilationBudget? hostBudget = null, CapabilityGrantPolicy? capabilityPolicy = null)
    {
        this.catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        this.hostBudget = hostBudget ?? CompilationBudget.Default;
        this.capabilityPolicy = capabilityPolicy ?? new CapabilityGrantPolicy(Array.Empty<CapabilityRequirement>());
    }

    public FuwenSourceCompilationResult Compile(string source, string documentId = "source.fuwen", string logicalName = "source.fuwen", CompilationBudget? callerBudget = null, CancellationToken cancellationToken = default)
        => CompileAsync(source, documentId, logicalName, callerBudget, cancellationToken).AsTask().GetAwaiter().GetResult();

    public async ValueTask<FuwenSourceCompilationResult> CompileAsync(string source, string documentId = "source.fuwen", string logicalName = "source.fuwen", CompilationBudget? callerBudget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = callerBudget is null ? hostBudget : CompilationBudget.ApplyCaller(hostBudget, callerBudget);
        cancellationToken.ThrowIfCancellationRequested();
        var lex = FuwenLexer.Lex(source, documentId, budget);
        var formatted = FuwenFormatter.FormatTokens(lex.Tokens);
        var bindingCatalogue = new CachingCatalogue(catalogue);
        var parser = new SourceParser(lex.Tokens, documentId, bindingCatalogue, budget, cancellationToken);
        var parsed = parser.Parse();
        var parseDiagnostics = lex.Diagnostics.Concat(parsed.Diagnostics).ToList();
        var parsedPlan = parsed.Plan;
        var bindingUsage = new CompilationUsageSummary();
        if (!parseDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) && parsedPlan is not null && parsed.InferredOutputPaths.Count != 0)
        {
            var binding = await BindInferredOutputsAsync(parsedPlan, parsed.InferredOutputPaths, bindingCatalogue, budget, cancellationToken).ConfigureAwait(false);
            parseDiagnostics.AddRange(binding.Diagnostics);
            bindingUsage = binding.Usage;
            parsedPlan = binding.Plan;
        }
        CompilationResult compilation;
        var ranWorkflowCompiler = false;
        WorkflowSourceMap? sourceMap = null;
        if (parseDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) || parsedPlan is null)
            compilation = new CompilationResult(null, parseDiagnostics, MergeUsage(parsed.Usage, bindingUsage), budget);
        else
        {
            ranWorkflowCompiler = true;
            var compiler = new WorkflowCompiler(bindingCatalogue, hostBudget, capabilityPolicy);
            compilation = await compiler.CompileAsync(parsedPlan, callerBudget, cancellationToken).ConfigureAwait(false);
            if (compilation.Succeeded && compilation.Plan is not null)
            {
                // Spans are offsets into the authored document, so the map's
                // source identity and declared byte length must use that same
                // normalized document, rather than the comment-free formatter output.
                var digest = SourceDigest(SourceIdentity.ComputeSourceFingerprint(source));
                sourceMap = new WorkflowSourceMap(
                    WorkflowPlanIdentity.ComputeExecutionFingerprint(compilation.Plan), digest,
                    [new SourceDocumentReference(documentId, logicalName, digest, Encoding.UTF8.GetByteCount(source))],
                    parsed.SourceEntries);
                WorkflowSourceMapValidator.ValidateForPlan(sourceMap, compilation.Plan);
            }
        }
        var totalUsage = ranWorkflowCompiler
            ? MergeUsage(lex.Usage, parsed.Usage, bindingUsage, compilation.Usage)
            : MergeUsage(lex.Usage, compilation.Usage);
        return new FuwenSourceCompilationResult(compilation, sourceMap, formatted, parseDiagnostics, totalUsage);
    }

    private static async ValueTask<BindingOutputResult> BindInferredOutputsAsync(
        WorkflowPlan plan,
        IReadOnlySet<string> inferredPaths,
        ITrustedCatalogue catalogue,
        CompilationBudget budget,
        CancellationToken cancellationToken)
    {
        var requested = EnumerateNodes(plan.Nodes)
            .Where(node => inferredPaths.Contains(node.StructuralPath))
            .Select(node => node switch
            {
                ActivityNode activity => activity.Activity,
                ContextNode context => context.Provider,
                InferenceNode inference => inference.Profile,
                _ => null,
            })
            .Where(static descriptor => descriptor is not null)
            .Select(static descriptor => descriptor!)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
            return new BindingOutputResult(plan, Array.Empty<CompilerDiagnostic>(), new CompilationUsageSummary());

        var resolved = await new TrustedCatalogueResolver(catalogue, budget).ResolveManyAsync(requested, cancellationToken).ConfigureAwait(false);
        var contracts = resolved.Results
            .Where(static result => result.Succeeded && result.Descriptor?.CallableContract is not null)
            .ToDictionary(static result => result.Requested, static result => result.Descriptor!.CallableContract!.Signature.OutputType);
        var bound = ReplaceInferredOutputs(plan, inferredPaths, contracts);
        return new BindingOutputResult(bound, resolved.Diagnostics, resolved.Usage);
    }

    private static WorkflowPlan ReplaceInferredOutputs(
        WorkflowPlan plan,
        IReadOnlySet<string> inferredPaths,
        IReadOnlyDictionary<DescriptorReference, FuwenType> contracts) => plan with
        {
            Nodes = ReplaceNodes(plan.Nodes, inferredPaths, contracts),
        };

    private static IReadOnlyList<WorkflowNode> ReplaceNodes(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlySet<string> inferredPaths,
        IReadOnlyDictionary<DescriptorReference, FuwenType> contracts) => nodes.Select(node => node switch
        {
            ActivityNode activity when inferredPaths.Contains(activity.StructuralPath) && contracts.TryGetValue(activity.Activity, out var type)
                => activity with { OutputType = type },
            ContextNode context when inferredPaths.Contains(context.StructuralPath) && contracts.TryGetValue(context.Provider, out var type)
                => context with { OutputType = type },
            InferenceNode inference when inferredPaths.Contains(inference.StructuralPath) && contracts.TryGetValue(inference.Profile, out var type)
                => inference with { OutputType = type },
            ConditionalNode conditional => conditional with
            {
                Then = ReplaceNodes(conditional.Then, inferredPaths, contracts),
                Else = ReplaceNodes(conditional.Else, inferredPaths, contracts),
            },
            _ => node,
        }).ToArray();

    private static IEnumerable<WorkflowNode> EnumerateNodes(IEnumerable<WorkflowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node is ConditionalNode conditional)
            {
                foreach (var child in EnumerateNodes(conditional.Then.Concat(conditional.Else)))
                    yield return child;
            }
        }
    }

    private sealed record BindingOutputResult(WorkflowPlan Plan, IReadOnlyList<CompilerDiagnostic> Diagnostics, CompilationUsageSummary Usage);

    private sealed class CachingCatalogue(ITrustedCatalogue inner) : ITrustedCatalogue
    {
        private readonly object gate = new();
        private readonly Dictionary<DescriptorReference, DescriptorResolutionResult> cache = new();

        public async ValueTask<DescriptorResolutionResult> ResolveAsync(DescriptorReference descriptor, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (cache.TryGetValue(descriptor, out var cached))
                    return WithoutUsage(cached);
            }
            var result = await inner.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
            lock (gate) cache[descriptor] = result;
            return result;
        }

        private static DescriptorResolutionResult WithoutUsage(DescriptorResolutionResult result) => new(
            result.Requested,
            result.Status,
            result.Descriptor,
            result.Diagnostics,
            new CompilationUsageSummary());
    }

    private static ContentDigest SourceDigest(string fingerprint)
    {
        var parts = fingerprint.Split(':', 3);
        return new ContentDigest(parts[0], parts[1], parts[2]);
    }
    private static CompilationUsageSummary MergeUsage(params CompilationUsageSummary[] values) => new(
        sourceBytes: values.Sum(static value => value.SourceBytes), tokens: values.Sum(static value => value.Tokens),
        astNodes: values.Sum(static value => value.AstNodes), nestingDepth: values.Max(static value => value.NestingDepth),
        workflowNodes: values.Sum(static value => value.WorkflowNodes), schemas: values.Sum(static value => value.Schemas),
        schemaDepth: values.Max(static value => value.SchemaDepth), schemaFields: values.Sum(static value => value.SchemaFields),
        expressions: values.Sum(static value => value.Expressions), stringBytes: values.Sum(static value => value.StringBytes),
        diagnostics: values.Sum(static value => value.Diagnostics), catalogueLookups: values.Sum(static value => value.CatalogueLookups),
        catalogueLookupMilliseconds: values.Sum(static value => value.CatalogueLookupMilliseconds),
        compilationMilliseconds: values.Sum(static value => value.CompilationMilliseconds));
}

internal sealed class SourceParser
{
    private readonly IReadOnlyList<FuwenToken> tokens;
    private readonly string documentId;
    private readonly ITrustedCatalogue catalogue;
    private readonly CompilationBudget budget;
    private readonly CancellationToken cancellationToken;
    private readonly DiagnosticBuilder diagnostics;
    private readonly Dictionary<string, DescriptorReference> schemaAliases = new(StringComparer.Ordinal);
    private readonly List<ResolvedSchemaDefinition> schemas = [];
    private readonly List<WorkflowNode> nodes = [];
    private readonly List<CapabilityRequirement> capabilities = [];
    private readonly List<SourceMapEntry> sourceEntries = [];
    private readonly Dictionary<string, FuwenType> nodeTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nodePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> inferredOutputPaths = new(StringComparer.Ordinal);
    private int position;
    private long astNodes;
    private long expressions;
    private int schemaFields;
    private int typeDepth;
    private int bindingDepth;
    private int conditionalOrdinal;
    private bool workflowSeen;
    private string workflowName = string.Empty;
    private string revision = "1";
    private string routing = "default";
    private FuwenType inputType = new PrimitiveType(FuwenPrimitiveKind.Json);
    private FuwenType outputType = new PrimitiveType(FuwenPrimitiveKind.Json);

    internal SourceParser(IReadOnlyList<FuwenToken> tokens, string documentId, ITrustedCatalogue catalogue, CompilationBudget budget, CancellationToken cancellationToken)
    {
        this.tokens = tokens; this.documentId = documentId; this.catalogue = catalogue; this.budget = budget;
        this.cancellationToken = cancellationToken; diagnostics = new DiagnosticBuilder(budget.MaxDiagnostics);
    }
    internal IReadOnlyList<SourceMapEntry> SourceEntries => sourceEntries;

    internal ParseOutput Parse()
    {
        while (!AtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Match("schema")) ParseSchema();
            else if (Match("enum")) ParseEnum();
            else if (Match("capability")) ParseCapability();
            else if (Match("workflow"))
            {
                if (workflowSeen)
                {
                    Error(CompilerDiagnosticCodes.ParseUnexpectedToken, "A source document may declare only one workflow.", Previous);
                    Recover("schema", "enum", "workflow", "capability");
                }
                else
                {
                    workflowSeen = true;
                    ParseWorkflow();
                }
            }
            else { Error(CompilerDiagnosticCodes.ParseUnexpectedToken, "Expected a top-level declaration.", Current); Recover("schema", "enum", "workflow", "capability"); }
        }
        WorkflowPlan? plan = null;
        if (!workflowSeen)
            Error(CompilerDiagnosticCodes.ParseUnexpectedToken, "Source must declare one workflow.", Current);
        if (!diagnostics.ToImmutable().HasErrors && workflowName.Length != 0)
        {
            try
            {
                var builder = new WorkflowPlanBuilder(workflowName, revision, inputType, outputType, routing);
                foreach (var schema in schemas) builder.AddSchema(schema);
                foreach (var capability in capabilities) builder.RequireCapability(capability);
                foreach (var node in nodes) builder.AddNode(node);
                builder.SetExecutionOrder(new WorkflowExecutionOrder(BuildRegions(workflowName, nodes)));
                plan = builder.BuildV3();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, "Source could not be lowered to executable IR.", Current, exception.Message);
            }
        }
        var usage = new CompilationUsageSummary(astNodes: astNodes, workflowNodes: CountNodes(nodes),
            schemas: schemas.Count, schemaFields: schemaFields, expressions: checked((int)Math.Min(int.MaxValue, expressions)));
        return new ParseOutput(plan, diagnostics.ToImmutable(), usage, sourceEntries.AsReadOnly(), inferredOutputPaths.ToHashSet(StringComparer.Ordinal));
    }

    private void ParseSchema()
    {
        var alias = ReadIdentifier("schema name");
        var descriptor = ParseDescriptor(DescriptorKind.Schema, alias);
        Expect("{");
        var fields = new List<SchemaField>();
        while (!AtEnd && Current.Text != "}")
        {
            var fieldName = ReadIdentifier("schema field name"); Expect(":");
            var fieldType = ParseType();
            schemaFields++;
            if (schemaFields <= budget.MaxSchemaFields)
                fields.Add(new SchemaField(fieldName, fieldType));
            Ast();
            if (schemaFields == budget.MaxSchemaFields + 1)
                Error(CompilerDiagnosticCodes.BudgetSchemaFieldsExceeded, "Schema field count exceeds the configured limit.", Current);
            if (!Match(",")) Expect(";");
        }
        Expect("}");
        if (schemas.Count >= budget.MaxSchemas)
            Error(CompilerDiagnosticCodes.BudgetSchemasExceeded, "Schema count exceeds the configured limit.", Previous);
        else
            schemas.Add(new ObjectSchemaDefinition(descriptor, fields));
        schemaAliases[alias] = descriptor; schemaAliases[descriptor.Name] = descriptor;
    }

    private void ParseEnum()
    {
        var alias = ReadIdentifier("enum name");
        var descriptor = ParseDescriptor(DescriptorKind.Schema, alias);
        Expect("{");
        var members = new List<EnumMember>();
        while (!AtEnd && Current.Text != "}")
        {
            var name = ReadIdentifier("enum member name"); var value = name;
            if (Match("=")) value = ReadText("enum member value");
            schemaFields++;
            if (schemaFields <= budget.MaxSchemaFields)
                members.Add(new EnumMember(name, value));
            Ast();
            if (schemaFields == budget.MaxSchemaFields + 1)
                Error(CompilerDiagnosticCodes.BudgetSchemaFieldsExceeded, "Schema field count exceeds the configured limit.", Current);
            if (!Match(",")) Expect(";");
        }
        Expect("}");
        if (schemas.Count >= budget.MaxSchemas)
            Error(CompilerDiagnosticCodes.BudgetSchemasExceeded, "Schema count exceeds the configured limit.", Previous);
        else
            schemas.Add(new EnumSchemaDefinition(descriptor, members));
        schemaAliases[alias] = descriptor; schemaAliases[descriptor.Name] = descriptor;
    }

    private void ParseCapability()
    {
        var name = ReadText("capability name"); string? scope = null;
        if (Match(":")) scope = ReadText("capability scope");
        capabilities.Add(new CapabilityRequirement(name, scope)); Match(";");
    }

    private void ParseWorkflow()
    {
        workflowName = ReadIdentifier("workflow name");
        if (Match("@")) revision = ReadText("workflow revision");
        if (Match("version") || Match("revision")) { Match("="); revision = ReadText("workflow revision"); }
        Expect("(");
        if (!Match(")"))
        {
            _ = ReadIdentifier("workflow input name"); Expect(":"); inputType = ParseType(); Expect(")");
        }
        if (Match("->") || Match("returns") || Match("output")) { Match(":"); outputType = ParseType(); }
        if (Match("routing")) { Match(":"); routing = ReadText("routing policy revision"); }
        Expect("{");
        nodeTypes.Clear(); nodeTypes["input"] = inputType;
        nodePaths.Clear();
        ParseNodes(nodes, workflowName, true);
    }

    private void ParseNodes(List<WorkflowNode> destination, string parentPath, bool allowReturn)
    {
        var closed = false;
        while (!AtEnd)
        {
            if (Match("}")) { closed = true; break; }
            cancellationToken.ThrowIfCancellationRequested();
            var node = ParseNode(parentPath, allowReturn);
            if (node is not null)
            {
                var currentNodeCount = ReferenceEquals(destination, nodes)
                    ? CountNodes(destination)
                    : CountNodes(nodes) + CountNodes(destination);
                if (currentNodeCount < budget.MaxWorkflowNodes)
                    destination.Add(node);
                else if (currentNodeCount == budget.MaxWorkflowNodes)
                    Error(CompilerDiagnosticCodes.BudgetWorkflowNodesExceeded, "Workflow node count exceeds the configured limit.", Current);
            }
            else Recover("context", "activity", "infer", "if", "return", "}");
        }
        if (!closed)
            Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected '}'.", Current);
    }

    private WorkflowNode? ParseNode(string parentPath, bool allowReturn)
    {
        var start = Current;
        var keyword = ReadIdentifier("node keyword");
        return keyword switch
        {
            "context" => ParseCallable(parentPath, DescriptorKind.ContextProvider, start),
            "activity" => ParseCallable(parentPath, DescriptorKind.Activity, start),
            "infer" => ParseInference(parentPath, start),
            "if" => ParseConditional(parentPath, start),
            "return" => ParseReturn(parentPath, start, allowReturn),
            _ => Unsupported(keyword, start),
        };
    }

    private WorkflowNode ParseCallable(string parentPath, DescriptorKind kind, FuwenToken start)
    {
        var name = ReadIdentifier("node name"); FuwenType? declared = null;
        if (Match(":")) declared = ParseType();
        Expect("="); Match(kind == DescriptorKind.Activity ? "activity" : "context");
        var descriptor = ParseDescriptor(kind, name);
        var arguments = ParseArguments();
        if (Match("->") || Match(":")) declared = ParseType();
        Match(";");
        var type = declared ?? CallableOutput(descriptor);
        var path = parentPath + "/" + name;
        if (declared is null) inferredOutputPaths.Add(path);
        var node = kind == DescriptorKind.Activity
            ? (WorkflowNode)new ActivityNode(name, path, descriptor, arguments, type)
            : new ContextNode(name, path, descriptor, arguments, type);
        nodeTypes[name] = type; nodePaths[name] = path; AddSpan(path, start, Previous); Ast(); return node;
    }

    private WorkflowNode ParseInference(string parentPath, FuwenToken start)
    {
        var name = ReadIdentifier("inference node name"); FuwenType? declared = null;
        if (Match(":")) declared = ParseType();
        Expect("="); Match("infer");
        var profile = ParseDescriptor(DescriptorKind.InferenceProfile, name);
        if (!Match("using") && !Match(",")) Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected 'using' before prompt template.", Current);
        var template = ParseDescriptor(DescriptorKind.PromptTemplate, name);
        var arguments = ParseArguments(); var requirements = new List<ContextRequirement>();
        if (Match("with") || Match("context"))
        {
            do
            {
                var contextName = ReadIdentifier("context node name");
                if (nodeTypes.TryGetValue(contextName, out var contextType) && nodePaths.TryGetValue(contextName, out var contextPath))
                    requirements.Add(new ContextRequirement(contextName, new NodeOutputBinding(contextPath, []), contextType));
                else Error(CompilerDiagnosticCodes.ContextRequirementSourceInvalid, "Inference context must reference a prior context node.", Current);
            }
            while (Match(","));
        }
        if (Match("->") || Match(":")) declared = ParseType();
        Match(";");
        var type = declared ?? CallableOutput(profile); var path = parentPath + "/" + name;
        if (declared is null) inferredOutputPaths.Add(path);
        var node = new InferenceNode(name, path, profile, template, arguments, [], type, requirements);
        nodeTypes[name] = type; nodePaths[name] = path; AddSpan(path, start, Previous); Ast(); return node;
    }

    private WorkflowNode ParseConditional(string parentPath, FuwenToken start)
    {
        // A condition starts with a binding, so an identifier immediately
        // followed by an operator is not an optional node name. Named
        // conditionals use `if name <binding> <operator> ...`.
        var name = HasConditionalName()
            ? Next().Text
            : "if" + (++conditionalOrdinal).ToString(CultureInfo.InvariantCulture);
        var condition = ParseCondition(); Expect("{"); var path = parentPath + "/" + name;
        var thenNodes = new List<WorkflowNode>(); var saved = new Dictionary<string, FuwenType>(nodeTypes, StringComparer.Ordinal);
        var savedPaths = new Dictionary<string, string>(nodePaths, StringComparer.Ordinal);
        ParseNodes(thenNodes, path + "/$then", false);
        nodeTypes.Clear(); foreach (var pair in saved) nodeTypes[pair.Key] = pair.Value;
        nodePaths.Clear(); foreach (var pair in savedPaths) nodePaths[pair.Key] = pair.Value;
        var elseNodes = new List<WorkflowNode>();
        if (Match("else")) { Expect("{"); ParseNodes(elseNodes, path + "/$else", false); }
        else Error(CompilerDiagnosticCodes.ParseExpectedToken, "Conditional requires an else branch.", Current);
        AddSpan(path, start, Previous); Ast(); return new ConditionalNode(name, path, condition, thenNodes, elseNodes);
    }

    private WorkflowNode? ParseReturn(string parentPath, FuwenToken start, bool allowReturn)
    {
        if (!allowReturn)
        {
            Error(CompilerDiagnosticCodes.ConditionalBranchValueUnsupported, "Branch-local return values are not supported; return after the conditional.", start);
            _ = ParseBinding(); Match(";"); return null;
        }
        var value = ParseBinding(); Match(";"); var path = parentPath + "/return_result";
        AddSpan(path, start, Previous); Ast(); return new ReturnNode("return_result", path, value);
    }

    private ConditionExpression ParseCondition()
    {
        CountExpression();
        if (Match("not")) return new ConditionExpression(ConditionOperator.Not, ParseBinding());
        var left = ParseBinding(); var op = Current.Text switch
        {
            "==" => ConditionOperator.Equal,
            "!=" => ConditionOperator.NotEqual,
            "<" => ConditionOperator.LessThan,
            "<=" => ConditionOperator.LessThanOrEqual,
            ">" => ConditionOperator.GreaterThan,
            ">=" => ConditionOperator.GreaterThanOrEqual,
            "and" => ConditionOperator.And,
            "or" => ConditionOperator.Or,
            "exists" => ConditionOperator.Exists,
            _ => ConditionOperator.Equal,
        };
        if (op == ConditionOperator.Exists) { Next(); return new ConditionExpression(op, left); }
        if (!Match("==") && !Match("!=") && !Match("<") && !Match("<=") && !Match(">") && !Match(">=") && !Match("and") && !Match("or"))
            Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected a supported condition operator.", Current);
        return new ConditionExpression(op, left, ParseBinding());
    }

    private IReadOnlyList<ArgumentBinding> ParseArguments()
    {
        var result = new List<ArgumentBinding>();
        if (!Match("(")) return result;
        while (!AtEnd && Current.Text != ")")
        {
            var name = ReadIdentifier("argument name");
            if (!Match(":") && !Match("=")) Expect(":");
            result.Add(new ArgumentBinding(name, ParseBinding())); CountExpression();
            if (!Match(",")) Expect(";");
        }
        Expect(")");
        return result;
    }

    private Binding ParseBinding()
    {
        CountExpression();
        if (Match("["))
        {
            if (bindingDepth >= budget.MaxNestingDepth)
            {
                Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Binding nesting exceeds the configured limit.", Previous);
                SkipDelimitedBinding("]");
                return NullLiteral();
            }
            bindingDepth++;
            var list = new List<Binding>();
            try
            {
                while (!AtEnd && Current.Text != "]") { list.Add(ParseBinding()); if (!Match(",")) Expect(","); }
                Expect("]");
                return new ListBinding(list);
            }
            finally { bindingDepth--; }
        }
        if (Match("{"))
        {
            if (bindingDepth >= budget.MaxNestingDepth)
            {
                Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Binding nesting exceeds the configured limit.", Previous);
                SkipDelimitedBinding("}");
                return NullLiteral();
            }
            bindingDepth++;
            var values = new Dictionary<string, Binding>(StringComparer.Ordinal);
            try
            {
                while (!AtEnd && Current.Text != "}")
                {
                    var key = ReadText("object property"); Expect(":"); values[key] = ParseBinding();
                    if (!Match(",")) Expect(",");
                }
                Expect("}");
                return new ObjectBinding(values);
            }
            finally { bindingDepth--; }
        }
        if (Current.Kind is FuwenTokenKind.String or FuwenTokenKind.Number || Current.Text is "true" or "false" or "null")
        {
            var raw = Current.Text; Next();
            try { using var document = JsonDocument.Parse(raw); return new LiteralBinding(document.RootElement.Clone()); }
            catch (JsonException) { Error(CompilerDiagnosticCodes.ParseUnexpectedToken, "Invalid literal.", Previous); return NullLiteral(); }
        }
        var name = ReadIdentifier("binding expression"); var projection = new List<string>();
        while (Match(".")) projection.Add(ReadIdentifier("projection field"));
        if (name == "input") return new InputBinding(projection);
        if (!nodeTypes.ContainsKey(name))
            Error(CompilerDiagnosticCodes.BindingReferenceInvalid, "Binding references an unknown name.", Previous, name);
        return new NodeOutputBinding(nodePaths.TryGetValue(name, out var path) ? path : name, projection);
    }

    private static LiteralBinding NullLiteral() => new(JsonDocument.Parse("null").RootElement.Clone());

    private FuwenType ParseType()
    {
        typeDepth++;
        if (typeDepth > budget.MaxSchemaDepth)
        {
            Error(CompilerDiagnosticCodes.BudgetSchemaDepthExceeded, "Schema type depth exceeds the configured limit.", Current);
            typeDepth--;
            return new PrimitiveType(FuwenPrimitiveKind.Json);
        }
        var token = Current; var name = ReadIdentifier("type");
        FuwenType type = name switch
        {
            "string" => new PrimitiveType(FuwenPrimitiveKind.String),
            "bool" or "boolean" => new PrimitiveType(FuwenPrimitiveKind.Boolean),
            "int" or "integer" => new PrimitiveType(FuwenPrimitiveKind.Integer),
            "number" => new PrimitiveType(FuwenPrimitiveKind.Number),
            "duration" => new PrimitiveType(FuwenPrimitiveKind.Duration),
            "json" => new PrimitiveType(FuwenPrimitiveKind.Json),
            "list" or "List" => ParseListType(),
            "artifact" or "Artifact" => ParseArtifactType(),
            _ when schemaAliases.TryGetValue(name, out var descriptor) => new NamedTypeReference(descriptor),
            _ => new NamedTypeReference(ResolveDescriptor(DescriptorKind.Schema, name, token)),
        };
        if (Match("?")) type = new OptionalType(type);
        typeDepth--;
        return type;
    }

    private FuwenType ParseListType()
    {
        Expect("<"); var item = ParseType(); Expect(">");
        var maximum = 100_000;
        if (Match("[")) { maximum = ParsePositiveInt(); Expect("]"); }
        return new ListType(item, maximum);
    }
    private FuwenType ParseArtifactType()
    {
        Expect("<"); var descriptor = ParseDescriptor(DescriptorKind.Artifact, "artifact"); Expect(">");
        return new ArtifactType(descriptor);
    }

    private DescriptorReference ParseDescriptor(DescriptorKind kind, string fallback)
    {
        var token = Current; var text = ReadText(kind.ToString() + " descriptor");
        var name = text; var version = "1"; string? digest = null;
        var hash = text.IndexOf('#');
        if (hash >= 0) { digest = text[(hash + 1)..]; text = text[..hash]; }
        var at = text.LastIndexOf('@');
        if (at > 0) { name = text[..at]; version = text[(at + 1)..]; } else name = text;
        if (Match("@")) version = ReadText("descriptor version");
        if (Match("#")) digest = ReadText("descriptor digest");
        return ResolveDescriptor(kind, string.IsNullOrWhiteSpace(name) ? fallback : name, token, version, digest);
    }

    private DescriptorReference ResolveDescriptor(DescriptorKind kind, string name, FuwenToken token, string version = "1", string? digestText = null)
    {
        if (digestText is not null)
        {
            if (TryParseDescriptorDigest(digestText, out var digest))
                return new DescriptorReference(kind, name, version, digest!);

            Error(
                CompilerDiagnosticCodes.SourceDescriptorUnresolved,
                "Descriptor '" + kind + ":" + name + "@" + version + "' has an invalid exact digest.",
                token);
            return new DescriptorReference(
                kind,
                name.Length == 0 ? "unresolved" : name,
                version,
                new ContentDigest("sha256", "descriptor/v1", new string('0', 64)));
        }
        if (catalogue is InMemoryTrustedCatalogue memory)
        {
            var found = memory.Descriptors.FirstOrDefault(item => item.Descriptor.Kind == kind &&
                item.Descriptor.Name == name && item.Descriptor.Version == version);
            if (found is not null) return found.Descriptor;
        }
        Error(CompilerDiagnosticCodes.SourceDescriptorUnresolved,
            "Descriptor '" + kind + ":" + name + "@" + version + "' requires an exact trusted catalogue identity.", token);
        return new DescriptorReference(kind, name.Length == 0 ? "unresolved" : name, version,
            new ContentDigest("sha256", "descriptor/v1", new string('0', 64)));
    }

    private static bool TryParseDescriptorDigest(string text, out ContentDigest? digest)
    {
        var pieces = text.Split(':', 3);
        var algorithm = pieces.Length == 1 ? "sha256" : pieces.ElementAtOrDefault(0);
        var contract = pieces.Length == 1 ? "descriptor/v1" : pieces.ElementAtOrDefault(1);
        var value = pieces.Length == 1 ? pieces[0] : pieces.ElementAtOrDefault(2);
        var valid = pieces.Length is 1 or 3 &&
            string.Equals(algorithm, "sha256", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(contract) &&
            contract.Length <= 64 &&
            value is { Length: 64 } &&
            value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        digest = valid ? new ContentDigest(algorithm!, contract!, value!) : null;
        return valid;
    }

    private FuwenType CallableOutput(DescriptorReference descriptor)
    {
        if (catalogue is InMemoryTrustedCatalogue memory)
        {
            var found = memory.Descriptors.FirstOrDefault(item => item.Descriptor.Equals(descriptor));
            if (found?.CallableContract is not null) return found.CallableContract.Signature.OutputType;
        }
        return new PrimitiveType(FuwenPrimitiveKind.Json);
    }

    private static IReadOnlyList<WorkflowExecutionRegion> BuildRegions(string root, IReadOnlyList<WorkflowNode> values)
    {
        var result = new List<WorkflowExecutionRegion>();
        AddRegion(root, values, result);
        return result;
    }
    private static void AddRegion(string path, IReadOnlyList<WorkflowNode> values, List<WorkflowExecutionRegion> result)
    {
        result.Add(new WorkflowExecutionRegion(path, values.Select(item => new WorkflowExecutionPhase(new[] { item.StructuralPath })).ToArray()));
        foreach (var conditional in values.OfType<ConditionalNode>())
        {
            AddRegion(path + "/" + conditional.Name + "/$then", conditional.Then, result);
            AddRegion(path + "/" + conditional.Name + "/$else", conditional.Else, result);
        }
    }
    private static int CountNodes(IEnumerable<WorkflowNode> values) => values.Sum(item => 1 + (item is ConditionalNode conditional ? CountNodes(conditional.Then) + CountNodes(conditional.Else) : 0));
    private int ParsePositiveInt()
    {
        var text = ReadText("positive integer");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 100_000;
    }
    private string ReadIdentifier(string expected)
    {
        if (Current.Kind == FuwenTokenKind.Identifier) return Next().Text;
        Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected " + expected + ".", Current);
        if (!AtEnd) Next();
        return "unresolved";
    }
    private string ReadText(string expected)
    {
        if (Current.Kind == FuwenTokenKind.String)
        {
            var raw = Next().Text;
            try { using var document = JsonDocument.Parse(raw); return document.RootElement.GetString() ?? string.Empty; }
            catch (JsonException) { return raw.Trim('"'); }
        }
        return ReadIdentifier(expected);
    }
    private bool Match(string text) { if (Current.Text == text) { Next(); return true; } return false; }
    private bool MatchIdentifier(string text) { if (Current.Kind == FuwenTokenKind.Identifier && Current.Text == text) { Next(); return true; } return false; }
    private bool HasConditionalName()
    {
        if (Current.Kind != FuwenTokenKind.Identifier || Current.Text is "not" or "true" or "false" or "null")
            return false;
        var next = Peek(1);
        if (next.Kind != FuwenTokenKind.Identifier)
            return false;
        return next.Text is not "and" and not "or" and not "exists";
    }
    private FuwenToken Peek(int offset)
    {
        var index = Math.Min(position + offset, tokens.Count - 1);
        return tokens.Count == 0
            ? new FuwenToken(FuwenTokenKind.EndOfFile, string.Empty, new SourceSpan(documentId, 0, 0, 0, 0))
            : tokens[index];
    }
    private void SkipDelimitedBinding(string closing)
    {
        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            var token = Next();
            if (token.Text is "[" or "{") depth++;
            else if (token.Text is "]" or "}") depth--;
        }
        if (depth > 0)
            Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected '" + closing + "'.", Current);
    }
    private void Expect(string text)
    {
        if (Match(text)) return;
        Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected '" + text + "'.", Current);
        if (!AtEnd) Next();
    }
    private FuwenToken Next() { var value = Current; position = Math.Min(position + 1, tokens.Count - 1); return value; }
    private FuwenToken Current => tokens.Count == 0 ? new FuwenToken(FuwenTokenKind.EndOfFile, string.Empty, new SourceSpan(documentId, 0, 0, 0, 0)) : tokens[Math.Min(position, tokens.Count - 1)];
    private FuwenToken Previous => tokens[Math.Max(0, position - 1)];
    private bool AtEnd => Current.Kind == FuwenTokenKind.EndOfFile;
    private void Recover(params string[] starts) { while (!AtEnd && !starts.Contains(Current.Text, StringComparer.Ordinal)) Next(); }
    private WorkflowNode? Unsupported(string keyword, FuwenToken token) { Error(CompilerDiagnosticCodes.ParseUnsupportedConstruct, "Unsupported source construct '" + keyword + "'.", token); return null; }
    private void AddSpan(string path, FuwenToken start, FuwenToken end)
    {
        var startOffset = start.Span.StartUtf8ByteOffset;
        var endOffset = end.Span.StartUtf8ByteOffset + end.Span.Utf8ByteLength;
        sourceEntries.Add(new SourceMapEntry(path, new SourceSpan(documentId, startOffset, Math.Max(0, endOffset - startOffset), start.Span.StartLine, start.Span.StartUtf16Column)));
    }
    private void Error(string code, string message, FuwenToken token, string? actual = null)
    {
        diagnostics.Add(new CompilerDiagnostic(code, DiagnosticSeverity.Error,
            code.StartsWith("FWN-BUDGET", StringComparison.Ordinal) ? DiagnosticPhase.Budget :
            code.StartsWith("FWN-BINDING", StringComparison.Ordinal) ? DiagnosticPhase.Binding : DiagnosticPhase.Parsing,
            message, span: token.Span, actual: actual));
    }
    private void Ast() { astNodes++; if (astNodes == budget.MaxAstNodes + 1) Error(CompilerDiagnosticCodes.BudgetAstNodesExceeded, "AST node count exceeds the configured limit.", Current); }
    private void CountExpression() { expressions++; Ast(); if (expressions == budget.MaxExpressions + 1) Error(CompilerDiagnosticCodes.BudgetExpressionsExceeded, "Expression count exceeds the configured limit.", Current); }
    internal sealed record ParseOutput(
        WorkflowPlan? Plan,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        CompilationUsageSummary Usage,
        IReadOnlyList<SourceMapEntry> SourceEntries,
        IReadOnlySet<string> InferredOutputPaths);
}
