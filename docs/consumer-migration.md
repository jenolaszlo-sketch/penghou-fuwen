# Consumer migration notes

Consumer-visible breaking changes are listed here per preview, with the exact
edits a downstream package needs. The matching [CHANGELOG](../CHANGELOG.md)
entry names the change; this document gives the recipe.

Publish order matters: **Fuwen → Guihua (or any adapter) → applications**
(Guyabano, Marang). A consumer can only move once every package in its
dependency chain is indexed on NuGet.org, because a preview adapter pins the
exact Fuwen preview it was built against.

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
