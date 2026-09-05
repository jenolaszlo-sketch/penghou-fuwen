# Penghou.Fuwen Roadmap

## Status

**Delivery milestone A complete — Delivery milestone B next**

Last reviewed: **2026-09-01**

Fuwen is the proposed typed authoring and compilation layer for portable,
capability-reviewed AI workflows. It produces an immutable executable plan; it
is not another workflow engine. This roadmap consolidates the proposal,
architecture review, and semantic-contract addendum so work can resume without
reconstructing the design from chat history.

Fuwen is artifact-driven rather than code-generation-driven. Source code,
documents, datasets, reports, images, audio, video, prompts, model outputs, and
manifests are all host-described artifacts. Code generation is the first
demanding pilot, not a privileged language concept.

`WorkflowPlan` is compiled structured workflow semantics. Its canonical JSON
is a portable serialization of that IR, not a second authoring language or a
generic executable-graph format. Node identifiers and references express
dependencies and structured regions; they cannot be used to introduce
arbitrary control-flow cycles.

## Purpose

Penghou already provides durable execution, inference, structured-output
repair, memory, code knowledge, artifacts, and long-lived sessions. Applications
still need application code to compose them. Fuwen fills that gap with a
human-readable and LLM-generatable language that can be parsed, type-checked,
capability-reviewed, fingerprinted, stored, and executed without embedding
arbitrary executable source.

```text
.fuwen source
      |
      v
Fuwen compiler + trusted catalogues + host policy
      |
      v
canonical immutable IR + capability manifest
      |
      v
Penghou.Fuwen.Zhinu -> Zhinu durable execution
      |                    |
      |                    +-- registered activities/context providers
      +-- IInferenceExecutor <-- Penghou.Fuwen.Baize
```

## Responsibility boundaries

Fuwen owns syntax, formatting, schemas, restricted expressions, binding, type
checking, catalogue resolution, graph validation, capability analysis, stable
node identity, canonical IR, fingerprints, source maps, and diagnostics.

Existing components remain authoritative:

- **Zhinu:** persistence, scheduling, steps, fencing, retries, signals, fan-out,
  child workflows, compensation, restart, cancellation, and recovery state.
- **Baize:** provider-neutral inference, endpoint capabilities, actual model
  selection, tools, structured output, media generation, usage, and inference
  provenance. Host policy translates logical profiles into Baize requirements.
- **Nuwa:** JSON repair; repair success and final schema validation are distinct.
- **Hongxian:** optional long-lived session correlation, never Fuwen execution.
- **Cangjie, Hetu, and other context systems:** knowledge/query semantics.
  Fuwen records immutable context-snapshot references.
- **Artifact providers:** content and storage. Fuwen carries typed references.
- **The host:** authorization, trusted catalogues, resource-scoped grants,
  routing policy, budgets, and interaction presentation.

## Non-goals

Fuwen is not a workflow runtime, general-purpose language, authorization system,
artifact store, memory, code graph, session store, model-routing policy, or
replay log for Zhinu. It does not embed C#, Python, JavaScript, Lua, shell,
reflection, dynamically loaded code, mutable globals, arbitrary functions,
recursion, unbounded loops, lambdas, or general collection queries. When a
feature resembles general-purpose programming, prefer a trusted activity. Its
JSON IR is not an unrestricted graph API: generic node references cannot encode
backward jumps, irreducible control flow, or serializer-level object cycles.

## Artifact-driven semantics

Fuwen has two complementary value categories:

- **Inline immutable values** carry bounded control data such as booleans,
  enums, identifiers, small records, decisions, scores, and typed parameters.
- **Artifact references** carry durable or potentially large products such as
  source files, generated media, documents, datasets, model responses, reports,
  manifests, and validation evidence.

An artifact reference is provider-neutral and immutable. Its minimum semantic
identity includes a provider/namespace, opaque artifact identity, content hash,
and a declared schema or media descriptor. Optional provenance, size, logical
name, and related-artifact references may be present when the host supports
them. Host-issued resource handles are distinct from artifact references.

