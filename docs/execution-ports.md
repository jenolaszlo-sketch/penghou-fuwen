# Provider-neutral execution ports

The core package freezes the provider-neutral boundary used by future Zhinu,
Baize, and host adapters. Activity, context, and inference requests contain the
exact descriptor identities from the admitted plan, deeply snapshotted runtime
arguments, the declared output type, and an `ExecutionInvocation`.

`ExecutionInvocation.OperationKey` is a canonical
`sha256:fuwen-operation/v1:<lowercase-hex>` digest over the execution
fingerprint, structural path, runtime path, step revision, and effective request
fingerprint. Infrastructure retries reuse that identity even when a recovered
worker holds a newer lease;
selective restart, a changed input, or a changed revision must produce a new
identity.

Lease fencing is authorization/order evidence, not idempotency identity. It
must be supplied and checked separately by an adapter/runtime that exposes an
authoritative fence. Including a worker lease generation in the operation key
would permit duplicate external effects after crash recovery.

Inference context inputs keep four things together: the declared requirement
name, its expected type, the detached runtime value, and the immutable context
snapshot evidence. A snapshot reference never substitutes for the value.

Model delivery is a separate trusted-host decision. Baize requires an explicit
revisioned `BaizeContextDeliveryPolicy`, canonicalizes named values as bounded
JSON, and rejects missing mappings or oversized payloads before provider work.
Context-provider snapshots remain authoritative for selection, redaction, and
source truncation. Artifact context carries detached identity only; adapters do
not dereference bytes implicitly. See ADR 0008.

Inference requests carry an explicit tool allowlist. A non-null empty
`InferenceExecutionRequest.Tools` collection means `tools none`; a non-empty
collection is the exact model-visible set. Null means no declared model-callable
tools. Workflow-owned prompts and registered templates use the same rule. An
inference adapter records the effective model-visible descriptors in
`InferenceExecutionEvidence.AdmittedTools`, so provider requests and evidence
cannot describe different tool surfaces.

One model turn is a separate provider-neutral port. `IInferenceTurnExecutor`
accepts bounded normalized conversation state, the exact model-visible tool
requirements, and the remaining aggregate bounds, and returns either a final
candidate or exact tool-call proposals with normalized per-turn usage (unknown
stays null, never zero). `IInferenceReadToolExecutor` accepts one exact
admitted descriptor, typed bounded arguments, capability scope, stable
operation key, and retry safety, and returns a typed result with bounded
evidence. Neither port embeds credentials, filesystem paths, provider clients,
or Zhinu contexts. `InferenceTurnValidation` rejects undeclared,
write-capable, duplicate, oversized, or mistyped proposals before any tool
execution. Each turn request also carries the plan's per-call completion and
timeout bounds, and each read-tool request carries the effective result-byte
ceiling, so hosts can enforce them before returning. Turn executors may
advertise `IInferenceTurnExecutorManifest` for structured preflight of the
exact requirement. Baize maps its provider tool-call shapes through
`BaizeOneTurnMapper` under the same rules. Deterministic fake turn and
read-tool executors plus `InferenceTurnToolConformance` suites let provider
transport and host tools be tested independently of the durable coordinator.
The durable model → tool → model loop remains a Zhinu concern.

Execution results contain exactly one successful output or one failure.
Activity and inference successes may include deeply snapshotted artifact
publication receipts; context successes instead require snapshot evidence.
Inference evidence records the trusted resolved modality, provider/model route,
per-attempt and aggregate token usage, duration, and optional monetary cost in
integer microunits with its pricing revision. Integer microunits avoid
floating-point currency arithmetic and preserve deterministic durable envelopes.
Failure kinds and codes are closed typed contracts, while a bounded optional
provider code preserves provider-specific detail. Only a transient
infrastructure failure that cannot have committed an effect may request a retry
of the same operation identity.

Baize media generation remains an inference concern at this boundary and returns
nominal `ArtifactReference` values. The host owns publication and byte/digest
verification. Crash-safe routes must advertise idempotent submission and
operation retrieval, submit with `ExecutionInvocation.OperationKey`, and poll
the pinned provider handle.

The observer receives only minimal non-authoritative lifecycle observations.
It is not workflow state or durable evidence by itself. The ports do not retry,
admit, authorize, schedule, resolve credentials, dereference artifacts, or
carry provider/model configuration. Those responsibilities remain with the
host and adapters.
