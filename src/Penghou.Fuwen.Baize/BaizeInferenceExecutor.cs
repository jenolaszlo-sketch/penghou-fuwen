using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Baize;
using Penghou.Fuwen.Compiler;
using Penghou.Nuwa;

namespace Penghou.Fuwen.Baize;

/// <summary>Selects how an admitted inference output is obtained from Baize.</summary>
public enum BaizeInferenceOutputMode
{
    /// <summary>Read JSON structured output from the completion content.</summary>
    StructuredContent,
    /// <summary>Read JSON arguments from one declared model tool call.</summary>
    ToolCall,
}

/// <summary>A host-owned Baize endpoint binding in fallback order.</summary>
public sealed class BaizeEndpointBinding
{
    /// <summary>Creates an exact endpoint binding for a configured Baize client.</summary>
    public BaizeEndpointBinding(string endpointId, string provider, string model, ILlmClient client)
    {
        EndpointId = BaizeBindingValidation.Text(endpointId, nameof(endpointId), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Provider = BaizeBindingValidation.Text(provider, nameof(provider), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Model = BaizeBindingValidation.Text(model, nameof(model), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Host-assigned endpoint identity.</summary>
    public string EndpointId { get; }
    /// <summary>Configured provider identity.</summary>
    public string Provider { get; }
    /// <summary>Configured model identity.</summary>
    public string Model { get; }
    /// <summary>The provider-neutral Baize client.</summary>
    public ILlmClient Client { get; }

}

/// <summary>A host-owned model-visible tool and its exact Fuwen descriptor.</summary>
public sealed class BaizeToolBinding
{
    /// <summary>Creates a model tool binding.</summary>
    public BaizeToolBinding(DescriptorReference descriptor, LlmTool tool)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind != DescriptorKind.Tool)
            throw new ArgumentException("Tool bindings require a Tool descriptor.", nameof(descriptor));
        Descriptor = descriptor;
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("A Baize tool must have a name.", nameof(tool));
    }

    /// <summary>The exact admitted tool descriptor.</summary>
    public DescriptorReference Descriptor { get; }
    /// <summary>The normalized Baize tool declaration.</summary>
    public LlmTool Tool { get; }
}

/// <summary>Trusted host policy for one logical inference profile.</summary>
public sealed class BaizeInferencePolicy
{
    /// <summary>The largest total number of adapter attempts permitted.</summary>
    public const int MaximumAllowedAttempts = 8;

    /// <summary>Creates a bounded trusted policy.</summary>
    public BaizeInferencePolicy(
        int maximumAttempts = 1,
        int? maximumTokens = null,
        bool retryRepresentationFailures = false,
        string? policyRevision = null,
        string? routingPolicyRevision = null,
        Func<InferenceExecutionRequest, string?>? rejection = null)
    {
        if (maximumAttempts < 1 || maximumAttempts > MaximumAllowedAttempts)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (maximumTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        MaximumAttempts = maximumAttempts;
        MaximumTokens = maximumTokens;
        RetryRepresentationFailures = retryRepresentationFailures;
        PolicyRevision = BaizeBindingValidation.OptionalText(policyRevision, nameof(policyRevision), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        RoutingPolicyRevision = BaizeBindingValidation.OptionalText(routingPolicyRevision, nameof(routingPolicyRevision), InferenceExecutionEvidence.MaximumIdentityUtf8Bytes);
        Rejection = rejection;
    }

    /// <summary>Maximum total calls across all trusted fallbacks.</summary>
    public int MaximumAttempts { get; }
    /// <summary>Trusted upper bound for output tokens.</summary>
    public int? MaximumTokens { get; }
    /// <summary>Whether malformed/schema/truncated results may be retried.</summary>
    public bool RetryRepresentationFailures { get; }
    /// <summary>Host policy identity recorded in evidence.</summary>
    public string? PolicyRevision { get; }
    /// <summary>Host routing policy identity recorded in evidence.</summary>
    public string? RoutingPolicyRevision { get; }
    /// <summary>Optional host admission predicate; a non-null message rejects.</summary>
    public Func<InferenceExecutionRequest, string?>? Rejection { get; }

}

/// <summary>
/// Exact host-owned binding between Fuwen logical descriptors and Baize
/// endpoints. Profile and template names never become Baize request data.
/// </summary>
public sealed class BaizeInferenceBinding
{
    /// <summary>Creates a descriptor-bound inference binding.</summary>
    public BaizeInferenceBinding(
        DescriptorReference profile,
        DescriptorReference promptTemplate,
        IReadOnlyList<BaizeEndpointBinding> endpoints,
        string userPromptTemplate = "{arguments}",
        string? systemPrompt = null,
        BaizeInferenceOutputMode outputMode = BaizeInferenceOutputMode.StructuredContent,
        string? expectedToolName = null,
        IReadOnlyList<BaizeToolBinding>? tools = null,
        IReadOnlyList<ResolvedSchemaDefinition>? schemas = null,
        BaizeInferencePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(promptTemplate);
        if (profile.Kind != DescriptorKind.InferenceProfile)
            throw new ArgumentException("Inference bindings require an InferenceProfile descriptor.", nameof(profile));
        if (promptTemplate.Kind != DescriptorKind.PromptTemplate)
            throw new ArgumentException("Inference bindings require a PromptTemplate descriptor.", nameof(promptTemplate));
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0 || endpoints.Count > BaizeInferencePolicy.MaximumAllowedAttempts)
            throw new ArgumentOutOfRangeException(nameof(endpoints));
        var endpointCopy = endpoints.Select(endpoint => endpoint ?? throw new ArgumentException("Endpoint bindings cannot be null.", nameof(endpoints))).ToArray();
        if (endpointCopy.Select(endpoint => endpoint.EndpointId).Distinct(StringComparer.Ordinal).Count() != endpointCopy.Length)
            throw new ArgumentException("Endpoint ids must be unique within a binding.", nameof(endpoints));
        if (!Enum.IsDefined(outputMode)) throw new ArgumentOutOfRangeException(nameof(outputMode));
        if (outputMode == BaizeInferenceOutputMode.ToolCall && string.IsNullOrWhiteSpace(expectedToolName))
            throw new ArgumentException("Tool-call output requires an expected tool name.", nameof(expectedToolName));
        if (outputMode == BaizeInferenceOutputMode.StructuredContent && expectedToolName is not null)
            throw new ArgumentException("Structured-content output cannot specify an expected tool.", nameof(expectedToolName));

        var toolCopy = (tools ?? Array.Empty<BaizeToolBinding>()).ToArray();
        if (toolCopy.Any(tool => tool is null)) throw new ArgumentException("Tool bindings cannot be null.", nameof(tools));
        if (toolCopy.Select(tool => tool.Tool.Name).Distinct(StringComparer.Ordinal).Count() != toolCopy.Length)
            throw new ArgumentException("Tool names must be unique within a binding.", nameof(tools));
        if (outputMode == BaizeInferenceOutputMode.ToolCall && !toolCopy.Any(tool => string.Equals(tool.Tool.Name, expectedToolName, StringComparison.Ordinal)))
            throw new ArgumentException("The expected tool must be declared in the tool bindings.", nameof(expectedToolName));

        Profile = profile;
        PromptTemplate = promptTemplate;
        Endpoints = Array.AsReadOnly(endpointCopy);
        UserPromptTemplate = userPromptTemplate ?? throw new ArgumentNullException(nameof(userPromptTemplate));
        SystemPrompt = systemPrompt;
        OutputMode = outputMode;
        ExpectedToolName = expectedToolName;
        Tools = Array.AsReadOnly(toolCopy);
        Schemas = Array.AsReadOnly((schemas ?? Array.Empty<ResolvedSchemaDefinition>()).ToArray());
        Policy = policy ?? new BaizeInferencePolicy();
    }

    /// <summary>The exact admitted logical inference profile descriptor.</summary>
    public DescriptorReference Profile { get; }
    /// <summary>The exact admitted prompt-template descriptor.</summary>
    public DescriptorReference PromptTemplate { get; }
    /// <summary>Trusted endpoint bindings in host-selected fallback order.</summary>
    public IReadOnlyList<BaizeEndpointBinding> Endpoints { get; }
    /// <summary>Host-owned user prompt with optional argument placeholders.</summary>
    public string UserPromptTemplate { get; }
    /// <summary>Optional host-owned system prompt.</summary>
    public string? SystemPrompt { get; }
    /// <summary>Output extraction mode.</summary>
    public BaizeInferenceOutputMode OutputMode { get; }
    /// <summary>The required tool name for <see cref="BaizeInferenceOutputMode.ToolCall"/>.</summary>
    public string? ExpectedToolName { get; }
    /// <summary>Trusted model-visible tools.</summary>
    public IReadOnlyList<BaizeToolBinding> Tools { get; }
    /// <summary>Resolved Fuwen schemas used for final typed validation.</summary>
    public IReadOnlyList<ResolvedSchemaDefinition> Schemas { get; }
    /// <summary>Trusted retry, token, and admission policy.</summary>
    public BaizeInferencePolicy Policy { get; }
}

/// <summary>Receives detached provider-neutral inference evidence.</summary>
public interface IBaizeInferenceProvenanceSink
{
    /// <summary>Records one completed or failed inference evidence record.</summary>
    ValueTask RecordAsync(InferenceExecutionEvidence evidence, CancellationToken cancellationToken = default);
}

/// <summary>Executes admitted Fuwen inference requests through Baize.</summary>
public sealed class BaizeInferenceExecutor : IInferenceExecutor
{
    private readonly IReadOnlyList<BaizeInferenceBinding> bindings;
    private readonly IJsonRepairPipeline repairPipeline;
    private readonly IBaizeInferenceProvenanceSink? provenanceSink;

    /// <summary>Creates an adapter with deterministic Nuwa repair defaults.</summary>
    public BaizeInferenceExecutor(
        IReadOnlyList<BaizeInferenceBinding> bindings,
        IJsonRepairPipeline? repairPipeline = null,
        IBaizeInferenceProvenanceSink? provenanceSink = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0) throw new ArgumentException("At least one inference binding is required.", nameof(bindings));
        this.bindings = Array.AsReadOnly(bindings.Select(binding => binding ?? throw new ArgumentException("Bindings cannot contain null values.", nameof(bindings))).ToArray());
        if (this.bindings.Select(binding => (binding.Profile, binding.PromptTemplate)).Distinct().Count() != this.bindings.Count)
            throw new ArgumentException("Profile and prompt-template descriptor pairs must be unique.", nameof(bindings));
        this.repairPipeline = repairPipeline ?? JsonRepairPipeline.Create();
        this.provenanceSink = provenanceSink;
    }

    /// <inheritdoc />
    public async ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = bindings.FirstOrDefault(candidate => candidate.Profile == request.Profile && candidate.PromptTemplate == request.PromptTemplate);
        if (binding is null)
            return InferenceExecutionResult.Failed(new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.DescriptorUnavailable, "No exact Baize binding matched the admitted profile and prompt template."));

        var started = Stopwatch.GetTimestamp();
        string? policyMessage;
        try
        {
            policyMessage = binding.Policy.Rejection?.Invoke(request);
        }
        catch (Exception exception)
        {
            policyMessage = $"The trusted inference policy failed: {exception.GetType().Name}.";
        }
        if (policyMessage is not null)
        {
            var failure = new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected, BoundMessage(policyMessage));
            var evidence = BuildEvidence(request, binding, [], null, wasRepaired: false, repairAttempts: 0, repairStrategy: null, started,
                promptTokens: null, completionTokens: null, totalTokens: null);
            await RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
            return InferenceExecutionResult.Failed(failure, evidence);
        }