Nodes consume and produce typed values, typed artifact references, or bounded
collections of either. Activities and inference executors may publish new
artifacts and return verified publication receipts. Fuwen carries identities,
types, dependencies, and receipts; it does not read arbitrary paths, store
artifact bytes, define repository layouts, mutate an artifact in place, or
invent one universal artifact lifecycle. Validation, approval, selection, and
promotion are explicit typed activities or decisions that produce new evidence
or references rather than rewriting history.

## Accepted invariants

1. Fuwen is a compiler and semantic layer; Zhinu is the execution machine.
2. One generic Zhinu workflow executes immutable validated IR. Fuwen does not
   generate CLR workflow classes.
3. Every executable node is explicitly named and has a structural identity
   independent of source position and unrelated sibling order.
4. Renaming a node changes identity and execution fingerprint.
5. Runtime identity derives from structural identity plus explicit loop or
   fan-out identity.
6. Values are immutable; loop-carried state and results are explicit.
7. Fan-out requires stable item keys and explicit `yield`; aggregation preserves
   source collection order.
8. Context retrieval is a durable dependency producing an immutable snapshot.
   Inference retry reuses it; context restart creates a new revision and
   invalidates dependents.
9. Source fingerprint, execution fingerprint, and runtime provenance differ.
10. Model-profile names are host vocabulary. Baize receives normalized
    requirements and records the model actually used.
11. Fuwen.Zhinu uses `IInferenceExecutor`, not a direct Baize dependency.
12. Catalogue entries are trusted host definitions. Source cannot grant or
    expand capabilities, resource scopes, or budgets.
13. Resources use host-issued opaque handles or artifact references, not
    source-controlled filesystem roots.
14. Every inference has host-enforced tool, time, token, cost, and concurrency
    limits even when source omits them.
15. Compilation is resource-bounded because source is untrusted.
16. Security-oriented types such as `secret<T>` are not exposed until their
    semantics are enforced end to end.
17. Artifact references are first-class typed immutable values. Their content
    lives behind provider/host contracts and is never embedded into workflow
    state merely to make it durable.
18. Core node and type names remain domain-neutral. Terms such as repository,
   source file, build, prompt, image, or video belong to descriptors, schemas,
   catalogues, and applications rather than the grammar or core IR.
19. Repeated execution remains explicit in semantic constructs such as loops,
   keyed fan-out, retry regions, polling, and durable waits. It is never
   inferred from a generic reference to an earlier node.
20. Structural identity is independent of runtime execution identity. Fuwen
   identifies a loop body structurally; the adapter and Zhinu identify a
   durable iteration, fan-out item, retry attempt, or resumed wait.
21. Structured regions have closed reference boundaries. Values enter through
   declared inputs and leave through declared outputs; nodes cannot jump into
   or out of an internal body by referencing implementation nodes.
22. Repetition safety is explicit and fingerprinted. A construct declares its
   applicable bound, timeout, budget, cancellation, or permitted durable
   suspension semantics. Changing these semantics changes plan identity.

## Active delivery strategy

The long-range capability milestones below remain the semantic checklist. The
implementation order is deliberately vertical: prove immutable IR and durable
execution before investing in a friendly source grammar. A programmatic
definition builder is a bootstrap and testing surface, not a second public
language.

### Delivery milestone A — Freeze the executable-plan contract (complete)

Scope: the Milestone 1 design gate.

Activities:

- Specify the smallest useful workflow, schema, binding, node, source-map,
  capability, descriptor-reference, and execution-identity records.
- Decide node paths, reserved segments, escaping, limits, and identity under
  unrelated insertion or movement.
- Define canonical IR JSON, language/compiler semantic versions, execution
  fingerprint inputs, and immutable definition-store verification.
- Establish that the v1 reference graph is acyclic and that future repetition
  is represented by first-class structured regions rather than backward edges.
- Define typed artifact/reference and context-snapshot identities without
  choosing their storage providers.
