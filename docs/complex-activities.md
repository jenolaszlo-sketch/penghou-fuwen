# Bounded complex activities

Implementation work is tracked in the
[bounded complex inference implementation plan](complex-activities-implementation-plan.md).
The plan makes Marang the first consumer proof while keeping all contracts
product-neutral.

Status: accepted product and architecture direction; not yet implemented.

## Direction

Fuwen should expose workflow meaning without requiring authors to reproduce a
reusable provider protocol as workflow structure. The first application is the
existing `infer` node: one logical inference may require several model calls,
read-oriented tool calls, representation repair, and final validation while
remaining one workflow node.

The decision rule is:

> Hide repeatable protocol mechanics, not workflow decisions, and never hide
> the evidence needed to explain or safely resume them.

Model → tool → model → validated result may be an internal inference protocol.
Research → decide → implement → review → rework remains explicit Fuwen control
flow. Deployment, publication, commits, messages, transactions, destructive
changes, workflow mutation, and other externally meaningful effects remain
explicit nodes by default.

## Why this fits Fuwen

The current language already treats `infer` as a logical node with explicit
prompt source, typed bindings, context dependencies, profile, allowed tools,
output type, and limits. Extending its execution contract is preferable to
expanding model/tool turns into static Fuwen nodes because it keeps authored
plans compact, stable, provider-independent, and easier to generate and review.

This is an execution protocol behind a declared node, not a general scripting
escape hatch. Do not add a public generic `IComplexActivityHandler` or arbitrary
activity kinds until a second proven protocol demonstrates a common contract.
Fuwen does not expose a generic activity-plugin escape hatch through this design.

## Contract visible in the plan

An inference complex activity keeps these semantics explicit and fingerprinted:

- stable structural node identity;
- prompt definition or exact registered alias plus typed bindings;
- typed context inputs and output contract;
- exact profile and allowed-tool descriptors;
- required capabilities and effect restrictions;
- author-requested bounds that may only narrow host ceilings;
- dependencies and surrounding workflow control flow.

Initial aggregate bounds should cover turns, model calls, tool calls, output
tokens, elapsed duration, and—when trusted pricing exists—cost. Per-call limits
do not replace aggregate activity limits. Exhaustion produces typed normalized
failures such as turn, tool-call, token, cost, deadline, validation, protocol,
tool, or provider failure.

The syntax shown in product sketches is illustrative. Do not commit a new block
syntax or failure-handler syntax until the IR, bounds, error model, and
formatter contract are designed together.

## Admission and identity

Compilation proves the declared inference contract; adapter preflight proves a
configured runtime can honor it; admission authorizes one exact combination.
Preflight must report tool-loop support, prompt form, context delivery, output
mode, limits, missing bindings, and unsupported effects before provider work.

Provider/model selection and protocol implementation remain host-owned. A
versioned inference-protocol contract and its implementation revision must be
part of the admitted provider-runtime identity. Changing the internal
implementation must not change the node's structural identity, but a material
runtime/protocol change must not silently execute under an old admission
receipt. Author-visible bounds and allowed tools remain plan identity.

## Tool boundary

The first tool loop is read-oriented only. “Read-only” does not mean harmless:
repository reads, search, memory retrieval, graph queries, and database reads
can disclose sensitive data or carry prompt injection. Every internal tool call
therefore requires:

- an exact admitted descriptor and capability/resource scope;
- typed and bounded arguments and results;
- a model-visible allowlist no broader than the plan;
- per-call and aggregate deadlines, counts, payload and cost ceilings;
- stable operation and provider-call identities;
- durable evidence without unrestricted sensitive payload retention;
- host authorization at execution time.

Writes, external transactions, destructive effects, commits, pushes,
deployments, publication, email, infrastructure changes, and financial actions
remain explicit workflow activities initially. An inference may return a typed
proposal for such an action; a later explicit node performs it after normal
admission and policy checks. Temporary or isolated writes are deferred until
their idempotency, cleanup, visibility, and recovery contracts are proven.

## Durability and replay

Zhinu schedules the inference node, owns its workflow attempt, cancellation,
deadline, dependencies, checkpointing, and final result. The inference protocol
coordinator owns bounded internal turns, but persists a durable protocol journal
through the execution boundary. At minimum the journal identifies:

