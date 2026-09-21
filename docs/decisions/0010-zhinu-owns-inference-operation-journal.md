# ADR 0010: Zhinu owns inference operation identity and durable recovery

## Status

Accepted, 2026-09-21.

## Context

One logical inference may contain paid model calls and host-authorized read
tools. Workflow retry, process restart, cancellation, and fencing must not
silently repeat completed work or turn an in-memory loop into an untracked
workflow step. Provider outcomes can also be ambiguous: a request may have
committed remotely even when the caller did not receive a response.

Evidence must explain the activity and permit recovery without putting raw
provider payloads, credentials, unrestricted context, tool contents, or
chain-of-thought into ordinary workflow history. The enclosing Fuwen workflow
needs a stable result while Zhinu needs a durable source of truth for internal
operations.

## Decision

Zhinu owns the logical inference invocation, attempt, fencing, cancellation,
deadline, durable journal, and recovery policy. Internal model-turn, tool-call,
validation, and representation-repair operations are not independent Fuwen
nodes and do not automatically become independent workflow steps.

Each admitted invocation derives a stable interaction identity from the
execution fingerprint, structural/runtime path, step revision, effective
request fingerprint, and inference-protocol revision. Each internal operation
adds a deterministic ordinal and kind, for example:

```text
<interaction>/model/0001
<interaction>/tool/0001/<model-tool-call-id>
<interaction>/model/0002
<interaction>/validation/0001
```

The journal durably records, at minimum, the invocation and attempt,
protocol/runtime revision, operation identity and fence, normalized request
digest, disposition, bounded result or protected payload reference, provider
and model identity, usage/cost quality, timing, commitment uncertainty,
continuation/reconciliation state, and terminal failure or outcome. The exact
provider request and sensitive values are not required ordinary evidence.

Zhinu must fence claims before execution and persist the claim/receipt state
around every paid or tool operation. On recovery it reuses a completed
operation, reconciles an ambiguous operation through a provider/tool receipt
or idempotency key, or stops with a typed operator-action outcome. It must not
issue a second operation under a new identity merely because a prior response
was lost. Exactly-once remote execution is not assumed. Read-only calls may be
effect-repeatable, but their replay policy, cost, data freshness, and evidence
remain explicit.

The journal stores safe summaries, digests, and immutable protected-payload
references by default. A protected reference identifies an access-controlled
host-owned payload, its content digest, descriptor, length, retention/policy
metadata, and provider/storage identity; it is not an implicit dereference.
Normal workflow history and operator reports do not contain credentials, raw
secrets, unrestricted context, arbitrary tool payloads, or chain-of-thought.
Access to protected payloads is a host policy decision and is not required for
basic replay or explanation.

Workflow retry resumes or reconciles the same logical invocation. Forks must
carry the existing identity/evidence rules and explicitly reject incompatible
or changed fingerprints; they must not copy a live fence or accidentally reuse
an operation belonging to another invocation. Hongxian and Siming may receive
correlation sinks, but neither is authoritative for execution truth.

## Consequences

- Crash recovery has a single owner and a durable identity for every internal
  paid or tool operation.
- Lost responses cannot silently cause a duplicate provider operation.
- Fencing and replay remain enforceable across restart, retry, and fan-out.
- Evidence is explainable by default while sensitive payloads remain behind
  explicit host-controlled references.
- Zhinu storage/recovery is part of the versioned protocol contract, not an
  in-memory implementation detail or a requirement that internal operations
  become authored workflow nodes.