- Prove the same artifact contract represents code, document, dataset, and
  media examples without adding domain-specific IR node kinds.
- Publish golden vectors, including an independent implementation, for
  canonical bytes, fingerprints, and stable node identities.

Exit: the provider-neutral IR round-trips canonically and has no dependency on
Zhinu, Baize, Hongxian, Guyabano, or an application domain.

### Delivery milestone B — Scaffold and validate plans programmatically

Scope: the core of Milestone 2, before text parsing.

Activities:

- Scaffold the solution, core/compiler packages, tests, README, CI, packaging,
  formatting, architecture decisions, and threat-model starter.
- Implement schemas, descriptors, catalogues, diagnostics, source maps,
  canonical serialization, fingerprints, capability manifests, and hard
  compilation budgets.
- Add a constrained programmatic definition builder that lowers through the
  same binder/type checker/validator future source text will use.
- Validate acyclic generic references, structured-region boundaries, definite
  assignment, complete returns, catalogue pinning, descriptor hashes,
  capability grants, and unsafe retry policy.
- Add an in-memory definition store and conformance tests for future stores.

Exit: tests can construct, reject, canonicalize, fingerprint, persist, and
reload a non-trivial typed plan without a parser or runtime.

### Marang Gate 0.5 — plan acceptance and supervisory execution

This is a prioritization overlay on milestones B and C, not a new IR version.
It identifies the reusable work needed before a host may accept a
supervisor-authored compiled plan. A fingerprint proves byte identity; it does
not prove that the supervisor, descriptors, capabilities, or requested side
effects are authorized.

#### Reusable foundation already available

- `WorkflowPlanIdentity` produces canonical IR bytes and execution
  fingerprints, including descriptor and routing-policy inputs.
- `StructuralNodeIdentity` and `RuntimeNodeIdentity` provide source-stable
  structural paths and deterministic typed loop/fan-out key helpers.
- `WorkflowDefinitionDocument.LoadVerified` and
  `InMemoryWorkflowDefinitionStore` provide canonical-byte, size, and
  fingerprint verification at an immutable content-addressed store boundary.
- `FuwenType`, `ArtifactReference`, and the plan/node input-output fields
  provide provider-neutral type and artifact shapes.

These are usable by Marang as contracts. They do not, by themselves, perform
authoritative catalogue admission, binding/type checking, or execution.

#### P0 — blockers for safe compiled-plan acceptance

Complete these before accepting supervisor-authored plans from an untrusted or
model-mediated source:

- **Deep immutable plan snapshots.** Make the complete plan graph
  (collections, dictionaries, nodes, bindings, and schema records) defensively
  snapshotted or persist only a verified canonical `WorkflowDefinitionDocument`.
  A plan must not change after validation or fingerprinting through a caller's
  mutable collection. `WorkflowPlan.Revision` must be part of the same
  immutable snapshot.
- **Authoritative semantic admission.** Extend the milestone B validator to
  resolve every binding and projection, reject unknown/backward/cross-region
  references and dataflow cycles, enforce structured-region boundaries,
  definite assignment, complete returns, and input/output type compatibility.
  Validate literals, limits, descriptor hashes, catalogue membership, host
  capability grants, side-effect/retry policy, and hard source/AST/depth/node/
  diagnostic/time budgets. The plan's self-declared capability manifest is
  evidence to check, not an authorization grant. `LoadVerified` remains
  mandatory, but canonicality alone is insufficient.
- **Explicit immutable revision lineage.** Define a revision identity and
  lineage contract (including parent/precedence and deployment selection
  semantics) instead of treating `Revision` as an arbitrary nonblank label.
  Define which revision fields affect execution identity and ensure a revision
  cannot alias or mutate an already accepted fingerprint.
- **First-class supervisory checkpoint/input semantics.** Add a typed,
  structured external-input node for supervisor-authored values, checkpoints,
  approvals, or resumptions. It must declare inputs/outputs and durable
  suspension/resume behavior, authorization/policy requirements, timeout,
  cancellation, duplicate, replay, and late-input semantics. Do not encode
  this as an undocumented Marang graph trick or as a generic activity with
  hidden control-flow meaning.
