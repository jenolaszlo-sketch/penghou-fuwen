# Penghou.Fuwen: pending Penghou.Hufu integration

Status: **Pending integration; not implemented.** Recorded 2026-09-28, amended
2026-10-06 by [ADR 0012](decisions/0012-defer-luban-decouple-hufu-from-command-language.md):
Hufu authority MUST be language-neutral and MUST NOT depend on Penghou.Luban
(deferred/parked).

Penghou.Hufu is the new reusable authority library and authority-store boundary.
It currently contains a buildable scaffold and design documents, with no public
authority API, enforcement implementation, or persistent authority store.
This note records future consumer work; it does not announce a package dependency,
a shipped security guarantee, or an additional current-release acceptance gate.

Hufu will own reusable grants, envelopes, authority requests and decisions,
attenuation, revocation, and durable authority records. Hosts retain identity,
policy, credentials, resource resolution, and approval surfaces. Zhinu retains
execution state and recovery; existing budget services retain accounting.

## Fuwen's planned integration

- Represent typed Hufu requirements per activity in the DSL/IR, including
  bounded resource scopes, conditions, and delegation ceilings.
- Include authority-sensitive semantics in canonical plan identity and expose
  per-node and conservative workflow summaries.
- Bind trusted capability/evaluator versions and resolved resource identities
  into exact host admission through the appropriate integration boundary.
- Expose structured missing-authority diagnostics and reject unsupported
  required contracts before affected execution.

Fuwen continues to own compilation and plan semantics. It must not become the
grant issuer, credential resolver, live authority store, or resource sandbox.
Existing catalogue/capability checks are foundations, not proof of Hufu integration.

Fuwen and Hufu MUST NOT take a dependency on Luban syntax, commands, cmdlets,
or language semantics. Authority is expressed as neutral resource/effect
operations (e.g. `filesystem.read`, `http.request`, `process.execute`) and
native execution delegates to Gagamba; a future Luban frontend MAY produce the
same execution requests without Hufu changes.

## Completion evidence

Requirement changes alter identity; unknown or uncovered requirements cannot
reach protected I/O; a host can consume the summary and bind the exact plan to
a valid Hufu admission. Coordinate with the existing FI-04 resource-grant work
rather than building a competing authority model.

## Dependency and design home

Implementation depends on Hufu's reviewed contracts, durable-store semantics,
and a proven host/resource-broker enforcement path. Continue current correctness
work independently; do not add placeholder dependencies or infer security from
the existence of Hufu's scaffold.

Canonical design (links assume sibling checkouts):

- [Hufu architecture](../../Penghou.Hufu/docs/architecture.md)
- [Authority specification](../../Penghou.Hufu/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](../../Penghou.Hufu/docs/roadmap.md)
