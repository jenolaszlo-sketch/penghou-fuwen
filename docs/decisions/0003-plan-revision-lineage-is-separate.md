# ADR 0003: Plan-revision lineage is separate from executable IR

## Status

Accepted, 2026-09-07.

## Decision

Adaptive planning produces immutable lineage documents outside `WorkflowPlan`.
A host assigns each lineage node an opaque `PlanRevisionId`; a canonical
content-derived envelope fingerprint binds that identity, its optional direct
parent, one verified execution fingerprint, explicit objective/acceptance/
validation content identities, and bounded immutable artifact references.

The same execution fingerprint may appear in multiple plan revisions. This
supports reactivation, a new decision, or branching without manufacturing a
semantic difference in the executable plan. A revision store must bind each
host-issued ID to one immutable envelope and reject changed reuse.

`WorkflowPlan.Revision` remains part of the published v1/v2 execution identity.
It is not silently replaced by `PlanRevisionId`; clarifying or renaming that IR
field requires a future versioned contract.

## Consequences

- Lineage metadata cannot perturb executable identity accidentally.
- Parentage expresses history, not activation order or deployment precedence.
- Proposal, reason, and evidence content remains in artifact providers rather
  than embedding free-form or hidden reasoning in Fuwen state.
- The envelope fingerprint proves integrity only. It does not identify an
  issuer, grant capabilities, authorize execution, activate a revision, create
  a Zhinu generation, or approve artifact reuse.
- Full ancestry traversal, cycle detection across documents, branch selection,
  retention, and authorization remain host/store responsibilities.
