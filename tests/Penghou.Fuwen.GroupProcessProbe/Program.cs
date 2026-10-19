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
    new FuwenZhinuExecutionPorts(new Echo(directory), new UnusedContext(), new UnusedInference()))
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
if (args[2] == "start")
{
    runId = await engine.StartAsync("group-process", "1", JsonSerializer.SerializeToElement("hello"), cancellationToken: ct);
    await File.WriteAllTextAsync(Path.Combine(directory, "run-id"), runId.ToString("D"), ct);
    var execution = engine.ExecuteAsync(runId, ct);
    while (!(await store.GetStepsAsync(runId, ct)).Any(s => s.Status == StepStatus.Waiting))
        await Task.Delay(20, ct);
    await File.WriteAllTextAsync(Path.Combine(directory, "barrier"), "waiting", ct);
    await execution;
    return 3; // The parent must terminate this launch at the durable wait barrier.
}
runId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run-id"), ct));
await engine.SendSignalAsync(runId, "approve", "accepted", ct);
await engine.ExecuteAsync(runId, ct);
var output = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: ct);
await File.WriteAllTextAsync(Path.Combine(directory, "result"), output.GetString(), ct);
return output.GetString() == "accepted" ? 0 : 4;

sealed class Echo(string directory) : IActivityExecutor
{
    public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
    {
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
