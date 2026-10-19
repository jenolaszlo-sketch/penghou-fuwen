#pragma warning disable RS0016
#pragma warning disable CS1591
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penghou.Fuwen;

namespace Penghou.Fuwen.Compiler;

internal sealed partial class SourceParser
{
    private const int MaximumExpandedGroups = 4096;
    private readonly Dictionary<string, MacroDefinition> macroDefinitions = new(StringComparer.Ordinal);
    private readonly List<WorkflowGroupDefinition> groupDefinitions = [];
    private readonly List<WorkflowGroupMember> groupMembers = [];
    private readonly List<GroupScope> groupStack = [];
    private readonly List<string> macroExpansionStack = [];
    private readonly Dictionary<string, Binding> macroBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> nextGroupOrdinal = new(StringComparer.Ordinal);
    private long macroExpansionWork;

    private void ParseMacroDeclaration(FuwenToken declarationStart)
    {
        var name = ReadIdentifier("macro name");
        var parameters = new List<MacroParameter>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        Expect("(");
        while (!AtEnd && Current.Text != ")")
        {
            var parameterName = ReadIdentifier("macro parameter name");
            Expect(":");
            var type = ParseType();
            if (!parameterNames.Add(parameterName))
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Duplicate macro parameter '{parameterName}'.", Previous);
            if (type is not PrimitiveType { Primitive: FuwenPrimitiveKind.String or FuwenPrimitiveKind.Integer or FuwenPrimitiveKind.Number or FuwenPrimitiveKind.Boolean or FuwenPrimitiveKind.Json })
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, "Macro parameters must use a runtime data type (string, integer, number, boolean, or json).", Previous);
            parameters.Add(new MacroParameter(parameterName, type));
            Ast();
            if (!Match(")") && !Match(",")) Expect(",");
            else if (Previous.Text == ")") break;
        }
        if (Current.Text == ")") Next();
        Expect("{");
        var body = new List<FuwenToken>();
        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            var token = Next();
            if (token.Text == "{") depth++;
            if (token.Text == "}")
            {
                depth--;
                if (depth == 0) break;
            }
            body.Add(token);
        }
        if (depth != 0) Error(CompilerDiagnosticCodes.ParseExpectedToken, "Expected '}' to close macro declaration.", Current);
        if (workflowSeen)
            Error(CompilerDiagnosticCodes.ParseUnexpectedToken, "Macro declarations must precede the workflow declaration.", declarationStart);
        if (macroDefinitions.ContainsKey(name))
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Duplicate macro definition '{name}'.", declarationStart);
        else
        {
            var end = Previous;
            macroDefinitions.Add(name, new MacroDefinition(name, parameters, body.ToArray(),
                new SourceSpan(documentId, declarationStart.Span.StartUtf8ByteOffset,
                    Math.Max(0, end.Span.StartUtf8ByteOffset + end.Span.Utf8ByteLength - declarationStart.Span.StartUtf8ByteOffset),
                    declarationStart.Span.StartLine, declarationStart.Span.StartUtf16Column)));
        }
        Ast();
    }

    private void ParseLogicalGroup(List<WorkflowNode> destination, string parentPath)
    {
        var start = Previous;
        var name = ReadIdentifier("group name");
        var scopePath = parentPath + "/$group/" + name;
        if (groupStack.Count >= Math.Min(budget.MaxNestingDepth, 128))
            Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Group expansion depth exceeds the configured limit.", start);
        AddGroup(scopePath, name, WorkflowGroupKind.Logical, null, groupStack.LastOrDefault()?.Key);
        Expect("{");
        var savedTypes = new Dictionary<string, FuwenType>(nodeTypes, StringComparer.Ordinal);
        var savedPaths = new Dictionary<string, string>(nodePaths, StringComparer.Ordinal);
        Binding? yielded = null;
        FuwenType? yieldedType = null;
        groupStack.Add(new GroupScope(scopePath));
        ParseNodes(destination, scopePath, allowReturn: false, (binding, yieldToken) =>
        {
            if (yielded is not null) Error(CompilerDiagnosticCodes.SemanticValidationFailed, "A group may declare only one yield.", yieldToken);
            else { yielded = binding; yieldedType = ResolveYieldType(binding, yieldToken); }
        });
        groupStack.RemoveAt(groupStack.Count - 1);
        nodeTypes.Clear(); foreach (var pair in savedTypes) nodeTypes[pair.Key] = pair.Value;
        nodePaths.Clear(); foreach (var pair in savedPaths) nodePaths[pair.Key] = pair.Value;
        if (yielded is not null) ExportYield(name, yielded, yieldedType);
    }

    private void ParseMacroInvocation(List<WorkflowNode> destination, string parentPath)
    {
        var start = Previous;
        var invocationName = ReadIdentifier("macro invocation name");
        Expect("=");
        var macroName = ReadIdentifier("macro name");
        var actuals = new Dictionary<string, Binding>(StringComparer.Ordinal);
        Expect("(");
        while (!AtEnd && Current.Text != ")")
        {
            var parameterName = ReadIdentifier("macro argument name");
            if (!Match(":") && !Match("=")) Expect(":");
            var value = ParseBinding();
            if (!actuals.TryAdd(parameterName, value))
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Macro argument '{parameterName}' is supplied more than once.", Previous);
            if (!Match(")") && !Match(",")) Expect(",");
            else if (Previous.Text == ")") break;
        }
        if (Current.Text == ")") Next();
        Match(";");
        var end = Previous;
        if (!macroDefinitions.TryGetValue(macroName, out var macro))
        {
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Unknown macro '{macroName}'.", start);
            return;
        }
        if (macroExpansionStack.Contains(macroName, StringComparer.Ordinal))
        {
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Recursive macro expansion involving '{macroName}' is not supported.", start);
            return;
        }
        var maximumDepth = Math.Min(budget.MaxNestingDepth, 128);
        if (macroExpansionStack.Count >= maximumDepth || groupStack.Count >= maximumDepth)
        {
            Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Macro expansion depth exceeds the configured limit.", start);
            return;
        }
        if (macro.Body.Count > budget.MaxAstNodes - macroExpansionWork)
        {
            Error(CompilerDiagnosticCodes.BudgetAstNodesExceeded, "Expanded macro parse work exceeds the configured AST-node budget.", start);
            return;
        }
        macroExpansionWork += macro.Body.Count;
        foreach (var parameter in macro.Parameters)
        {
            if (!actuals.ContainsKey(parameter.Name))
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Macro '{macroName}' is missing argument '{parameter.Name}'.", start);
        }
        foreach (var actual in actuals.Keys)
        {
            if (!macro.Parameters.Any(parameter => string.Equals(parameter.Name, actual, StringComparison.Ordinal)))
                Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Macro '{macroName}' has no parameter '{actual}'.", start);
        }
        if (diagnostics.ToImmutable().HasErrors) return;
        foreach (var parameter in macro.Parameters)
            ValidateMacroArgument(parameter, actuals[parameter.Name], start);

        var scopePath = parentPath + "/$group/" + invocationName;
        AddGroup(scopePath, invocationName, WorkflowGroupKind.Macro,
            new WorkflowMacroProvenance(macroName, GetMacroFingerprint(macro, new HashSet<string>(StringComparer.Ordinal)), scopePath,
                macro.DefinitionSpan, Span(start, end)), groupStack.LastOrDefault()?.Key);
        var savedTypes = new Dictionary<string, FuwenType>(nodeTypes, StringComparer.Ordinal);
        var savedPaths = new Dictionary<string, string>(nodePaths, StringComparer.Ordinal);
        var savedTokens = tokens;
        var savedPosition = position;
        var savedBindings = macroBindings.ToArray();
        var savedFanOutItem = fanOutItemName;
        var savedLoopState = loopStateName;
        var savedLoopIteration = loopIterationName;
        // Each macro receives only its explicitly bound parameters. Nested calls
        // can pass those bindings onward, but cannot capture the enclosing scope.
        macroBindings.Clear(); foreach (var pair in actuals) macroBindings[pair.Key] = pair.Value;
        nodeTypes.Clear();
        nodePaths.Clear();
        fanOutItemName = null;
        loopStateName = null;
        loopIterationName = null;
        macroExpansionStack.Add(macroName);
        groupStack.Add(new GroupScope(scopePath));
        Binding? yielded = null;
        FuwenType? yieldedType = null;
        var invocationTokens = macro.Body.Append(new FuwenToken(FuwenTokenKind.Symbol, "}", end.Span))
            .Append(new FuwenToken(FuwenTokenKind.EndOfFile, string.Empty, end.Span)).ToArray();
        tokens = invocationTokens;
        position = 0;
        ParseNodes(destination, scopePath, allowReturn: false, (binding, yieldToken) =>
        {
            if (yielded is not null) Error(CompilerDiagnosticCodes.SemanticValidationFailed, "A macro body may declare only one yield.", yieldToken);
            else { yielded = binding; yieldedType = ResolveYieldType(binding, yieldToken); }
        });
        position = savedPosition;
        tokens = savedTokens;
        groupStack.RemoveAt(groupStack.Count - 1);
        macroExpansionStack.RemoveAt(macroExpansionStack.Count - 1);
        macroBindings.Clear(); foreach (var pair in savedBindings) macroBindings[pair.Key] = pair.Value;
        fanOutItemName = savedFanOutItem;
        loopStateName = savedLoopState;
        loopIterationName = savedLoopIteration;
        nodeTypes.Clear(); foreach (var pair in savedTypes) nodeTypes[pair.Key] = pair.Value;
        nodePaths.Clear(); foreach (var pair in savedPaths) nodePaths[pair.Key] = pair.Value;
        if (yielded is null)
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Macro '{macroName}' must explicitly yield a node.", start);
        else ExportYield(invocationName, yielded, yieldedType);
        Ast();
    }

    private void ValidateMacroArgument(MacroParameter parameter, Binding value, FuwenToken at)
    {
        if (parameter.Type is not PrimitiveType expected) return;
        var actual = value switch
        {
            LiteralBinding literal => literal.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => FuwenPrimitiveKind.String,
                System.Text.Json.JsonValueKind.Number when literal.Value.TryGetInt64(out _) => FuwenPrimitiveKind.Integer,
                System.Text.Json.JsonValueKind.Number => FuwenPrimitiveKind.Number,
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => FuwenPrimitiveKind.Boolean,
                _ => FuwenPrimitiveKind.Json,
            },
            NodeOutputBinding output when output.Projection.Count == 0 => nodePaths
                .Where(pair => string.Equals(pair.Value, output.NodePath, StringComparison.Ordinal))
                .Select(pair => nodeTypes[pair.Key]).OfType<PrimitiveType>().Select(type => (FuwenPrimitiveKind?)type.Primitive).FirstOrDefault(),
            InputBinding input when input.Projection.Count == 0 => (inputType as PrimitiveType)?.Primitive,
            _ => null,
        };
        if (actual is not null && actual != expected.Primitive && expected.Primitive != FuwenPrimitiveKind.Json)
            Error(CompilerDiagnosticCodes.BindingTypeMismatch, $"Macro parameter '{parameter.Name}' expects '{expected.Primitive}' but receives '{actual}'.", at);
    }

    private FuwenType? ResolveYieldType(Binding binding, FuwenToken at)
    {
        if (binding is not NodeOutputBinding { Projection.Count: 0 } output)
        {
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, "A group or macro yield must be a direct node output without projection.", at);
            return null;
        }
        var type = nodePaths.Where(pair => string.Equals(pair.Value, output.NodePath, StringComparison.Ordinal))
            .Select(pair => nodeTypes[pair.Key]).FirstOrDefault();
        if (type is null)
        {
            Error(CompilerDiagnosticCodes.BindingReferenceInvalid, "Yield must reference a node declared in the current scope.", at);
            return null;
        }
        return type;
    }

    private void ExportYield(string alias, Binding binding, FuwenType? type)
    {
        if (binding is not NodeOutputBinding { Projection.Count: 0 } output || type is null) return;
        if (nodeTypes.ContainsKey(alias))
        {
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Group or macro result name '{alias}' is already defined in this scope.", Current);
            return;
        }
        nodeTypes[alias] = type;
        nodePaths[alias] = output.NodePath;
    }

    private Binding AppendProjection(Binding binding, IReadOnlyList<string> projection, FuwenToken at)
    {
        if (projection.Count == 0) return binding;
        return binding switch
        {
            InputBinding input => new InputBinding(input.Projection.Concat(projection).ToArray()),
            NodeOutputBinding output => new NodeOutputBinding(output.NodePath, output.Projection.Concat(projection).ToArray()),
            FanOutItemValueBinding item => new FanOutItemValueBinding(item.Projection.Concat(projection).ToArray()),
            LoopStateBinding state => new LoopStateBinding(state.Projection.Concat(projection).ToArray()),
            LoopIterationBinding iteration => new LoopIterationBinding(iteration.Projection.Concat(projection).ToArray()),
            _ => InvalidProjection(binding, at),
        };
    }

    private Binding InvalidProjection(Binding binding, FuwenToken at)
    {
        Error(CompilerDiagnosticCodes.BindingReferenceInvalid, "A macro parameter projection requires a value binding.", at);
        return binding;
    }

    private void AddGroup(string key, string displayName, WorkflowGroupKind kind, WorkflowMacroProvenance? provenance, string? parent)
    {
        if (Encoding.UTF8.GetByteCount(key) > 512)
        {
            Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded, "Group scope identity exceeds its 512-byte limit.", Current);
            return;
        }
        if (groupDefinitions.Count >= MaximumExpandedGroups)
        {
            if (groupDefinitions.Count == MaximumExpandedGroups)
                Error(CompilerDiagnosticCodes.BudgetAstNodesExceeded, "Expanded group count exceeds the 4096 group limit.", Current);
            return;
        }
        var ordinalKey = parent ?? string.Empty;
        nextGroupOrdinal.TryGetValue(ordinalKey, out var ordinal);
        nextGroupOrdinal[ordinalKey] = ordinal + 1;
        if (groupDefinitions.Any(group => string.Equals(group.GroupKey, key, StringComparison.Ordinal)))
        {
            Error(CompilerDiagnosticCodes.SemanticValidationFailed, $"Duplicate group scope '{key}'.", Current);
            return;
        }
        groupDefinitions.Add(new WorkflowGroupDefinition(key, parent, kind, displayName, ordinal, provenance));
    }

    private void RecordGroupMember(string nodePath)
    {
        if (groupStack.Count == 0) return;
        groupMembers.Add(new WorkflowGroupMember(nodePath, groupStack[^1].Key));
    }

    private string GetMacroFingerprint(MacroDefinition macro, ISet<string> visiting)
    {
        if (macro.Fingerprint is not null) return macro.Fingerprint;
        if (visiting.Count >= Math.Min(budget.MaxNestingDepth, 128))
        {
            Error(CompilerDiagnosticCodes.BudgetNestingDepthExceeded,
                "Macro definition fingerprint expansion depth exceeds the configured limit.", Current);
            return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("depth-limit:" + macro.Name))).ToLowerInvariant();
        }
        if (!visiting.Add(macro.Name))
            return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("recursive:" + macro.Name))).ToLowerInvariant();
        var content = new StringBuilder();
        content.Append("macro:").Append(macro.Name).Append('\n');
        foreach (var parameter in macro.Parameters)
            content.Append("parameter:").Append(parameter.Name).Append(':').Append(parameter.Type).Append('\n');
        foreach (var token in macro.Body)
            content.Append((int)token.Kind).Append(':').Append(token.Text.Length).Append(':').Append(token.Text).Append('\n');
        foreach (var nestedName in FindNestedMacroNames(macro.Body))
        {
            if (!macroDefinitions.TryGetValue(nestedName, out var nested))
            {
                content.Append("unresolved:").Append(nestedName).Append('\n');
                continue;
            }
            content.Append("nested:").Append(nestedName).Append(':').Append(GetMacroFingerprint(nested, visiting)).Append('\n');
        }
        visiting.Remove(macro.Name);
        macro.Fingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))).ToLowerInvariant();
        return macro.Fingerprint;
    }

    private static IEnumerable<string> FindNestedMacroNames(IReadOnlyList<FuwenToken> body)
    {
        for (var index = 0; index + 3 < body.Count; index++)
        {
            if (body[index].Text == "use" && body[index + 1].Kind == FuwenTokenKind.Identifier && body[index + 2].Text == "=" && body[index + 3].Kind == FuwenTokenKind.Identifier)
                yield return body[index + 3].Text;
        }
    }

    private SourceSpan Span(FuwenToken start, FuwenToken end) => new(documentId,
        start.Span.StartUtf8ByteOffset,
        Math.Max(0, end.Span.StartUtf8ByteOffset + end.Span.Utf8ByteLength - start.Span.StartUtf8ByteOffset),
        start.Span.StartLine, start.Span.StartUtf16Column);

    private sealed record MacroParameter(string Name, FuwenType Type);
    private sealed class MacroDefinition(string name, IReadOnlyList<MacroParameter> parameters, IReadOnlyList<FuwenToken> body, SourceSpan definitionSpan)
    {
        internal string Name { get; } = name;
        internal IReadOnlyList<MacroParameter> Parameters { get; } = parameters;
        internal IReadOnlyList<FuwenToken> Body { get; } = body;
        internal SourceSpan DefinitionSpan { get; } = definitionSpan;
        internal string? Fingerprint { get; set; }
    }
    private sealed record GroupScope(string Key);
}
