# Consumer migration notes

Consumer-visible breaking changes are listed here per preview, with the exact
edits a downstream package needs. The matching [CHANGELOG](../CHANGELOG.md)
entry names the change; this document gives the recipe.

Publish order matters: **Fuwen → Guihua (or any adapter) → applications**
(Guyabano, Marang). A consumer can only move once every package in its
dependency chain is indexed on NuGet.org, because a preview adapter pins the
exact Fuwen preview it was built against.

## Unreleased source after preview.11

These changes are in the source tree and are **not yet a published NuGet
version**. Rebuild and test adapters and hosts before selecting a future
preview tag.

- **Coordinated turn requests:** InferenceTurnRequest.BudgetEnforcement now carries the admitted strict/advisory mode. The legacy constructor defaults to strict. Custom turn executors may honor advisory prompt, total-token and cost allowances as monitoring only; they must reject unsupported hard ceilings before submission. Assistant history now carries native ToolCalls, and tool results carry the exact Tool descriptor. Preserve these fields when adapting provider conversations. InferenceToolCallTurnResult.AssistantText retains mixed text/tool responses. InferenceTurnFailureException preserves pre-submit versus possibly committed failures.
- **Baize hosts:** BaizeInferenceTurnExecutor can bind an exact configured profile and tool schemas for coordinated turns. It makes one provider call per turn and does not supply a trusted maximum-charge quote; strict aggregate prompt, total-token and cost registration remains closed. Unsupported provider continuation blobs fail with possible-commitment evidence.
- **Coordinated turn executors:** implement
  `IInferenceTurnExecutorManifest` on the selected executor. When the authored
  plan or host sets a completion-token ceiling, advertise
  `SupportsHardCompletionTokenLimit` only if the executor actually enforces
  each request's `MaxCompletionTokens`. Treat
  `MaximumNewToolCalls == 0` with an empty `VisibleTools` list as a
  finalization-only request; no tool proposals are allowed. The coordinator
  rejects a batch larger than the remaining allowance before running any
  tool. Different descriptors with the same provider-visible name now fail
  coordinated registration. Admitted tool signatures now validate exact
  argument names/types before tool I/O and result types before storage.
  Adapt fixtures and executors that previously sent arbitrary JSON objects
  despite declaring a different callable signature.
- **Hosts using coordinated read tools:** supply an
  `IInferenceProtectedPayloadStore` through the expanded
  `FuwenZhinuExecutionPorts` constructor. The old constructor remains
  available, but registration of a coordinated tool workflow fails without
  the store. It must durably bind idempotent writes to interaction, scope,
  descriptor, operation key and exact bytes; authorize reads and retain
  results for the workflow's recovery lifetime. The included in-memory store
  is a test fixture, not a production storage adapter.
- **Host cost ceilings:** a coordinated host-only `CostMicrounits` ceiling
  now fails admission unless the authored protocol supplies the currency.
  Strict host monetary ceilings still fail admission. The optional SQLite
  leaf ledger reserves quoted charges before a turn, but the configured Baize
  path cannot supply a trusted maximum quote. Authored cost
  limits also fail by default; use explicit `aggregate advisory` only if
  after-call monitoring is acceptable. Successive advisory turn requests
  receive the remaining monetary allowance; mixed pricing revisions produce
  unknown aggregate cost.
- **Persisted runs:** interaction and operation-key identities changed, and
  coordinator state now binds admitted tool signatures and native assistant/tool history as well as protected tool payload semantics. Do not
  resume an older in-flight coordinator journal with the new implementation
  unless it is explicitly migrated; the runtime stops unsupported state.
  Plan IR and package versions are separate from this journal contract.
- **Limits and evidence:** final usage/cost settlement, currency checks,
  overflow handling, and serialized evidence caps now fail closed. Prompt,
  total-token and cost strict ceilings now fail admission before a paid call.
  Advisory monitoring cannot prevent a paid overrun; applications must not
  present it as a hard spend limit. Protected storage
  currently covers successful coordinated read-tool results, not prompts,
  arguments, model outputs, or other workflow history.

## preview.10 → preview.11

Verified by migrating Guyabano (`Penghou.Guihua` `0.1.0-preview.1` →
`0.1.0-preview.2`). preview.11 collapsed the versioned-vector surface (CI-2)
and hardened inference admission.

### Renamed constants (`FuwenContracts`)

| Removed | Replacement |
| --- | --- |
| `FuwenContracts.ExecutionFingerprintVersionV1` | `FuwenContracts.ExecutionFingerprintVersion` |
| `FuwenContracts.IrVersionV7` (and other `*V<n>` names) | `FuwenContracts.IrVersion` |

The current contract is the only supported one; the legacy `*V<n>` constant
names were removed.

### `WorkflowPlanBuilder`

`BuildV3()`, `BuildV4()`, `BuildV5()`, `BuildV6()`, and `BuildV7()` were
collapsed into a single `Build()`. Replace every `BuildV<n>()` call with
`Build()`.

### Inference executors must preflight

`FuwenZhinuWorkflowFactory` now rejects an inference node unless the configured
inference executor implements `IInferenceExecutorManifest` or
`IInferenceExecutorPreflight`:

> Inference node '<path>' requires an adapter manifest or preflight implementation.

A one-call executor can satisfy this with a minimal preflight that rejects only
what it does not support; `null` means executable:

```csharp
public sealed class MyInferenceExecutor(...) : IInferenceExecutor, IInferenceExecutorPreflight
{
    public ExecutionFailure? Preflight(InferenceExecutionRequirement requirement)
    {
        if (requirement.Tools.Count > 0)
        {
            return new ExecutionFailure(
                ExecutionFailureKind.Admission,
                ExecutionFailureCode.NotAdmitted,
                "This executor runs a single provider call without tools.");
        }

        return null;
    }
}
```

Coordinated (multi-turn) executors should implement `IInferenceExecutorManifest`
and return a structured `InferencePreflightReport` instead. Test doubles that
never run tools may return `null` unconditionally.

### Test-only fixtures

Execution-fingerprint fixtures must use the canonical
`sha256:fuwen-execution/v1:<64 lowercase hex>` form; `WorkflowPlanIdentity`
rejects other fingerprint versions.

## Detecting breaks

`Microsoft.CodeAnalysis.PublicApiAnalyzers` runs on pack. During preview every
public API lives in `PublicAPI.Unshipped.txt` and `PublicAPI.Shipped.txt` is
empty, so preview renames are intentional and must be recorded here. Before RC,
baselines move to `Shipped` and additive-only applies within the line; see
[graduation requirements](graduation-requirements.md).
