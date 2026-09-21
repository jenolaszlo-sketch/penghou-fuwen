# ADR 0007: Registered prompt aliases retain host-owned identity

## Status

Accepted, 2026-09-21.

## Context

Fuwen source can declare a typed prompt alias with `uses registered`, but the
alias owns no inline messages. Treating it as a workflow-owned prompt therefore
produces an empty render and loses the exact host-owned prompt-template
identity. Deferring discovery until a run also permits a workflow to register
successfully even though its executor cannot honor the admitted descriptor.

## Decision

A registered prompt declaration is an alias for its exact
`PromptTemplate` descriptor. Its declared parameter names are the host runtime
argument names: the interpreter evaluates bindings with the existing typed
binding rules and passes bound values in declaration order. Unbound optional
parameters are omitted. The interpreter does not render inline messages for
this shape.

The execution request carries the exact registered template, while its request
identity retains the alias semantic digest and evaluated arguments. Runtime
evidence records the exact `PromptTemplate`; `PromptDigest` remains reserved for
workflow-owned inline prompts. This keeps replay identity sensitive to both the
immutable alias source and its runtime values without pretending the workflow
owns the provider prompt body.

Executors that support registered aliases expose descriptor preflight. The
Zhinu adapter resolves every admitted inference requirement before storing or
registering the workflow. A missing profile/template pair, missing tool, or
changed descriptor is an admission failure and no provider work begins. An
executor without preflight support may still execute inline prompts and direct
templates, but Zhinu rejects registered aliases because it cannot prove their
host binding before registration.

Baize resolves registered aliases by exact profile and prompt-template
descriptor. Its exact-descriptor router applies the same rule and delegates
additional checks to the selected executor when available.

## Consequences

- Registered aliases execute without inventing an empty inline prompt.
- Parameter mapping is explicit, typed, deterministic, and stable under replay.
- Descriptor drift fails before definition storage, scheduling, or provider
  invocation.
- Provenance distinguishes exact host-owned templates from workflow-owned
  prompt digests.
- Renaming a host argument requires a new alias declaration or a future
  explicit mapping feature; implicit name translation is not supported.