- **Typed context requirements and snapshot references.** Add an explicit
  context requirement/request contract and an immutable typed snapshot
  reference carrying the provider/request identity, content/revision hash,
  policy/budget metadata, and provenance needed for retry versus refresh. Bind
  references to declared context nodes and validate their type and scope.

#### P1 — reusable execution seam for Marang

After P0 admission is enforced, complete the existing Delivery C typed-port
and generic-workflow checklist, extending it with the new external-input/
checkpoint port. The seam must load plans only by verified execution
fingerprint, bind runs to the exact plan revision and input identity, map
structural paths to deterministic Zhinu step keys, and enforce host catalogue,
resource, capability, timeout, and side-effect policy. Zhinu remains
authoritative for persistence, retries, fencing, scheduling, and recovery.

This is an upstream reusable port/package plus a Marang adapter implementation;
there is currently no Zhinu package or `IInferenceExecutor` in this repository.

#### Later enhancements, not first supervisory-slice blockers

Keep keyed fan-out and bounded structured loops as milestones 8 and 10. The
first supervisory slice can use the existing conditional IR plus the new
external-input/checkpoint node; it does not need generic repetition or a
Marang-specific workaround. Wait/interaction surface details, Baize
integration, and broader provider conformance follow the P0/P1 gates.

**Gate exit:** P0 is the safe-plan-acceptance gate; P1 is the execution
integration gate. Do not mark either gate complete until rejection tests prove
the corresponding invariants and the implementation is present in the public
packages.

### Delivery milestone C — Prove durable execution with fakes

Scope: a deliberately narrow subset of Milestones 4 and 5.

Activities:

- Define typed activity, context, inference, and execution-observer ports.
- Carry typed artifact inputs, outputs, collections, and provider receipts
  through the fake vertical without loading artifact bytes into Zhinu state.
- Support only pure/read-only activities and demonstrably idempotent writes in
  the first executable slice.
- Register one generic Zhinu workflow that loads verified immutable IR and maps
  stable node identities to stable step keys.
- Use deterministic fake activity, context, and inference providers so tests
  need no network or credentials.
- Prove process restart, infrastructure retry, selective restart, changed
  input, changed descriptor, changed IR, fencing, and operation-key semantics.
- Expose generic execution/provenance observations; do not depend on Hongxian
  or invent another event store.

Exit: a typed
`request -> context snapshot -> infer -> conditional activity -> artifacts/result`
plan survives process restart and selective restart with deterministic evidence.

### Delivery milestone D — Add the minimal Fuwen source language

Scope: Milestone 3 compiled to the already-proven IR.

Activities:

- Add schemas, enums, typed workflow input/output, immutable bindings, named
  context/activity/inference nodes, restricted expressions, `if/else`, and
  complete typed returns.
- Implement the resource-bounded lexer/parser, canonical formatter, source
  maps, stable diagnostic codes, malformed-input tests, and fuzz tests.
- Publish concise machine-readable grammar and catalogue descriptions suitable
  for LLM authoring and diagnostic-driven correction.

Exit: handwritten and model-produced source compile to byte-identical IR when
they express the same semantics; formatting is idempotent.

### Delivery milestone E — Integrate Baize and structured inference policy

Scope: the inference parts of Milestones 4 and 6.

Activities:

- Implement `Penghou.Fuwen.Baize` behind `IInferenceExecutor`.
- Keep logical profiles in host policy; resolve and record actual providers,
  models, fallbacks, tools, usage, timing, cost, and artifacts.
- Model syntax repair, schema validation, tool mapping, truncation, provider
  failure, and policy rejection as distinct outcomes.
- Make bounded inference retry/escalation trusted-profile policy. Source may
  reduce a host limit but cannot expand attempts, tokens, cost, tools, or
  capabilities.