- activity invocation and attempt;
- protocol contract/implementation revision;
- interaction and turn number;
- model request/response operation identity;
- requested tool, tool-call identity, typed arguments/result identity;
- validation and representation-repair attempts;
- usage, cost, provider/model, timing, and possible committed effects;
- continuation/reconciliation state and terminal outcome.

Workflow retry must resume or reconcile the same invocation. It must not
silently restart a hidden loop, repeat a paid call, or duplicate a tool effect.
Exactly-once remote execution is not assumed: ambiguous provider outcomes need
idempotency keys, receipt lookup, or a conservative typed failure. Read-only
calls may be safely repeatable in effect while still differing in cost or data,
so their replay policy and evidence remain explicit.

Internal operations are not independent Fuwen nodes and do not automatically
become independent Zhinu workflow steps. The durable journal may use adapter
storage or nested Zhinu primitives, but recovery behavior is part of the
versioned protocol contract rather than an in-memory implementation detail.

## Validation and repair

Representation recovery may remain inside `infer`: bounded JSON extraction,
Nuwa-compatible repair, deserialization, and final declared-schema validation
are mechanics for obtaining the requested result. Repair success never
substitutes for final validation.

Semantic rejection remains workflow-visible. Failed tests, policy violations,
unacceptable architecture, inadmissible plans, and requests to mutate external
state should produce typed results/failures that explicit Fuwen control flow can
review, retry, replan, checkpoint, or escalate. Hidden semantic replanning is
not enabled merely because the protocol supports several turns.

## Prompt and context ownership

Business prompts remain explicit plan definitions or exact registered aliases.
If future workflows accept dynamically produced instruction text, it is runtime
input—not a mutation of the immutable workflow definition—and its exact value
must enter request identity and evidence under host policy. Context remains
explicit, bounded, provenance-bearing, and adapter-mapped; artifacts are never
implicitly dereferenced.

## Evidence ownership

Fuwen defines provider-neutral evidence and failure contracts. Zhinu durably
associates evidence with execution and recovery. Baize handles provider
transport and provider metadata. Nuwa may assist representation recovery.
Hongxian may correlate and present longer-lived history, but is optional and is
not the authoritative workflow store or the only place internal evidence lives.

Evidence should answer why the logical activity produced its result without
retaining credentials, unrestricted provider payloads, raw sensitive context,
or chain-of-thought. Prefer typed summaries, identities, hashes, usage, artifact
references, and access-controlled payload storage.

## Initial delivery sequence

1. Publish an adapter feature manifest and preflight report for the current
   one-call `infer` behavior.
2. Specify the versioned inference-protocol definition, aggregate bounds,
   normalized outcomes, internal operation identities, and durable journal.
3. Add a host-supplied exact tool-execution port and deterministic fake
   model/tools; support read-oriented calls only.
4. Implement bounded model → tool → model interaction with cancellation,
   replay/reconciliation, typed evidence, and final output validation.
5. Add source-to-Zhinu-to-provider recovery tests across sequential, repeat,
   and fan-out regions, including crash points around every paid call and tool
   result.
6. Ship a credential-free sample that shows compile, preflight, admit, execute,
   fail, resume, inspect evidence, and promote a proposed side effect to an
   explicit workflow activity.

Do not add other complex activity kinds until this protocol is useful in at
least two consumers and its recovery/evidence boundaries remain stable.

## Acceptance criteria

- authored Fuwen contains one logical `infer` node, not generated protocol
  loops;
- unsupported runtime combinations fail preflight before registration or paid
  work;
- exact declared tools are the only model-visible and executable tools;
- aggregate source limits can narrow but never expand finite host limits;
- every internal paid/tool operation has stable identity and durable evidence;
- crash/retry resumes or reconciles without silently duplicating an operation;
- semantic recovery and external effects remain explicit workflow decisions;
- changing plan semantics changes the execution fingerprint, while changing a
  host protocol implementation invalidates the old admission/runtime identity;
- no evidence path requires Hongxian, raw chain-of-thought, or embedded secrets.

In short: compile away the protocol, not the procedure, and never compile away
the evidence.
