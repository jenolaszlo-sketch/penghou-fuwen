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
- `Penghou.Fuwen.Zhinu` — admission-bound registration and, incrementally,
  durable interpretation of immutable Fuwen plans on Zhinu.
- `Penghou.Fuwen.Baize` — exact descriptor-bound structured inference through
  Baize, with bounded trusted retries, Nuwa repair, final type validation, and
  provider-neutral provenance.

## Current status

The executable-plan identity and storage foundation is complete. Fuwen now has
an IR v2 structured execution schedule, a pre-release IR v3 typed context-request
contract, immutable canonical definitions,
trusted catalogue resolution, and a constrained programmatic builder/compiler
that rejects invalid references, projections, return types, untrusted schema
changes, capability mismatches, missing host grants, callable signature
mismatches, and unsafe callable retry/effect combinations.

The bounded `.fuwen` source compiler now covers the minimal Delivery D
language: schemas, enums, typed workflows, named context/activity/inference
nodes, restricted bindings and conditions, control-only `if/else`, complete
typed returns, canonical formatting, source maps, and stable diagnostics.

The compiler deliberately separates three boundaries: canonical-definition
integrity, semantic compilation, and host admission. Trusted callable signatures
and conservative side-effect/idempotency/retry checks are implemented.
`WorkflowAdmissionService` can now issue an opaque in-process receipt bound to
the exact execution fingerprint, immutable catalogue snapshot, resolved trusted
metadata, finite capability grants, policy revision, and effective limits.
Unversioned catalogues and policies—and the test-only `AllowAll` policy—cannot
issue a receipt. Immutable plan-revision documents
now bind host-issued lineage IDs and parentage to one verified definition plus
content-addressed objective, acceptance, validation, and supporting artifact
evidence. Their fingerprints prove integrity only; they do not authorize or
activate execution. Deterministic plan comparison reports exact structural-path,
dependency, descriptor, execution-order, objective, acceptance, and validation
changes. It is an explanation surface, not permission to reuse runtime artifacts.
`WorkflowExplanation` now projects completed compilation or admission results
into a deterministic, side-effect-free view of the plan, descriptor pins,
required capabilities, trusted callable effect summaries, repair-oriented
guidance, limits, usage, and bounded diagnostics. Effect summaries and repair
guidance are descriptive evidence only: the projection cannot issue an
admission receipt, grant capabilities, or execute workflow work.
Trusted catalogue results are also charged cumulatively against the existing
structural and UTF-8 text budgets, preventing many individually valid descriptor
payloads from amplifying compiler memory beyond the admitted limits.
Caller-controlled plan text and JSON literals are preflighted before snapshot
allocation, artifact references enforce artifact descriptor kinds, and every
supported IR version now requires its exact compiler-semantics contract.
Recursive named-schema graphs are rejected deterministically, enum literals are
checked against resolved serialized member values, and the canonical JSON v1
number domain now rejects lossy floating-point fallback. Matching .NET and
independent Python vectors cover decimal boundaries, negative zero, Unicode
escaping, non-BMP text, and ordinal property ordering.
Provider results can now cross a bounded, provider-neutral runtime boundary as
detached JSON, artifact identities, and bounded list/object composites. Strict
runtime validation covers optional, list, object, enum, numeric, and nominal
artifact types, while immutable context
snapshot references preserve selection evidence without storing context content
or granting artifact access. IR v3 inference nodes declare exactly which typed
context-node outputs they consume; runtime snapshot evidence remains separate
from that compiled request. See [runtime evidence](docs/runtime-evidence.md).
Provider-neutral execution ports now carry these values, exact descriptor
identities, declared output types, canonical operation keys, typed failures,
and non-authoritative observations without taking ownership of admission,
retry, scheduling, credentials, or artifact access. See
[execution ports](docs/execution-ports.md).
`Penghou.Fuwen.Zhinu` now verifies admission, provider-runtime identity,
immutable definition storage, and exact Zhinu workflow fingerprinting before
registration. Its first executable slice interprets IR v3 context, inference,
activity, conditional, and return nodes through stable durable Zhinu steps,
validates provider outputs at their declared Fuwen types, and preserves context
snapshot evidence. SQLite-backed tests prove crash recovery, selective restart,
run-scoped operation keys, definition-drift rejection, cancellation, corrupt
evidence rejection, and idempotent step-owned artifact publication. See
[the Zhinu adapter boundary](docs/zhinu-adapter.md).
`Penghou.Fuwen.Baize` now maps exact host-owned logical profile/template
bindings to Baize endpoints without leaking application profile names into the
provider layer. Recorded tests distinguish malformed, repaired-but-invalid,
schema-mismatched, truncated, tool-mapping, policy, and provider failures;
only explicitly classified fallback or representation-retry cases may cause
another model call. Evidence records modality, attempt-level and aggregate
usage, duration, host-priced cost, and pricing revision. Descriptor-bound image,
video, and audio generation requires crash-safe idempotent submission, polls a
pinned provider operation, and returns host-verified artifact publication
receipts. Exact routing composes structured-text and media inference in one
workflow executor.

IR v4 also contains the first programmatic keyed fan-out contract. It validates
all stable item keys before child work, persists one durable item outcome per
key, aggregates in source order, and supports focused item restart without
repeating successful siblings. The `.fuwen` source syntax and Guyabano pilot
for fan-out remain roadmap work, so this surface should be treated as preview.

See [the roadmap](docs/roadmap.md) and
[the first implementation batch](docs/first-batch.md).
