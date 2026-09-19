# ADR 0005: Inference contracts are self-describing

## Status

Accepted, 2026-09-19.

## Context

An inference node references host-registered prompt templates and inference
profiles by digest. The source stays compact, but the workflow does not
contain the instructions each inference executes or the tools it may invoke.
Guyabano-generated workflows therefore describe the graph without describing
the work, and a supervisor-authored workflow cannot be understood or executed
without the authoring host's hidden prompt configuration.

## Decision

A Fuwen workflow describes the inference contract: first-class typed prompt
declarations owned by the workflow (with registered prompts allowed only as
explicit versioned descriptor references), typed prompt bindings, context
requirements as typed dependencies rather than interpolated text, an explicit
tool allowlist defaulting to none, the output contract, a logical inference
profile, and execution limits.

Fuwen describes WHAT an inference may do. The host decides WHETHER it is
authorized and HOW it is executed. Provider selection, credentials,
authorization, resource handles, routing, and policy remain host
responsibilities and stay out of the language. Profiles remain logical host
vocabulary, never provider or model names.

Prompt content, bindings, registered-prompt identity, and the resolved tool
surface contribute to execution identity, so prompt and tool changes are
visible in plan comparison and invalidate dependent nodes on mutation.
Source-declared tools request an environment; they never grant capabilities,
and admission requires exact host resolution of every declared tool.

## Consequences

- The grammar and canonical IR gain prompt declarations, prompt references
  with typed bindings, and tool declarations. Existing v3–v7 plans keep their
  current semantics behind IrVersion gating; the new semantics ship under a
  new IR version.
- The trusted catalogue gains tool descriptors with side-effect, idempotency,
  and retry metadata. Admission verifies exact tool resolution against host
  policy.
- Prompt substitution is typed and bounded: parameters use Fuwen types, every
  parameter is validated at compile time, templates cannot execute arbitrary
  expressions or reach undeclared workflow state, and bindings evaluate in the
  parent region.
- Host-injected instructions must be pinned to a policy or descriptor revision
  and represented in identity; untracked prompt mutation is not allowed.
- Guyabano generation guidance, the workflow-authoring pack, and Marang
  supervisory-authoring requirements follow in later work: every generated
  inference must be a complete contract, and plans depending on undeclared
  external prompt or tool state are rejected.
- Write-capable tools wait for durable side-effect semantics; the first
  implementation admits only none, read-only, and demonstrably idempotent
  operations.
