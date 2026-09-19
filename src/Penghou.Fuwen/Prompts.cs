using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Fuwen;

/// <summary>Message roles for workflow-owned prompt declarations.</summary>
public enum PromptMessageRole
{
    /// <summary>System instruction establishing the model's role and constraints.</summary>
    System,
    /// <summary>User message carrying the task, usually with bound parameters.</summary>
    User,
}

/// <summary>A typed prompt parameter bound at the inference node.</summary>
public sealed record PromptParameter(string Name, FuwenType Type);

/// <summary>One prompt message: a role plus a template with typed placeholders.</summary>
public sealed record PromptMessage(PromptMessageRole Role, string Template);

/// <summary>A binding of one prompt parameter to a workflow value.</summary>
public sealed record PromptBinding(string ParameterName, Binding Value);

/// <summary>
/// A workflow-owned prompt declaration: named typed parameters plus either
/// inline ordered messages whose <c>{{ name }}</c> placeholders reference
/// only declared parameters, or an explicit registered prompt source whose
/// semantics live in the referenced trusted descriptor. The definition is
/// part of workflow semantics and carries its own content digest; rendering
/// with bindings is a host responsibility.
/// </summary>
public sealed record PromptDefinition(
    string Name,
    IReadOnlyList<PromptParameter> Parameters,
    IReadOnlyList<PromptMessage> Messages,
    DescriptorReference? RegisteredSource = null)
{
    /// <summary>The semantic content contract for prompt definitions.</summary>
    public const string SemanticDigestContract = "prompt-definition/v1";

    /// <summary>
    /// Computes the self-describing semantic digest over the canonical
    /// definition: name, parameters in declaration order with normalized
    /// types, messages in order, and the registered source identity when the
    /// definition aliases a host prompt. Any wording, structure, binding-shape,
    /// or source change yields a different digest.
    /// </summary>
    public string GetSemanticDigest()
    {
        var canonical = CanonicalJson.Serialize(new PromptDefinitionCanonicalForm(
            Name,
            Parameters.Select(parameter => new PromptParameterCanonicalForm(
                parameter.Name,
                Encoding.UTF8.GetString(CanonicalJson.Serialize(parameter.Type)))).ToArray(),
            Messages.Select(message => new PromptMessageCanonicalForm(
                message.Role.ToString(),
                message.Template)).ToArray(),
            RegisteredSource is null
                ? null
                : Encoding.UTF8.GetString(CanonicalJson.Serialize(RegisteredSource))));
        var hash = SHA256.HashData(canonical);
        return $"sha256:{SemanticDigestContract}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// Extracts placeholder names from a template in order of appearance.
    /// A placeholder is <c>{{</c>, optional whitespace, an identifier,
    /// optional whitespace, <c>}}</c>. Throws on malformed placeholders.
    /// </summary>
    public static IReadOnlyList<string> GetPlaceholders(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var placeholders = new List<string>();
        var offset = 0;
        while (true)
        {
            var open = template.IndexOf("{{", offset, StringComparison.Ordinal);
            if (open < 0)
                return placeholders;
            if (!TryReadPlaceholderAt(template, open, out var name, out var end))
                throw new ArgumentException(
                    $"Malformed prompt placeholder starting at offset {open}; " +
                    "placeholders use '{{ name }}' with an identifier name.",
                    nameof(template));
            placeholders.Add(name);
            offset = end;
        }
    }

    internal static bool TryReadPlaceholderAt(string template, int open, out string name, out int end)
    {
        name = string.Empty;
        end = open;
        var cursor = open + 2;
        while (cursor < template.Length && char.IsWhiteSpace(template[cursor]))
            cursor++;
        var start = cursor;
        if (cursor < template.Length &&
            (char.IsLetter(template[cursor]) || template[cursor] is '_' or '$'))
        {
            cursor++;
            while (cursor < template.Length &&
                (char.IsLetterOrDigit(template[cursor]) || template[cursor] is '_' or '$'))
            {
                cursor++;
            }
        }
        name = template[start..cursor];
        while (cursor < template.Length && char.IsWhiteSpace(template[cursor]))
            cursor++;
        if (name.Length == 0 || !template.AsSpan(cursor).StartsWith("}}", StringComparison.Ordinal))
            return false;
        end = cursor + 2;
        return true;
    }

    /// <summary>
    /// Validates the definition shape, returning one error per violation.
    /// Callers decide how to report (parser diagnostics or validator exceptions).
    /// </summary>
    public static IReadOnlyList<string> ValidateDefinition(PromptDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Name))
            errors.Add("Prompt definition requires a name.");
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                errors.Add($"Prompt '{definition.Name}' has a parameter without a name.");
            else if (!parameterNames.Add(parameter.Name))
                errors.Add($"Prompt '{definition.Name}' declares duplicate parameter '{parameter.Name}'.");
            if (parameter.Type is null)
                errors.Add($"Prompt '{definition.Name}' parameter '{parameter.Name}' requires a type.");
        }
        if (definition.RegisteredSource is not null)
        {
            if (definition.Messages.Count != 0)
                errors.Add($"Prompt '{definition.Name}' aliases a registered prompt and must not declare inline messages.");
        }
        else if (definition.Messages.Count == 0)
        {
            errors.Add($"Prompt '{definition.Name}' requires at least one message.");
        }
        foreach (var message in definition.Messages)
        {
            if (!Enum.IsDefined(message.Role))
                errors.Add($"Prompt '{definition.Name}' has a message with an undefined role.");
            if (message.Template is null)
            {
                errors.Add($"Prompt '{definition.Name}' has a message without template text.");
                continue;
            }
            IReadOnlyList<string> placeholders;
            try
            {
                placeholders = GetPlaceholders(message.Template);
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Prompt '{definition.Name}': {exception.Message}");
                continue;
            }
            foreach (var placeholder in placeholders)
            {
                if (!parameterNames.Contains(placeholder))
                    errors.Add($"Prompt '{definition.Name}' references undeclared parameter '{{{{ {placeholder} }}}}'.");
            }
        }
        return errors;
    }

    private sealed record PromptDefinitionCanonicalForm(
        string Name,
        IReadOnlyList<PromptParameterCanonicalForm> Parameters,
        IReadOnlyList<PromptMessageCanonicalForm> Messages,
        string? RegisteredSource);

    private sealed record PromptParameterCanonicalForm(string Name, string Type);

    private sealed record PromptMessageCanonicalForm(string Role, string Template);
}

