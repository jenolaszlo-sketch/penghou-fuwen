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
plans containing context, inference, activity, conditional, and return nodes.
It intentionally executes unordered phase members sequentially in
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

`ConditionalNode` selects one closed branch and may expose one explicit typed
merge result. Branch-local outputs otherwise cannot be referenced from the root
region; the adapter never infers a merge or weakens region isolation.

## Durable complex inference

When the host supplies `IInferenceTurnExecutor` and (for tool proposals)
`IInferenceReadToolExecutor` through `FuwenZhinuExecutionPorts`, an `infer` node
that declares aggregate `Protocol` limits runs through the internal
`FuwenInferenceCoordinator` instead of the one-call step. The coordinator owns a
durable model → tool → model loop: each iteration performs exactly one durable
operation (`infer-model-turn-NNNN` or `infer-read-tool-NNNN-<callId>`) whose
stable operation identity is derived from the interaction identity and ordinal.
A completed operation is reused on replay, so a crash after a committed tool
result repeats only the in-flight turn. Ambiguous provider/tool transport, or a
tool failure that may have committed an effect, stops with a typed
`AmbiguousOperation` failure rather than silently issuing a new identity.

Effective bounds are the per-dimension minimum of the authored protocol limits
and the optional host `InferenceHostCeilings`; source bounds can only narrow
host ceilings. The coordinator checks turns, model calls, tool calls, tokens
(including unknown-usage `BudgetUnknown`), cost, payload bytes, retained
conversation bytes, and Zhinu's durable `TimeBudget` before each operation. A
tool is not issued when no turn or model call remains to consume its result.
Workflow-owned local prompts are required; a registered template or alias on
a coordinated node is rejected at registration, not at runtime. Context
delivery is not yet part of the coordinated loop. The one-call path remains
the behavior for nodes without aggregate protocol limits.

Per-call `maxTokens` and `timeout` from `InferenceLimits` are forwarded to
every turn and enforced: a completion overage fails with
`PerCallLimitExceeded`, an overrun fails with `Timeout`, and unknown usage
against a declared per-call bound stops as `BudgetUnknown`. Aggregate bounds
combine per dimension as the minimum of source and host ceilings, including a
host-only cost ceiling; mixed cost currencies fail closed as unknown rather
than converting. Interaction identity includes the runtime scope so repeat
iterations never share one operation journal, and per-operation evidence uses
a global sequence ordinal.

Registration preflights each coordinated node's exact requirement against the
turn executor's `IInferenceTurnExecutorManifest` when present; one-call nodes
always use the one-call executor's manifest or preflight hook, so mixed plans
route each node to its own manifest. Coordinated inference inside a fan-out
body is rejected at registration: an item step has no item-scoped nested-loop
primitive for per-operation durable steps.

The coordinator runs in root, conditional, and repeat regions. In a repeat body
its loop nests under the repeat iteration, so each iteration has its own
interaction identity and operation journal. Fan-out bodies still execute
inference through the one-call path; a multi-turn protocol per fan-out item
would require an item-scoped nested-loop primitive and remains out of scope.

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

## Fork evidence

Durable steps copied by a Zhinu fork retain their source invocation identity.
The host must explicitly list every accepted source execution fingerprint in
`FuwenZhinuExecutionPorts.PriorExecutionFingerprints`. This includes the current
fingerprint when a fingerprint-identical fork is allowed to reuse earlier
steps. An empty set keeps strict run fencing. The adapter still requires the
exact structural path and effective request fingerprint, so this setting does
not authorize unrelated evidence or changed inputs.