- Reuse one immutable context snapshot across inference retries; selective
  restart of the context node creates a new snapshot revision.
- Ensure Nuwa repair success never substitutes for final schema validation.

Exit: recorded malformed, missing-tool, schema-mismatch, and truncated-response
cases produce deterministic typed outcomes and policy-driven retry behavior.

### Delivery milestone F — Add keyed fan-out and pilot in Guyabano

Scope: the minimum of Milestone 8 needed by task decomposition.

Activities:

- Add bounded keyed fan-out, explicit yield, deterministic aggregation, and
  duplicate/null-key rejection using Zhinu `FanOutAsync`.
- Preserve successful siblings when one item fails; restart one keyed item and
  invalidate only its aggregate/dependents.
- Express Guyabano task decomposition as the first real consumer: component
  context, typed structured inference, validation, per-parent fan-out,
  aggregation, and typed product outcome.
- Run the existing recorded failure corpus first, then one live dogfood run.
- Compare the old and Fuwen paths for outputs, diagnostics, repair/retry rate,
  token use, provenance, restart scope, and successful-sibling reuse.
- Remove the old Guyabano implementation only after behavioral parity and a
  successful focused-restart dogfood run.

Exit: Fuwen replaces one bounded Guyabano phase without weakening audit,
recovery, typed failure reporting, or selective restart.

### Delivery milestone G — First public preview

Activities:

- Add samples, API docs, compatibility tests, package validation, changelog,
  release checklist, and security documentation.
- Run at least one non-code artifact workflow fixture—for example, research
  material to a cited report or a keyed media-generation batch—through the
  same IR and runtime contracts before publishing.
- Stabilize diagnostic codes, canonical IR versioning, and provider conformance
  fixtures before promising compatibility.
- Publish preview packages only after the fake vertical and Guyabano pilot pass
  in CI.

Exit: another application can author and execute the minimal vertical without
referencing Guyabano or internal test infrastructure.

## Explicitly deferred from the first preview

- General loops and correction syntax; initial retry/escalation is bounded
  trusted inference policy, not user-authored control flow. Deferral does not
  permit lowering repetition to generic backward edges in the meantime.
- Waits, user interaction, approvals, and Hongxian integration.
- Child workflows, compensation declarations, imports, secrets, DSL templates,
  arbitrary write/external/destructive activities, and media-specific syntax.
- Replacing all Guyabano orchestration. One phase is enough to prove or reject
  the abstraction.
- Domain-specific grammar for code, documents, research, images, audio, or
  video. These domains use typed schemas, descriptors, artifacts, and trusted
  activities over the same language constructs.

## Milestone 0 — Architecture record

- [x] Define the ecosystem gap and domain-neutral use cases.
- [x] Establish compiler/runtime and component responsibility boundaries.
- [x] Define stable node, loop-instance, and fan-out identity direction.
- [x] Define explicit loop state/results and fan-out aggregation.
- [x] Define durable context snapshots and refresh-versus-retry behavior.
- [x] Separate source identity, executable-plan identity, and provenance.
- [x] Reduce the first vertical and record deferred constructs/non-goals.

## Milestone 1 — IR and identity design gate

Design semantics before the grammar.

- [x] Define versioned provider-neutral IR for `WorkflowDefinition`, schemas,
  enums, bindings, `ActivityNode`, `InferenceNode`, `ContextNode`,
  `ConditionalNode`, `ReturnNode`, `CapabilityManifest`, `SourceMap`, and
  execution identity.
- [x] Define IR evolution/compatibility; newer code must never silently
  reinterpret immutable historical IR.
- [x] Define lexical scope, qualified node paths, reserved segments, escaping,
  length limits, and collision diagnostics.
- [x] Reserve deterministic identity semantics for later loop, fan-out, wait,
  interaction, and child-workflow nodes.
- [x] Define canonical fan-out key types/serialization. Keys are non-null and
  unique; positional identity requires explicit opt-in and a warning.
