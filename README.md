# Penghou.Fuwen

Penghou.Fuwen is a proposed typed language and compiler for portable,
artifact-driven AI workflows. It turns bounded, capability-reviewed source into
a canonical immutable executable plan. Penghou.Zhinu executes that plan;
Fuwen does not replace the workflow engine.

The same language is intended to coordinate code, documents, datasets,
research, images, audio, video, model outputs, manifests, and validation
evidence. These are represented by typed immutable artifact references rather
than domain-specific grammar or filesystem paths.

## Why Fuwen

Applications currently need substantial custom code to connect inference,
context, activities, artifacts, retries, validation, and durable workflow
execution. Fuwen makes those relationships explicit and reviewable before a
workflow runs:

- typed inputs, outputs, bindings, and artifact references;
- trusted activity, context, inference-profile, template, and tool catalogues;
- capability and resource-budget analysis;
- stable structural node identities and execution fingerprints;
- canonical immutable IR suitable for durable execution and audit;
- portable semantics without embedded C#, Python, JavaScript, or shell.

## Ecosystem responsibilities

- **Fuwen** owns source syntax, binding, typing, capability analysis, canonical
  IR, fingerprints, source maps, and diagnostics.
- **Zhinu** owns durable execution, scheduling, retries, fencing, signals,
  fan-out, restart, cancellation, child workflows, and compensation.
- **Baize** owns provider-neutral inference and records the provider/model,
  tools, usage, and execution provenance actually used.
- **Nuwa** repairs malformed JSON; successful repair never replaces final
  schema validation.
- **Cangjie and Hetu** provide memory and code/context knowledge. Fuwen carries
  immutable snapshot references.
- **Hongxian** may correlate long-lived sessions later but is not workflow
  state or execution.
- **Artifact providers and hosts** own bytes, storage, authorization, routing,
  resource handles, policies, and budgets.

## Initial packages

- `Penghou.Fuwen` — provider-neutral executable-plan, type, identity, artifact,
  descriptor, capability, and diagnostic contracts.
- `Penghou.Fuwen.Compiler` — bounded validation and compilation into canonical
  Fuwen IR.

`Penghou.Fuwen.Zhinu` and `Penghou.Fuwen.Baize` will be added only after the IR
and identity design gate closes.

## Current status

The executable-plan identity and storage foundation is complete. Fuwen now has
an IR v2 structured execution schedule, immutable canonical definitions,
trusted catalogue resolution, and a constrained programmatic builder/compiler
that rejects invalid references, projections, return types, untrusted schema
changes, capability mismatches, missing host grants, callable signature
mismatches, and unsafe callable retry/effect combinations.

The compiler deliberately separates three boundaries: canonical-definition
integrity, semantic compilation, and host admission. Trusted callable signatures
and conservative side-effect/idempotency/retry checks are implemented.
`WorkflowAdmissionService` can now issue an opaque in-process receipt bound to
the exact execution fingerprint, immutable catalogue snapshot, resolved trusted
metadata, finite capability grants, policy revision, and effective limits.
Unversioned catalogues and policies—and the test-only `AllowAll` policy—cannot
issue a receipt. Parser work remains deferred while revision lineage and
semantic plan comparison are completed.

See [the roadmap](docs/roadmap.md) and
[the first implementation batch](docs/first-batch.md).
