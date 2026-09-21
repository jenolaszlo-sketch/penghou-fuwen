# ADR 0009: Inference protocol mechanics stay behind one logical activity

## Status

Accepted, 2026-09-21.

## Context

The existing `infer` node already expresses a workflow decision: a prompt,
typed inputs, context, profile, allowed tools, output contract, and limits.
An implementation may need several model turns, read-oriented tool calls,
representation repair, and final validation to produce that result. Expanding
those provider mechanics into authored Fuwen nodes would make plans larger and
provider-dependent, while hiding semantic decisions such as research,
review, rework, or external effects would make workflows harder to inspect and
recover.

The first complex-activity release must also preserve IR v3–v8 bytes and the
current one-call executor behavior. A public generic activity-handler surface
would prematurely freeze an abstraction without a second proven protocol.

## Decision

The first complex activity is the existing `infer` node. Its implementation
may run a versioned, bounded inference protocol internally, but the authored
workflow observes one logical activity and one typed result. The protocol may
perform normalized model turns, exact read-only tool calls, bounded
representation recovery, and final declared-schema validation.

Fuwen remains the authority for authored meaning: prompt or exact registered
alias, typed bindings and context, profile, exact tool descriptors, output
contract, aggregate limits, dependencies, and surrounding control flow.
Zhinu owns durable protocol coordination and recovery; Baize owns one
provider/model turn; the host owns authorization and read-tool execution.
None of these boundaries expose provider clients, credentials, product types,
or Zhinu contexts through Fuwen contracts.

No public generic `IComplexActivityHandler`, arbitrary activity-kind plugin
point, or generic hidden workflow scripting escape hatch is introduced in this
release. A second protocol may motivate a future common abstraction only after
its contracts, recovery, evidence, and consumer boundaries have been proven.

The internal protocol is versioned independently of the workflow's structural
identity. A material protocol implementation change requires a new admitted
provider-runtime/protocol identity; it does not change the workflow
fingerprint unless authored plan semantics change. IR v3–v8 definitions and
their canonical bytes continue through their existing one-call path. Complex
inference is introduced through the separately versioned contract planned for
IR v9 and is rejected by adapters that cannot preflight that revision.

Only effect-free and read-only tools are in scope initially. Writes,
transactions, destructive changes, commits, publication, deployment, email,
and other external effects remain explicit workflow activities. An inference
may return a typed proposal for a later activity, but it cannot perform that
effect as hidden protocol work.

## Consequences

- Authored plans remain compact, product-neutral, and provider-independent.
- Operators see one logical inference with explicit evidence for its internal
  turns, tools, validation, limits, and terminal outcome.
- Semantic rejection, replanning, and external side effects remain visible
  workflow decisions rather than hidden model behavior.
- Existing v3–v8 persisted plans and one-call executors remain compatible.
- The initial API is deliberately narrower; a generic activity abstraction is
  deferred until a second durable protocol supplies evidence for its shape.