- [x] Define canonical IR JSON using Penghou canonical JSON rules where
  compatible and add independent golden vectors.
- [x] Define source normalization: comments, line endings, Unicode, formatter,
  and language version.
- [x] Define execution fingerprint inputs: language-semantics version,
  compiler-semantics version, canonical IR, resolved schema and descriptor
  hashes, template hashes, routing-policy revision, and capability manifest.
- [x] Keep compiler-semantics version separate from NuGet/assembly version so a
  packaging-only release does not change executable identity.
- [x] Define immutable `IWorkflowDefinitionStore`: one execution fingerprint
  resolves to exactly one byte-identical verified IR artifact.
- [x] Preserve source maps for diagnostics without including positions in node
  identity.
- [x] Prove that inserting or moving an unrelated sibling preserves identities.
- [x] Define the structured-control-flow invariant: generic references form an
  acyclic plan; intentional repetition remains a first-class semantic region
  and never becomes an arbitrary backward edge in the public IR.

Acceptance gate: canonical round-trip and independent fingerprints pass golden
tests; future node kinds fit without changing existing meaning; core IR depends
on no Zhinu, Baize, Hongxian, or application type.

## Milestone 2 — Core and compiler skeleton

Initial packages:

```text
Penghou.Fuwen
Penghou.Fuwen.Compiler
```

- [x] Scaffold solution, tests, README, architecture docs, CI, formatting,
  packaging, and trusted publishing consistent with the ecosystem.
- [ ] Implement IR, schema, descriptor, diagnostics, source maps, fingerprints,
  and capability-manifest contracts.
- [ ] Define a constrained JSON-Schema-compatible dialect: objects, arrays,
  scalar primitives, null, enums, optional fields, and bounded nesting. Reject
  unsupported JSON Schema features.
- [ ] Use nominal typing at named workflow/activity/inference boundaries and
  for enums; restricted structural compatibility for literals/projections.
- [ ] Define trusted catalogues for schemas, activities, inference profiles,
  registered templates, tools, and context providers.
- [ ] Pin descriptor identity, version, and hash during compilation.
- [ ] Enforce hard limits for source bytes, tokens, AST nodes, nesting, workflow
  nodes, schemas/depth/fields, expressions, strings, diagnostics, catalogue
  lookups, lookup time, and total compile time.
- [ ] Return stable diagnostics for limit failures rather than exhausting host
  resources.

## Milestone 3 — Minimal source language

- [ ] Brace-delimited `schema` and `enum`.
- [ ] Typed `workflow` input/output and immutable `into` bindings.
- [ ] Restricted literals, records/lists, field access, boolean conditions,
  equality/ordering, `and`, `or`, `not`, and `exists` where type-safe.
- [ ] Named `activity` resolved from trusted descriptors.
- [ ] Named `infer` using registered template and profile descriptors.
- [ ] Declarative context requests lowered to explicit context dependencies.
- [ ] `if / else` with branch typing and definite-assignment analysis.
- [ ] `return` with complete result-path analysis.
- [ ] Canonical idempotent formatter and golden tests.
- [ ] Stable human/machine diagnostics designed for LLM repair.
- [ ] Parser/compiler malformed-input, fuzz, and resource-budget tests.

## Milestone 4 — Execution descriptors and safety

- [ ] Activity descriptors declare exact identity/version/hash, input/output
  schemas, side-effect class, idempotency class, retry safety, compensation,
  timeout defaults, and resource-scoped capabilities.
- [ ] Side effects: `none`, `read`, `write`, `external`, `destructive`.
- [ ] Idempotency: `idempotent`, `idempotentWithKey`, `nonIdempotent`, `unknown`.
- [ ] Define provider-neutral typed activity handlers and
  `IInferenceExecutor` requests/results.
- [ ] Define registered typed prompt-template descriptors; defer DSL templates.
- [ ] Preserve resolved inference modality without forcing Baize text/media
  surfaces into one lowest-level transport API.
- [ ] Define tools with schemas, side effects, idempotency, retry safety,
  capabilities, and host-handle restrictions.
