# ADR 0001: Fuwen is a compiler, not a workflow runtime

## Status

Accepted.

## Decision

Fuwen parses and validates bounded source and emits canonical immutable IR with
stable identities and a capability manifest. Penghou.Zhinu remains authoritative
for durable execution, scheduling, retries, fencing, fan-out, signals, restart,
cancellation, child workflows, and compensation.

One generic Zhinu workflow will eventually interpret verified Fuwen IR. Fuwen
will not generate application-specific CLR workflow classes or persist a second
authoritative workflow state machine.

## Consequences

- Core and compiler packages have no Zhinu dependency.
- Runtime integration belongs in `Penghou.Fuwen.Zhinu` after the IR design gate.
- Syntax cannot promise behavior Zhinu cannot durably enforce.
- Fuwen execution identity and Zhinu run/step identity remain correlated but
  distinct.
