# ADR 0011: Aggregate inference budgets reserve conservatively

## Status

Accepted, 2026-09-21.

## Context

Per-call model limits do not bound a multi-turn inference. A protocol can
exceed an author's intended spend, duration, token, tool, or payload ceiling by
repeating individually valid calls. Hosts also impose ceilings, and usage or
pricing may be unavailable, delayed, estimated, or reported under a changed
pricing revision. Treating unknown usage or price as zero would authorize work
that cannot be proven to fit the remaining budget.

Aggregate enforcement must remain deterministic under restart, replay,
concurrency, and ambiguous provider outcomes while preserving the distinction
between compiler budgets, model output limits, protocol totals, deadlines,
estimated cost, and committed spend.

## Decision

Complex inference has immutable aggregate limits for protocol turns, model
calls, tool calls, prompt/completion/total tokens, logical-activity duration,
tool argument/result bytes, retained conversation/evidence bytes, and—when the
host supplies trusted pricing—cost in an explicit currency and pricing
revision. Existing v3–v8 one-call limits retain their current meaning and are
not silently reinterpreted as aggregate v9 limits.

For every finite dimension, the effective bound is the minimum of the source
bound and the host/runtime ceiling. Source declarations may narrow a host
ceiling but may never expand it. Every operation is admitted only after a
reservation against the remaining aggregate budget. Reservations include the
operation's known upper bound where available, are atomically committed with
the journal claim when concurrency or external commitment requires it, and are
released or settled using the recorded outcome. A provider call is not started
when its reservation cannot be proven to fit.

Unknown token usage, unknown price, or a stale/incompatible pricing revision
is never normalized to zero. If the uncertainty prevents proof that the next
paid operation fits the effective budget, the coordinator stops before that
operation with a normalized budget/usage-unknown outcome. A host may provide a
conservative bounded estimate and quality marker; an estimate does not become
committed spend until provider evidence settles it. Ambiguous completion
remains an ambiguous operation outcome even when a reservation existed.

Duration and cancellation deadlines are checked before and during the
activity. Counts, bytes, tokens, and cost are accumulated from durable
operation evidence, so replay reuses settled accounting rather than charging a
completed operation twice. Unknown or unreconciled prior usage consumes the
conservative reservation until reconciliation or explicit operator resolution.
Budget exhaustion is reported through stable Fuwen outcomes such as turn,
model-call, tool-call, token, cost, deadline, or usage/pricing-unknown limit;
provider-specific exception types do not cross the contract boundary.

Hosts expose pricing and ceiling provenance as part of admission/runtime
identity. A change to pricing revision or limit semantics invalidates the old
admission for new paid work; it does not rewrite historical usage. The host
may choose to reject unknown pricing at preflight, or admit it only when the
effective policy can still prove a finite conservative bound.

## Consequences

- A bounded protocol cannot spend unbounded resources through repeated valid
  per-call operations.
- Source budgets compose safely with host ceilings and cannot escalate them.
- Unknown usage and pricing fail closed before an unprovably affordable call,
  rather than becoming accidental free capacity.
- Durable reservations and settled evidence make accounting restart- and
  replay-safe, including concurrent or ambiguous operations.
- Cost limits are optional only when the host lacks trusted pricing; token,
  count, byte, and time limits remain independently enforceable.
