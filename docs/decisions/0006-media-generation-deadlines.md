# ADR 0006: Media generation uses one effective local deadline

## Status

Accepted, 2026-09-21.

## Context

Media generation can span provider submission, durable-operation polling, and
artifact publication. Fuwen inference nodes may declare a timeout while the
host also configures a trusted generation timeout. Enforcing only the host
timeout silently weakens the admitted plan, while treating local cancellation
as proof of remote rollback would overstate what the executor knows.

Token limits are meaningful for structured-text providers but are not a
supported media-generation control in the current Baize adapter.

## Decision

The generation executor uses the shorter of the plan-declared timeout and the
host generation-policy timeout. The effective deadline starts immediately
before provider submission and covers submission, polling, artifact
publication, and publication-receipt validation. Request mapping and admission
checks occur before this provider-work interval.

The executor classifies outcomes as follows:

- caller cancellation remains `OperationCanceledException` and is not relabeled
  as a timeout;
- expiry of the effective local deadline returns a typed, non-retryable timeout
  with `PlanDeadlineExceeded` or `HostDeadlineExceeded` provenance;
- a provider-reported timeout remains `TimeoutExceeded`, distinct from either
  local deadline;
- local deadline failures set `MayHaveCommittedEffect` because submission or
  publication may have committed remotely before local observation stopped;
- local cancellation does not claim provider rollback and does not issue an
  unverified compensating cancellation;
- `MaxTokens` is rejected before provider invocation because media generation
  does not currently implement that limit.

An executor may resume safely only through the existing operation-key,
operation-handle, and idempotent-publication contracts. A timeout never grants
permission for a fresh semantic retry.

## Consequences

- Host operators can tighten a plan deadline but cannot loosen it.
- Submission, polling, and publication share one budget instead of receiving
  independent full-duration timeouts.
- Evidence distinguishes plan, host, provider, and caller-controlled stopping
  conditions without asserting that remote work was undone.
- A future media-specific token or cost control requires a new explicit
  contract rather than silently reinterpreting `MaxTokens`.
