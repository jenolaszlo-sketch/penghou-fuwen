using System.Security.Cryptography;
using System.Text;

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
        var index = 0;
        while (true)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
                return placeholders;
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
            var name = template[start..cursor];
            while (cursor < template.Length && char.IsWhiteSpace(template[cursor]))
                cursor++;
            if (name.Length == 0 ||
                !template.AsSpan(cursor).StartsWith("}}", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Malformed prompt placeholder starting at offset {open}; " +
                    "placeholders use '{{ name }}' with an identifier name.",
                    nameof(template));
            }
            placeholders.Add(name);
            index = cursor + 2;
        }
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
                    errors.Add($"Prompt '{definition.Name}' references undeclared parameter '{{{{ {placeholder} }}}}'." );
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
