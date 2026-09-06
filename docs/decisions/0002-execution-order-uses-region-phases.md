# ADR 0002: Execution order uses structured region phases

## Status

Accepted for IR v2 implementation.

## Context

IR v1 derives data dependencies from bindings and canonicalizes sibling nodes
by structural path. That is sufficient for identity experiments, but it does
not state when independent side-effecting nodes must complete. In particular,
the v1 golden plan contains a validation activity and a return that both depend
on inference; path sorting must not decide whether validation precedes return.

Generic `before`, `after`, or `dependsOn` edges would expose an unrestricted
graph surface, obscure structured branch boundaries, and make source order an
accidental execution contract.

## Decision

IR v2 will add a versioned execution schedule made of lexical regions and
ordered phases. A region is the workflow root or one structured branch. Every
node directly owned by a region appears exactly once in one phase.

- Phases are ordered completion barriers.
- Nodes in one phase are unordered and may execute concurrently.
- Node paths inside a phase are canonicalized using ordinal ordering.
- A binding may consume a node output only from an earlier phase in the same
  region in the initial profile.
- Cross-region values require future explicit region input/output ports; raw
  cross-branch references are rejected.
- A conditional node completes only after its selected branch region completes.
- The root has exactly one return node. It is the sole member of the final root
  phase, cannot be an output source, and must produce the declared output type.
- Branch-local returns are rejected in the initial v2 profile.

Validation remains an ordinary trusted activity. Placing it in a phase before
the return creates a completion barrier; a Boolean result has no implicit
control meaning and must be consumed by an explicit condition when it gates
success.

## Compatibility and identity

This changes executable meaning and therefore will not reinterpret IR v1.

- IR becomes `fuwen-ir/v2`.
- Execution fingerprints become `fuwen-execution/v2`.
- Compiler semantics become an exact supported v2 contract.
- Canonical JSON remains `penghou-canonical-json/v1`.
- `workflow_plan_v1.json` remains a historical vector; v2 receives a new
  canonical fixture and independent verification vector.

IR v1 may remain loadable as historical content, but it is not admitted for v2
execution and no default schedule is inferred for it.

## Consequences

- Declaration order and canonical JSON property order never authorize work.
- Independent siblings retain order-independent identity.
- Required completion order is explicit and fingerprinted.
- The future Zhinu adapter can lower phase barriers to durable dependencies
  while Zhinu remains authoritative for execution, fencing, retry, restart,
  scheduling, and recovery.
- Admission must reject missing or duplicate schedule entries, unknown or
  wrongly scoped paths, empty phases, same/forward/cross-region output
  references, missing or non-final returns, and incompatible return types.
- Loops, fan-out, waits, and child workflows remain first-class structured
  regions rather than backward edges.
