using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

// This worker has no source text or macro-library input. Both launches re-admit
// the same stored expansion against the same host catalogue.
if (args.Length != 3) return 2;
var directory = Path.GetFullPath(args[0]);
var mode = args[2];
var fanOutMode = mode.StartsWith("fanout-", StringComparison.Ordinal);
var definitions = new DirectoryWorkflowDefinitionStore(directory);
var pinned = await definitions.ReadGroupedAsync(args[1]) ?? throw new InvalidOperationException("Missing pinned expansion.");
var text = new PrimitiveType(FuwenPrimitiveKind.String);
var catalogue = new InMemoryTrustedCatalogue([
    new TrustedCatalogueDescriptor(new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1",
        new ContentDigest("sha256", "descriptor/v1", new string('a', 64))),
        callableContract: new CallableContract(new CallableSignature([new CallableParameter("value", text)], text),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]);
var admission = await new WorkflowAdmissionService(new WorkflowCompiler(catalogue,
    capabilityPolicy: new CapabilityGrantPolicy("policy/1", []))).AdmitGroupedAsync(pinned);
if (!admission.Succeeded) throw new InvalidOperationException(string.Join("; ", admission.Admission.Diagnostics.Select(d => d.Message)));
var identity = new FuwenZhinuProviderRuntimeIdentity(admission.Admission.Receipt!.CatalogueSnapshotRevision,
    admission.Admission.Receipt.ResolvedDescriptorSetFingerprint);
var registration = await new FuwenZhinuWorkflowFactory(definitions, identity,
    new FuwenZhinuExecutionPorts(new Echo(directory, blockOnSecond: mode == "fanout-start"), new UnusedContext(), new UnusedInference(),
        observer: null, options: new FuwenZhinuExecutionPorts.Options(maximumFanOutConcurrency: fanOutMode ? 1 : 32)))
    .CreateGroupedAsync("group-process", "1", admission);
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "run.db"), Pooling = false });
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ct = timeout.Token;
await using var engine = new WorkflowEngine(store, registration.Register(new WorkflowRegistry()), new ZhinuOptions
{
    LeaseDuration = TimeSpan.FromSeconds(1),
    LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
    PollInterval = TimeSpan.FromMilliseconds(10)
});
Guid runId;
if (mode is "start" or "fanout-start")
{
    var input = fanOutMode ? JsonSerializer.SerializeToElement(new[] { "a" }) : JsonSerializer.SerializeToElement("hello");
    runId = await engine.StartAsync("group-process", "1", input, cancellationToken: ct);
    await File.WriteAllTextAsync(Path.Combine(directory, "run-id"), runId.ToString("D"), ct);
    var execution = engine.ExecuteAsync(runId, ct);
    if (mode == "fanout-start")
    {
        while (!File.Exists(Path.Combine(directory, "fanout-second-entered")))
            await Task.Delay(20, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier"), "second-operation", ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return 3; // The parent kills this launch after operation one's durable commit.
    }
    while (!(await store.GetStepsAsync(runId, ct)).Any(s => s.Status == StepStatus.Waiting))
        await Task.Delay(20, ct);
    await File.WriteAllTextAsync(Path.Combine(directory, "barrier"), "waiting", ct);
    await execution;
    return 3; // The parent must terminate this launch at the durable wait barrier.
}
runId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run-id"), ct));
if (!fanOutMode) await engine.SendSignalAsync(runId, "approve", "accepted", ct);
await engine.ExecuteAsync(runId, ct);
var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
var result = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText();
await File.WriteAllTextAsync(Path.Combine(directory, "result"), result, ct);
return fanOutMode || result == "accepted" ? 0 : 4;

sealed class Echo(string directory, bool blockOnSecond = false) : IActivityExecutor
{
    public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (blockOnSecond && request.Invocation.StructuralPath.EndsWith("/second", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "fanout-second-entered"), request.Invocation.StructuralPath, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        await File.AppendAllTextAsync(Path.Combine(directory, "effects"), request.Invocation.StructuralPath + "\n", cancellationToken);
        return ActivityExecutionResult.Succeeded(request.Arguments.Single().Value);
    }
}
sealed class UnusedContext : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
sealed class UnusedInference : IInferenceExecutor
{
    public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
