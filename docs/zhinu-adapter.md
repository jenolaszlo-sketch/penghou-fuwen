# Zhinu adapter boundary

`Penghou.Fuwen.Zhinu` combines a strict admission/registration boundary with a
deliberately narrow sequential interpreter. It accepts only a successful
`WorkflowAdmissionResult` with its in-process `WorkflowAdmissionReceipt` and
immutable definition. Before it registers a workflow, it verifies all of the
following:

- The receipt and immutable definition have the exact same execution fingerprint.
- The active provider runtime has the receipt's catalogue snapshot revision and
  resolved descriptor-set fingerprint.
- The immutable definition can be stored and loaded again under that exact
  execution fingerprint without changing its canonical bytes.

The generated Zhinu workflow implements `IWorkflowFingerprint`; its fingerprint
is the admitted Fuwen execution fingerprint. An admission-only factory remains
available for definition registration and fails explicitly if execution is
attempted without provider ports.

## Executable subset

When constructed with `FuwenZhinuExecutionPorts`, the adapter executes admitted
IR v3 plans containing context, inference, activity, conditional, and return
nodes. It intentionally executes unordered phase members sequentially in
ordinal structural-path order. Every node is a stable Zhinu step, and dependency
edges come from Fuwen bindings, typed context requirements, and conditional
ancestry. This preserves deterministic replay and gives Zhinu the graph needed
for later selective restart.

Provider results are validated by Fuwen's authoritative runtime validator before
Zhinu commits the step. Context values remain paired with immutable snapshot
evidence, and artifact references can be carried through nested typed values
without loading artifact bytes into workflow state. Loops and waits remain
outside this checkpoint. IR v4 keyed fan-out is mapped through stable per-item
`StepAsync` calls and a source-ordered aggregate step. Zhinu's preview
`FanOutAsync` uses positional index keys, so it cannot provide Fuwen's canonical
keyed identity; the adapter deliberately composes its durable step primitive
instead. Per-item outcomes remain durable when one sibling fails, and the
aggregate depends on all item steps so dependency-aware restart preserves
unrelated successful siblings. Plan concurrency is always capped by the host's
`MaximumFanOutConcurrency` option.

Successful activity and inference publication receipts are accepted only when
their idempotency key matches the current Fuwen operation key and their artifact
descriptor belongs to the admitted plan. The adapter publishes a provider-neutral
reference through the current Zhinu step, including the Fuwen execution,
structural, operation, provider, artifact, and receipt identities as metadata.
Zhinu's step-revision publication scope makes this idempotent across the crash
window between publication and step completion; an explicit restart creates a
new step revision and therefore new publication provenance.

Execution observations are optional and best-effort. They occur only when a
provider delegate actually runs, so replaying a completed step does not emit a
false provider call. Observer failure cannot alter workflow state, while
authoritative workflow cancellation is still propagated.

The current `ConditionalNode` selects a closed branch but produces no merged
value. Branch-local outputs cannot be referenced from the root region. A later
versioned IR may add an explicit typed branch result; the adapter will not infer
one or weaken region isolation.

## Startup and durable resume

Admission receipts are opaque in-process proofs, not persisted credentials. The
adapter neither serializes, stores, nor recreates one. Before a host resumes a
durable Zhinu run after startup, it must re-admit the immutable Fuwen definition
against its current trusted catalogue, policy, grants, and budget, verify that
the provider runtime identity still matches, and register the resulting
workflow. A changed identity must be treated as a new admission decision, never
as a receipt that can be forged from persisted state.

Policy revision, finite grants, and compilation/admission budgets are checked by
that fresh admission. They are not duplicated in the provider-runtime identity:
if current policy still admits the exact plan, the new in-process receipt is the
authority to resume; otherwise registration is unavailable. Catalogue revision
and resolved descriptor identity are checked again at the adapter boundary
because they also identify the implementations that will receive execution
requests.

Zhinu lease generation is fencing evidence and is intentionally not part of the
Fuwen operation key. A lease may change when another worker safely takes over
the same step revision; the downstream idempotency identity must not change in
that case.