- [ ] Define `ContextSnapshot`: provider/descriptors, request fingerprint,
  selected opaque references/content revisions, policy revision,
  truncation/budget metadata, hash, provenance, and creation time.
- [ ] Decide the host-owned snapshot/artifact store contract; Fuwen must not
  become a context-content store.
- [ ] Treat Nuwa repair and final schema validation as separate typed outcomes.

### Idempotency correction required

For `idempotentWithKey`, a provider operation key must distinguish retry from
selective restart or changed input. Bind at least:

```text
execution fingerprint
structural/runtime node identity
Zhinu step revision or fencing generation
canonical effective request/input fingerprint
```

Infrastructure retry reuses the key; restart or changed input gets a new key.
Execution fingerprint plus node identity alone would incorrectly deduplicate a
legitimate regeneration.

- [ ] Specify/test this contract before write or external activities.
- [ ] Reject unsafe automatic retry for `nonIdempotent` or `unknown`; a warning
  is insufficient for durable side effects.
- [ ] Decide whether the initial executable slice allows only `none`, `read`,
  and demonstrably idempotent activities.

## Milestone 5 — Generic durable execution through Zhinu

Package: `Penghou.Fuwen.Zhinu`.

- [ ] Register one generic Zhinu workflow that loads immutable IR by execution
  fingerprint and rejects missing, changed, or unsupported content.
- [ ] Map executable node identity deterministically to Zhinu step keys.
- [ ] Map activity, context, inference, condition, and return semantics without
  reimplementing Zhinu persistence, retry, fencing, or scheduling.
- [ ] Bind every run to exact execution fingerprint and input identity.
- [ ] Define typed capability, compatibility, activity, inference, context,
  schema, and unsafe-retry failures.
- [ ] In the first fake-executor slice, semantic failures fail the workflow and
  only permitted infrastructure failures use Zhinu retry. The later Baize
  slice adds trusted-profile, bounded retries for explicitly classified
  inference representation failures; this is not authored correction syntax.
- [ ] Preserve Zhinu fencing/idempotency through handler calls.
- [ ] Add restart, replay, retry, selective-restart, changed-input,
  changed-descriptor, and changed-IR tests.

## Milestone 6 — Baize adapter and first vertical

Package: `Penghou.Fuwen.Baize`.

- [ ] Implement `IInferenceExecutor` using Baize text and generation surfaces.
- [ ] Translate host profiles to normalized Baize requirements; keep application
  profile names out of Baize.
- [ ] Record actual model/provider, fallbacks, tools/filtered arguments,
  repair/validation diagnostics, artifacts, usage, cost, timing, attempts, and
  errors as provenance.
- [ ] Prove permitted fallback does not alter execution fingerprint.
- [ ] Implement one durable context provider, one read-only activity, and one
  safely idempotent side-effecting activity with provider receipt verification.
- [ ] Complete end to end:

  ```text
  Request -> context -> infer -> conditional activity -> Answer
  ```

- [ ] Provide deterministic fake inference so CI needs no network credentials.

## Milestone 7 — AI authoring reliability

- [ ] Publish grammar, catalogue/schema descriptions, and concise generation
  guidance in machine-consumable form.
- [ ] Test diagnostic-driven LLM repair outside the Fuwen workflow runtime.
- [ ] Measure syntax, binding, typing, catalogue, and capability failures across
  model families.
- [ ] Keep one canonical spelling per semantic operation.
- [ ] Treat formatter output and diagnostic codes as public APIs.

## Milestone 8 — Parallelism and aggregation

- [ ] Add named `parallel`, bounded `foreach`/`parallel foreach`, stable `key`,
  explicit positional opt-in, typed `yield`, and source-order results.
- [ ] Represent fan-out as a structured region with declared inputs, item/body
  scope, yielded outputs, and no references that jump across its boundary.
- [ ] Reject null/duplicate keys before child work starts.
- [ ] Persist individual outcomes; one failure fails aggregate and blocks
  downstream nodes without erasing successful siblings.
