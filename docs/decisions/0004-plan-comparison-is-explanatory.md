# ADR 0004: Plan comparison is explanatory

## Status

Accepted, 2026-09-07.

## Decision

Fuwen compares two verified executable definitions and their immutable lineage
records deterministically. Exact structural paths establish node
correspondence. A renamed path is therefore reported as one removed node and
one added node rather than guessed as a rename.

The comparison separates an element's own executable semantics from its
dependencies. It also reports workflow-level execution-order and contract
changes and content-identity changes to the objective, acceptance criteria, and
validation requirements. Results are ordered by structural path and bounded by
the admitted plan limits.

## Consequences

- Display names and similarity heuristics cannot silently establish identity.
- A changed callable descriptor is visible both at the workflow catalogue and
  at each affected node; this deliberate redundancy supports overview and
  local explanation.
- A path rename may also change execution-order dependencies. The comparison
  reports both facts rather than suppressing one.
- The result does not authorize execution, restart, transition activation, or
  artifact reuse. A later Fuwen.Zhinu adapter must combine it with durable
  runtime state, artifact provenance, current inputs, evidence freshness, and
  host policy.