        var attempts = new List<InferenceAttemptEvidence>();
        LlmResponse? lastResponse = null;
        ExecutionFailure? lastFailure = null;
        var anyWasRepaired = false;
        var repairAttempts = 0;
        string? repairStrategy = null;
        var promptTokens = 0;
        var completionTokens = 0;
        var totalTokens = 0;
        var hasPromptTokens = false;
        var hasCompletionTokens = false;
        var hasTotalTokens = false;
        for (var attempt = 1; attempt <= binding.Policy.MaximumAttempts; attempt++)
        {
            var endpoint = binding.Endpoints[(attempt - 1) % binding.Endpoints.Count];
            try
            {
                var metadata = Metadata(endpoint);
                var requestToProvider = CreateRequest(request, binding, endpoint, out var schemaJson);
                var requirements = LlmRequestRequirements.From(requestToProvider);
                if (!requirements.IsSatisfiedBy(endpoint.Client.Capabilities, out var capabilityReason))
                {
                    lastFailure = new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected, BoundMessage($"Endpoint '{endpoint.EndpointId}' does not satisfy the trusted inference requirements: {capabilityReason}"));
                    attempts.Add(FailedAttempt(attempt, metadata, endpoint, lastFailure.Code.ToString(), lastFailure.Message));
                    if (attempt == binding.Policy.MaximumAttempts) break;
                    continue;
                }

                lastResponse = await endpoint.Client.CompleteAsync(requestToProvider, cancellationToken).ConfigureAwait(false);
                AddUsage(lastResponse.Usage?.PromptTokens, ref promptTokens, ref hasPromptTokens);
                AddUsage(lastResponse.Usage?.CompletionTokens, ref completionTokens, ref hasCompletionTokens);
                AddUsage(lastResponse.Usage?.TotalTokens, ref totalTokens, ref hasTotalTokens);
                var extracted = await ExtractOutputAsync(lastResponse, request.OutputType, binding, schemaJson, cancellationToken).ConfigureAwait(false);
                anyWasRepaired |= extracted.WasRepaired;
                repairAttempts = Math.Min(InferenceExecutionEvidence.MaximumAttempts, repairAttempts + extracted.RepairAttempts);
                repairStrategy = extracted.RepairStrategy ?? repairStrategy;
                if (!extracted.Succeeded)
                {
                    lastFailure = extracted.Failure!;
                    attempts.Add(FailedAttempt(attempt, metadata, endpoint, lastFailure.Code.ToString(), lastFailure.Message));
                    if (!ShouldRetry(lastFailure.Code, binding.Policy, attempt)) break;
                    continue;
                }

                attempts.Add(new InferenceAttemptEvidence(attempt, metadata.Provider, metadata.Model, endpoint.EndpointId, true));
                var evidence = BuildEvidence(request, binding, attempts, lastResponse, anyWasRepaired, repairAttempts, repairStrategy, started,
                    hasPromptTokens ? promptTokens : null, hasCompletionTokens ? completionTokens : null, hasTotalTokens ? totalTokens : null);
                await RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
                return InferenceExecutionResult.Succeeded(extracted.Output!, evidence: evidence);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LlmClientException exception)
            {
                var metadata = Metadata(endpoint);
                var message = BoundMessage(exception.Message);
                lastFailure = new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, message, providerCode: exception.FailureKind.ToString());
                attempts.Add(FailedAttempt(attempt, metadata, endpoint, exception.FailureKind.ToString(), message));
                if (!exception.CanFallback || attempt == binding.Policy.MaximumAttempts) break;
            }
            catch (Exception exception)
            {
                var metadata = Metadata(endpoint);
                lastFailure = new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, "Baize provider execution failed.", providerCode: exception.GetType().Name);
                attempts.Add(FailedAttempt(attempt, metadata, endpoint, exception.GetType().Name, exception.Message));
                // Only Baize's typed client exception can assert that a call is
                // safe and useful to repeat or route elsewhere. An arbitrary
                // exception may be a host/programming defect, so repeating it
                // could duplicate cost without any trustworthy retry signal.
                break;
            }
        }

        var failureEvidence = BuildEvidence(request, binding, attempts, lastResponse, anyWasRepaired, repairAttempts, repairStrategy, started,
            hasPromptTokens ? promptTokens : null, hasCompletionTokens ? completionTokens : null, hasTotalTokens ? totalTokens : null);
        await RecordAsync(failureEvidence, cancellationToken).ConfigureAwait(false);
        return InferenceExecutionResult.Failed(lastFailure ?? new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError, "Baize inference did not produce a result."), failureEvidence);
    }

    private static LlmClientMetadata Metadata(BaizeEndpointBinding endpoint)
    {
        if (endpoint.Client is ILlmClientMetadataProvider provider)
        {
            try
            {
                var metadata = provider.Metadata;
                if (metadata is not null && IsBoundIdentity(metadata.Provider) && IsBoundIdentity(metadata.Model))
                    return metadata;
            }
            catch
            {
                // Fall back to the host binding if a decorator cannot expose
                // metadata safely; evidence must remain bounded and usable.
            }
        }

        return new LlmClientMetadata(endpoint.Provider, endpoint.Model, EndpointId: endpoint.EndpointId);
    }

    private static bool ShouldRetry(ExecutionFailureCode code, BaizeInferencePolicy policy, int attempt) =>
        policy.RetryRepresentationFailures && attempt < policy.MaximumAttempts && code is
            ExecutionFailureCode.MalformedOutput or
            ExecutionFailureCode.RepairedOutputSchemaInvalid or
            ExecutionFailureCode.SchemaMismatch or
            ExecutionFailureCode.ToolMappingFailure or
            ExecutionFailureCode.TruncatedOutput;

    private static LlmRequest CreateRequest(InferenceExecutionRequest request, BaizeInferenceBinding binding, BaizeEndpointBinding endpoint, out string? schemaJson)
    {
        schemaJson = binding.OutputMode == BaizeInferenceOutputMode.StructuredContent
            ? CreateSchemaJson(request.OutputType, binding.Schemas)
            : null;
        var messages = new List<LlmMessage>();
        if (!string.IsNullOrWhiteSpace(binding.SystemPrompt)) messages.Add(LlmMessage.Text("system", binding.SystemPrompt));
        var arguments = ToJsonObject(request.Arguments.ToDictionary(argument => argument.Name, argument => ToJsonNode(argument.Value), StringComparer.Ordinal));
        var context = ToJsonObject(request.ContextInputs.ToDictionary(input => input.Name, input => ToJsonNode(input.Value), StringComparer.Ordinal));
        var prompt = binding.UserPromptTemplate.Replace("{arguments}", arguments.ToJsonString(), StringComparison.Ordinal)
            .Replace("{context}", context.ToJsonString(), StringComparison.Ordinal);
        if (binding.UserPromptTemplate == "{arguments}") prompt = arguments.ToJsonString();
        messages.Add(LlmMessage.Text("user", prompt));
        var tools = binding.Tools.Select(tool => tool.Tool).ToList();
        var responseFormat = binding.OutputMode == BaizeInferenceOutputMode.StructuredContent
            ? LlmResponseFormat.JsonSchema(schemaJson!)
            : null;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["fuwen.operation.key"] = request.Invocation.OperationKey };
        return new LlmRequest(messages, maxTokens: binding.Policy.MaximumTokens, tools: tools, responseFormat: responseFormat, metadata: metadata);
    }

    private async ValueTask<ExtractedOutput> ExtractOutputAsync(LlmResponse response, FuwenType outputType, BaizeInferenceBinding binding, string? schemaJson, CancellationToken cancellationToken)
    {
        if (response.FinishReasonKind == LlmFinishReasonKind.LengthLimit)
            return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.TruncatedOutput, "The Baize response reached its output limit."));
        string? text;
        bool wasRepaired = response.ContentWasRepaired;
        int repairAttempts = Math.Min(InferenceExecutionEvidence.MaximumAttempts, response.ContentRepairAttempts?.Count ?? 0);
        string? repairStrategy = response.ContentRepairAttempts?.LastOrDefault(attempt => attempt.Status == LlmRepairStatus.Succeeded)?.Name;
        if (binding.OutputMode == BaizeInferenceOutputMode.ToolCall)
        {
            var toolCalls = response.ToolCalls ?? Array.Empty<LlmToolCall>();
            if (toolCalls.Count != 1)
                return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.ToolMappingFailure, "The response did not contain exactly one unambiguous tool call."));
            var toolCall = toolCalls[0];
            if (!string.Equals(toolCall.Name, binding.ExpectedToolName, StringComparison.Ordinal))
                return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.ToolMappingFailure, $"The response did not contain the expected tool '{binding.ExpectedToolName}'."));
            if (toolCall.NormalizationStatus is LlmToolCallNormalizationStatus.UnknownTool or LlmToolCallNormalizationStatus.EmptyArguments or LlmToolCallNormalizationStatus.InvalidArguments)
                return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.ToolMappingFailure, $"Tool call '{toolCall.Name}' was not mapped to its admitted contract."));
            text = toolCall.ArgumentsJson;
            wasRepaired |= toolCall.JsonWasRepaired;
            repairAttempts = Math.Min(InferenceExecutionEvidence.MaximumAttempts, repairAttempts + (toolCall.JsonRepairAttempts?.Count ?? 0));
            repairStrategy ??= toolCall.JsonRepairAttempts?.LastOrDefault(attempt => attempt.Status == LlmRepairStatus.Succeeded)?.Name;
        }
        else
        {
            if (response.ToolCalls is { Count: > 0 })
                return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.ToolMappingFailure, "The structured response contained an unexpected tool call."));
            text = response.Content;
        }

        if (string.IsNullOrWhiteSpace(text))
            return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.MalformedOutput, "The Baize response contained no structured JSON output."));

        JsonDocument? document = null;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { }
        if (document is null)
        {
            var repaired = await TryRepairAsync(text, schemaJson, cancellationToken).ConfigureAwait(false);
            if (!repaired.Succeeded)
            {
                document?.Dispose();
                return ExtractedOutput.Failed(new ExecutionFailure(ExecutionFailureKind.ProviderOutput, ExecutionFailureCode.MalformedOutput, "The Baize response was not valid JSON after deterministic repair."));
            }
            document?.Dispose();
            document = JsonDocument.Parse(repaired.Document!.RootElement.GetRawText());
            wasRepaired |= repaired.WasRepaired;
            repairAttempts = Math.Min(InferenceExecutionEvidence.MaximumAttempts, repairAttempts + repaired.TextRepairs.Count + repaired.NodeRepairs.Count);
            repairStrategy ??= repaired.SucceededBy?.Name;
        }

        RuntimeValue value;
        try
        {
            value = RuntimeValue.FromJson(document.RootElement);
        }
        catch (ArgumentException exception)
        {
            return ExtractedOutput.Failed(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                ExecutionFailureCode.MalformedOutput,
                BoundMessage($"The Baize response exceeded the bounded runtime-value contract. {exception.Message}")),
                wasRepaired,
                repairAttempts,
                repairStrategy);
        }
        finally
        {
            document.Dispose();
        }
        var validation = RuntimeValueValidator.Validate(value, outputType, binding.Schemas);
        if (!validation.Succeeded)
        {
            var detail = string.Join("; ", validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            return ExtractedOutput.Failed(new ExecutionFailure(
                ExecutionFailureKind.ProviderOutput,
                wasRepaired ? ExecutionFailureCode.RepairedOutputSchemaInvalid : ExecutionFailureCode.SchemaMismatch,
                BoundMessage($"The Baize output did not satisfy the admitted Fuwen schema. {detail}")),
                wasRepaired,
                repairAttempts,
                repairStrategy);
        }
        return ExtractedOutput.Success(value, wasRepaired, repairAttempts, repairStrategy);
    }

    private async ValueTask<JsonRepairResult> TryRepairAsync(string text, string? schemaJson, CancellationToken cancellationToken)
    {
        JsonSchemaExpectation? expectation = null;
        if (schemaJson is not null)
            expectation = JsonSchemaExpectation.FromSchemaNode(JsonNode.Parse(schemaJson)!);
        return await repairPipeline.RepairAsync(text, expectation, cancellationToken).ConfigureAwait(false);
    }

    private static InferenceExecutionEvidence BuildEvidence(
        InferenceExecutionRequest request,
        BaizeInferenceBinding binding,
        IReadOnlyList<InferenceAttemptEvidence> attempts,
        LlmResponse? response,
        bool wasRepaired,
        int repairAttempts,
        string? repairStrategy,
        long started,
        int? promptTokens,
        int? completionTokens,
        int? totalTokens)
    {
        return new InferenceExecutionEvidence(request.Profile, request.PromptTemplate, attempts,
            promptTokens, completionTokens, totalTokens,
            wasRepaired, Math.Min(InferenceExecutionEvidence.MaximumAttempts, repairAttempts),
            BoundOptionalText(repairStrategy, InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes),
            binding.Policy.PolicyRevision, binding.Policy.RoutingPolicyRevision,
            (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    private static string BoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The trusted inference policy rejected the request.";
        return BoundText(message, ExecutionFailure.MaximumMessageUtf8Bytes);
    }

    private static InferenceAttemptEvidence FailedAttempt(
        int attempt,
        LlmClientMetadata metadata,
        BaizeEndpointBinding endpoint,
        string code,
        string message) =>
        new(attempt, metadata.Provider, metadata.Model, endpoint.EndpointId, false,
            BoundText(code, InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes),
            BoundText(message, InferenceExecutionEvidence.MaximumDiagnosticUtf8Bytes));

    private static bool IsBoundIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Encoding.UTF8.GetByteCount(value) <= InferenceExecutionEvidence.MaximumIdentityUtf8Bytes;

    private static void AddUsage(int? value, ref int total, ref bool present)
    {
        if (value is null or < 0) return;
        present = true;
        total = value.Value > int.MaxValue - total ? int.MaxValue : total + value.Value;
    }

    private static string BoundText(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unclassified provider failure.";
        if (Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes) return value;

        var builder = new StringBuilder(value.Length);
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumUtf8Bytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return builder.Length == 0 ? "Unclassified provider failure." : builder.ToString();
    }

    private static string? BoundOptionalText(string? value, int maximumUtf8Bytes) =>
        value is null ? null : BoundText(value, maximumUtf8Bytes);

    private async ValueTask RecordAsync(InferenceExecutionEvidence evidence, CancellationToken cancellationToken)
    {
        if (provenanceSink is null) return;
        try { await provenanceSink.RecordAsync(evidence, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* evidence sinks are non-authoritative */ }
    }

    private sealed record ExtractedOutput(RuntimeValue? Output, ExecutionFailure? Failure, bool WasRepaired, int RepairAttempts, string? RepairStrategy)
    {
        internal bool Succeeded => Failure is null;
        internal static ExtractedOutput Success(RuntimeValue value, bool wasRepaired, int repairAttempts, string? repairStrategy) => new(value, null, wasRepaired, repairAttempts, repairStrategy);
        internal static ExtractedOutput Failed(ExecutionFailure failure, bool wasRepaired = false, int repairAttempts = 0, string? repairStrategy = null) =>
            new(null, failure, wasRepaired, repairAttempts, repairStrategy);
    }

    private static JsonObject ToJsonObject(IEnumerable<KeyValuePair<string, JsonNode>> values) =>
        new(values.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)));

    private static JsonNode ToJsonNode(RuntimeValue value) => value switch
    {
        JsonRuntimeValue json => JsonNode.Parse(json.Value.GetRawText())!,
        ArtifactRuntimeValue artifact => JsonSerializer.SerializeToNode(artifact.Artifact)!,
        ListRuntimeValue list => new JsonArray(list.Items.Select(ToJsonNode).ToArray()),
        ObjectRuntimeValue obj => ToJsonObject(obj.Properties.ToDictionary(pair => pair.Key, pair => ToJsonNode(pair.Value), StringComparer.Ordinal)),
        _ => throw new ArgumentException("Unsupported runtime value kind.", nameof(value))
    };

    private static string CreateSchemaJson(FuwenType type, IReadOnlyList<ResolvedSchemaDefinition> schemas)
    {
        JsonNode schema = CreateSchema(type, schemas, new HashSet<DescriptorReference>());
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode CreateSchema(FuwenType type, IReadOnlyList<ResolvedSchemaDefinition> schemas, HashSet<DescriptorReference> active)
    {
        return type switch
        {
            OptionalType optional => new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    CreateSchema(optional.ValueType, schemas, active),
                    new JsonObject { ["type"] = "null" }),
            },
            PrimitiveType primitive when primitive.Primitive == FuwenPrimitiveKind.Json => new JsonObject(),
            PrimitiveType primitive => new JsonObject { ["type"] = primitive.Primitive switch { FuwenPrimitiveKind.String or FuwenPrimitiveKind.Duration => "string", FuwenPrimitiveKind.Boolean => "boolean", FuwenPrimitiveKind.Integer => "integer", FuwenPrimitiveKind.Number => "number", _ => "string" } },
            ListType list => new JsonObject { ["type"] = "array", ["items"] = CreateSchema(list.ItemType, schemas, active), ["maxItems"] = list.MaxItems },
            NamedTypeReference named => CreateNamedSchema(named.Schema, schemas, active),
            _ => new JsonObject()
        };
    }

    private static JsonNode CreateNamedSchema(DescriptorReference descriptor, IReadOnlyList<ResolvedSchemaDefinition> schemas, HashSet<DescriptorReference> active)
    {
        if (!active.Add(descriptor)) return new JsonObject();
        try
        {
            return schemas.FirstOrDefault(schema => schema.Descriptor == descriptor) switch
            {
                EnumSchemaDefinition @enum => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(@enum.Members.Select(member => JsonValue.Create(member.Value)).ToArray()) },
                ObjectSchemaDefinition obj => new JsonObject { ["type"] = "object", ["properties"] = ToJsonObject(obj.Fields.ToDictionary(field => field.Name, field => CreateSchema(field.Type, schemas, active), StringComparer.Ordinal)), ["required"] = new JsonArray(obj.Fields.Where(field => field.Type is not OptionalType).Select(field => JsonValue.Create(field.Name)).ToArray()), ["additionalProperties"] = false },
                _ => new JsonObject()
            };
        }
        finally { active.Remove(descriptor); }
    }
}

internal static class BaizeBindingValidation
{
    internal static string Text(string value, string parameterName, int maximumUtf8Bytes) =>
        OptionalText(value, parameterName, maximumUtf8Bytes) ?? throw new ArgumentNullException(parameterName);

    internal static string? OptionalText(string? value, string parameterName, int maximumUtf8Bytes)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("The value cannot contain control characters.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentOutOfRangeException(parameterName, $"The value exceeds {maximumUtf8Bytes} UTF-8 bytes.");
        return new string(value.ToCharArray());
    }
}