/// <summary>A prompt message rendered with bound values, ready for a model request.</summary>
public sealed record RenderedPromptMessage(PromptMessageRole Role, string Text);

/// <summary>
/// Deterministically renders workflow-owned prompt definitions with evaluated
/// bindings. Substitution is single-pass: replacement text is never rescanned,
/// so bound values containing placeholder-like text are preserved verbatim.
/// </summary>
public static class PromptRenderer
{
    /// <summary>
    /// Renders every message of <paramref name="definition"/> using
    /// <paramref name="values"/> keyed by parameter name. Every required
    /// parameter must be present; omitted optional parameters render as empty
    /// strings; extra values are ignored.
    /// </summary>
    public static IReadOnlyList<RenderedPromptMessage> Render(
        PromptDefinition definition,
        IReadOnlyDictionary<string, RuntimeValue> values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        var renderedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            if (values.TryGetValue(parameter.Name, out var value))
            {
                renderedValues[parameter.Name] = RenderValue(value);
            }
            else if (parameter.Type is OptionalType)
            {
                renderedValues[parameter.Name] = string.Empty;
            }
            else
            {
                throw new ArgumentException(
                    $"Prompt '{definition.Name}' requires a value for parameter '{parameter.Name}'.",
                    nameof(values));
            }
        }
        var messages = new List<RenderedPromptMessage>(definition.Messages.Count);
        foreach (var message in definition.Messages)
            messages.Add(new RenderedPromptMessage(
                message.Role, RenderTemplate(definition.Name, message.Template, renderedValues)));
        return messages;
    }

    private static string RenderTemplate(
        string promptName,
        string template,
        IReadOnlyDictionary<string, string> renderedValues)
    {
        var builder = new StringBuilder(template.Length);
        var offset = 0;
        while (offset < template.Length)
        {
            var open = template.IndexOf("{{", offset, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(template, offset, template.Length - offset);
                break;
            }
            if (!PromptDefinition.TryReadPlaceholderAt(template, open, out var name, out var end))
                throw new ArgumentException(
                    $"Prompt '{promptName}' has a malformed placeholder starting at offset {open}.",
                    nameof(template));
            if (!renderedValues.TryGetValue(name, out var replacement))
                throw new ArgumentException(
                    $"Prompt '{promptName}' references unbound parameter '{{{{ {name} }}}}'.",
                    nameof(template));
            builder.Append(template, offset, open - offset);
            builder.Append(replacement);
            offset = end;
        }
        return builder.ToString();
    }

    /// <summary>The semantic content contract for rendered prompt instances.</summary>
    public const string RenderedDigestContract = "rendered-prompt/v1";

    /// <summary>
    /// Computes the content digest of rendered messages for runtime evidence:
    /// roles and text joined deterministically, so the same rendering always
    /// yields the same digest without storing prompt text.
    /// </summary>
    public static string GetRenderedDigest(IReadOnlyList<RenderedPromptMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            builder.Append(message.Role.ToString());
            builder.Append('\n');
            builder.Append(message.Text);
            builder.Append('\n');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"sha256:{RenderedDigestContract}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// Renders one bound value for prompt prose: strings contribute raw text,
    /// every other value contributes its canonical JSON representation.
    /// </summary>
    public static string RenderValue(RuntimeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = value is JsonRuntimeValue json
            ? json.Value
            : RuntimeValueJson.ToJsonElement(value);
        if (element.ValueKind == JsonValueKind.String && element.GetString() is string text)
            return text;
        return Encoding.UTF8.GetString(CanonicalJson.Canonicalize(element));
    }

}
