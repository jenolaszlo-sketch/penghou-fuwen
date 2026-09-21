# ADR 0008: Model-visible context delivery is explicit and bounded

## Status

Accepted, 2026-09-21.

## Context

Inference context requirements bind a typed runtime value to an immutable
context snapshot. Zhinu preserved both, but Baize's workflow-owned prompt path
sent only authored prompt messages and silently omitted the context values.
Automatically appending arbitrary context would fix visibility while creating
new ambiguity around redaction, size, artifacts, replay identity, and evidence.

## Decision

A Baize binding must opt in to context delivery with a
`BaizeContextDeliveryPolicy`. The policy has an immutable revision and a UTF-8
ceiling. Zhinu includes the presence of context in executor preflight, so a
missing mapping fails before definition storage or provider work. Registered
templates must additionally contain the explicit `{context}` placeholder.

Baize maps named context values to one canonical JSON object ordered by
requirement name. For workflow-owned prompts it adds that object as a separate
user-role message, clearly labelled as data rather than instructions. Authored
prompt messages are not mutated. A registered template receives the same
canonical object through its placeholder.

Selection, field-level redaction, and source truncation remain responsibilities
of the trusted context provider. Its `ContextSnapshotReference` already records
the provider, snapshot and content identities, redaction-policy revision, and
budget/truncation evidence. Baize neither reverses those decisions nor invents
implicit name-based redaction. If the canonical representation exceeds the
delivery ceiling, execution fails without a provider call; JSON is never cut or
silently truncated.

Artifact runtime values remain detached references. Context mapping serializes
only their provider, identifier, exact descriptor, content digest, length, and
logical name. It never retrieves artifact bytes. Supplying artifact contents
requires a future explicit host capability.

Inference evidence records the named context snapshots, delivery-policy
revision, canonical payload digest, and byte count, but not raw context values.
The Zhinu request identity already includes the typed values and snapshots. A
host must change its provider-runtime/catalogue snapshot identity whenever a
context-delivery policy revision changes, preventing durable reuse across
different model-visible mappings.

## Consequences

- Context can no longer be gathered successfully and then silently discarded.
- Operators can correlate intended snapshots with the bounded representation
  prepared for the model without storing sensitive values in provenance.
- Context-provider redaction and truncation stay visible and independently
  attestable.
- Oversized values and unsupported mappings fail before paid provider work.
- Sequential, repeat, and fan-out execution share the same request contract;
  per-region values and snapshots remain part of durable request identity.
