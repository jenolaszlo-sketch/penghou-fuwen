# Zhinu dependencies for Fuwen inference hardening

These are owning-project contracts identified against the current
`Penghou.Zhinu` source tree. Fuwen must continue to reject unsupported
combinations until the package it consumes provides and tests these contracts.

## Fenced operator commands (FI-06)

Zhinu already exposes read-only run, step, event-page, progress, diagnosis,
result, artifact, signal, loop, and wait queries through `IWorkflowReader`
(`src/Penghou.Zhinu/WorkflowEngineCapabilities.cs`). Event pages accept an
`afterSequence` cursor and a finite `limit`; Fuwen can use these now for a
bounded reference inspection view. The stock Baize Review consumer checks
that reading a run, its steps, and one event page causes no provider work.

Administrative writes still need an exact, fenced command contract.
`IWorkflowAdministration.CancelAsync` has no expected run state/revision or
command ID. Restart has an idempotent operation ID through
`RestartStepOptions` and `IIdempotentWorkflowRestartRepository`, but no
expected-state precondition. `ExecuteAsync(runId)` can resume work, yet is
not a receipt-bearing reconcile command.

**Zhinu ticket:** accept a stable command ID, exact run ID, and expected
generation/revision/status for cancel and restart (and a separate resume
command if the operator surface needs one). Compare and apply atomically in
the durable store, return the original receipt for duplicate commands, reject
ID reuse with different payload and stale state before new work, and preserve
fencing under lease changes. Tests must cover duplicate, conflicting, stale,
cancelled, completed, and resumed runs. Fuwen can then wrap these public
commands in a bounded operator view without inventing another authority.

## Item-scoped nested durable work (FI-08)

Zhinu's keyed `FanOutAsync` provides stable caller-supplied item keys,
bounded concurrency and independently durable **outer** item steps
(`src/Penghou.Zhinu/WorkflowContext.cs`). Each callback receives only a
`WorkflowStepContext` with identity, artifact publication and event emission
(`src/Penghou.Zhinu/WorkflowStepContext.cs`). It cannot start nested
journaled steps or loops under the item. A coordinated inference protocol
inside the callback would therefore be one opaque step with no durable
per-model/per-tool continuation.

**Zhinu ticket:** provide a child durable operation scope derived from the
canonical fan-out item key and structural path. It must journal nested steps
and loops with stable names, preserve item identity across source reordering,
fence concurrent/restarted workers, and allow focused restart to reuse
unaffected nested operations. A proof must run two items with identical
provider call IDs, crash one between model and tool results, reorder inputs,
and resume without cross-item journal or output reuse. Fuwen can then enable
coordinated inference in fan-out; until then admission remains closed.

Shared parent budget reservations are a separate host/ledger prerequisite:
per-item limits cannot claim to enforce a workflow-wide monetary ceiling
across concurrent items without atomic reservation and uncertain-charge
retention.