- [ ] Map item restart to reuse unrelated successful items while invalidating
  aggregate and dependents.
- [ ] Keep concurrency a host limit source may only reduce.

## Milestone 9 — Waiting and interaction

- [ ] Map named waits to Zhinu idempotent signals with typed timeout,
  cancellation, duplicate, and late-signal behavior.
- [ ] Keep durable suspension explicit in the IR; a wait may be intentionally
  unbounded only when host policy permits it and it must not be represented as
  a control-flow cycle.
- [ ] Add host intents for input, selection, and approval without UI semantics.
- [ ] Keep Hongxian optional and non-authoritative for workflow state.

## Milestone 10 — Explicit bounded correction loops

- [ ] Add named `repeat` only, with a positive static maximum.
- [ ] Require declared loop state, complete `continue with`, type-compatible
  `break with`, and explicit outer result.
- [ ] Preserve the loop as a first-class IR region with structural body
  identity, declared entry/update/exit bindings, and closed reference scope;
  do not lower it to generic backward `next` edges in the public contract.
- [ ] Fail with typed `LoopLimitExceeded`; never return last state silently.
- [ ] Map iteration restart to selected/later iterations and dependents while
  preserving earlier valid iterations.
- [ ] Include loop bounds, carried-state contracts, continuation/exit
  semantics, and applicable budgets in canonical plan identity.

## Milestone 11 — Deferred composition and safety

- [ ] Add subworkflows only through Zhinu child workflows.
- [ ] Consider DSL templates after registered templates prove interpolation.
- [ ] Add compensation only as a thin declaration mapped to Zhinu.
- [ ] Add `secret<T>` only with opaque handles, accepting descriptors,
  non-persistence, propagation, redaction, authorization, and full tests.
- [ ] Add imports only with immutable locking, trust, cycle detection, and
  fingerprint inclusion.
- [ ] Add explicit memory commands only when real workflows cannot express the
  need through context providers and activities.

## Milestone 12 — Domain-neutral maturity

- [ ] Validate coding, research/document, and multimedia workflows.
- [ ] Prove new providers and artifact kinds normally require catalogue entries,
  not grammar changes.
- [ ] Add provider conformance suites, public API docs, compatibility baselines,
  changelog, release checklist, samples, and threat model.
- [ ] Remain preview until two substantially different applications use Fuwen.

## Package direction

```text
Penghou.Fuwen
Penghou.Fuwen.Compiler
Penghou.Fuwen.Zhinu
Penghou.Fuwen.Baize
```

Do not create `Penghou.Fuwen.Runtime` initially. Add a Hongxian or other adapter
only after repeated integration demonstrates a real reusable boundary.

## Open decisions

- Exact schema dialect and nominal/structural assignability.
- Canonical formatter rules for comments and source-level whitespace once the
  parser exists; transport normalization and Unicode preservation are frozen.
- Routing-policy revision versus deployment/runtime provenance.
- Minimum artifact-reference contract and its owning package.
- Context-snapshot storage/publication ownership.
- Typed activity and inference failure taxonomy.
- Parser implementation after IR stabilizes; ANTLR is not yet mandated.
- Whether repeated use eventually justifies `Penghou.Fuwen.Hongxian`.

## Resume point

Continue with Delivery milestone B:

1. Define stable compiler diagnostics and hard compilation-budget contracts.
2. Add trusted catalogue interfaces and exact descriptor-resolution results.
3. Implement a constrained programmatic definition builder that lowers
   through the same binder, type checker, and validator future source uses.
4. Validate acyclic references, definite assignment, branch returns, nominal
   boundaries, capability closure, and catalogue pins with rejection tests.
5. Keep loops out of the builder until Zhinu proves its durable state-loop
   primitive; Fuwen loop IR and syntax follow that runtime contract.

Do **not** begin with the text grammar. The next deliverable is a programmatic
compile path that produces the already-frozen canonical `WorkflowPlan`.
