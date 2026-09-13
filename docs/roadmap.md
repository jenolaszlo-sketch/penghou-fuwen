# Penghou.Fuwen Roadmap

## Status

**Delivery milestones A–E complete — Delivery milestone F consumer pilot is next**

Last reviewed: **2026-09-13**

Fuwen is the proposed typed authoring and compilation layer for portable,
capability-reviewed AI workflows. It produces an immutable executable plan; it
is not another workflow engine. This roadmap consolidates the proposal,
architecture review, and semantic-contract addendum so work can resume without
reconstructing the design from chat history.

Fuwen is artifact-driven rather than code-generation-driven. Source code,
documents, datasets, reports, images, audio, video, prompts, model outputs, and
manifests are all host-described artifacts. Code generation is the first
demanding pilot, not a privileged language concept.

Fuwen is also the planning boundary for adaptive workflows. An AI, user, or
supervisor may propose a materially different objective or path, but it does
not mutate an activated plan. Fuwen compiles each accepted proposal into a new
immutable plan revision; Zhinu deterministically executes an admitted revision,
and Hongxian preserves the evidence explaining why revisions changed.

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
node identity, canonical IR, fingerprints, source maps, diagnostics, immutable
plan-revision lineage, and semantic comparison between plan revisions.

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
23. An activated plan is immutable. Retry executes the same plan; material
   objective, acceptance-criteria, dependency, or execution-path change creates
   a new plan revision.
24. Revision lineage is not execution behavior. Parentage and proposal evidence
   belong in an immutable revision envelope referencing an execution
   fingerprint; they do not alter canonical plan semantics merely to record
   history.
25. Fuwen can classify semantic differences and possible correspondence between
   stable structural nodes, but it cannot authorize physical artifact reuse.
   Zhinu combines that analysis with durable inputs, provenance, evidence, and
   host policy.

## Adaptive planning and workflow evolution

The target stack is:

```text
AI / user / supervisory checkpoint
              |
      Accept / Retry / Replan
              |
        Fuwen admission
              |
    immutable PlanRevision N
              |
       Zhinu generation N
              |
      deterministic execution

Hongxian records the decisions, references, and outcomes across the timeline.
```

The workflow instance survives multiple plan revisions and Zhinu execution
generations. Fuwen owns the immutable planning records required at that seam:

- a stable `PlanRevisionId` referencing one exact execution fingerprint;
- optional parent revision identity so lineage can branch without rewriting
  history;
- bounded proposal/reason/evidence references without chain-of-thought;
- deterministic semantic comparison of nodes, descriptors, dependencies,
  objectives, acceptance criteria, and required validation;
- explicit distinction between structural correspondence and a runtime reuse
  decision.

The revision envelope should remain separate from canonical executable IR.
Reactivating byte-identical semantics may create a new revision or execution
generation without manufacturing a new execution fingerprint. Display names
must not establish correspondence, and an explicit durable identity override
must never allow changed semantics to masquerade as reusable work.

The current `WorkflowPlan.Revision` is already part of published fingerprint
contracts. Do not silently reinterpret or remove it. The revision-envelope
design must either give that field a precise execution-semantic meaning or
introduce the new separation under a versioned IR/fingerprint contract while
continuing to verify historical plans with their original rules.

Initial comparison output should be bounded and explainable: unchanged, added,
removed, changed, dependency-changed, objective-changed, and
validation-requirement-changed. It may identify candidate reusable work but
must not claim that an artifact is valid. Runtime inputs, producer semantics,
artifact provenance, evidence freshness, and policy remain outside Fuwen.

Delivery order:

1. Finish trusted callable metadata and the host-admission receipt. **Complete.**
2. Define the immutable revision envelope and its identity/lineage invariants.
   **Complete.**
3. Add deterministic plan comparison using stable structural identity and
   semantic fingerprints. **Complete.** Exact structural paths establish
   correspondence; renames are removed plus added. Results distinguish node,
   dependency, workflow, objective, acceptance, and validation changes without
   authorizing artifact reuse.
4. Hand the comparison to a later Fuwen.Zhinu transition adapter; do not add
   scheduling, quiescence, artifact mutation, or generation state to Fuwen.

Automated plan generation, transition activation, rollback policy, branch
selection, and AI autonomy are not part of this Fuwen milestone.

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

Progress:

- [x] Establish stable compiler diagnostic, immutable result, usage-summary,
  host/caller budget, budget-evaluation, and bounded collection contracts.
- [x] Deep-freeze caller-owned plan graphs, clone JSON literals, hash the exact
  canonical buffer once, and reject oversized, cyclic, or excessively nested
  programmatic plans before unsafe traversal/allocation.
- [x] Keep definition loading an integrity/compatibility boundary: verify exact
  canonical bytes, supported IR/canonical/fingerprint contracts, size, and
  content identity without presenting the result as semantically admitted.
- [x] Add trusted catalogue interfaces and exact immutable resolution results,
  including deterministic snapshots, strict result invariants, cancellation,
  lookup/count deadlines, and bounded catalogue/schema inputs.
- [x] Introduce IR/fingerprint v2 region-local ordered phases, explicit final
  return barriers, same-region earlier-phase binding rules, aggregate schedule
  limits, and historical v1/v2 golden-vector isolation.
- [x] Implement the first constrained programmatic builder and semantic compiler
  path: explicit execution schedules, detached snapshots, exact trusted
  descriptor/schema resolution, binding/projection/condition/return checks,
  inferred capability assertions, host capability grants, canonical definition
  output, bounded diagnostics, and multi-target rejection tests.
- [x] Add bounded trusted callable signatures with deeply snapshotted named
  parameters and output types; require exact argument names and nominal output
  contracts; reject missing metadata, undeclared type closure, unsupported
  effects, keyed/non-idempotent/unknown invocation semantics, and unsafe or
  host-controlled retry claims. These checks validate a conservative compiler
  slice but do not authorize execution.
- [x] Complete executable host admission for the programmatic slice: immutable
  catalogue snapshot identity, exact resolved-metadata identity, finite
  policy/grant identity, effective-budget identity, and an opaque in-process
  receipt with no public constructor. Unversioned catalogues and policies,
  failed compilation, and test-only `AllowAll` cannot issue a receipt. This is
  an in-process authorization token, not a signed transport credential.
- [x] Harden the remaining programmatic semantic boundary against undefined or
  wrong-arity condition operators and structural list/object bindings supplied
  where trusted callable signatures require scalar values. Preserve structured
  values only for compatible list/object schemas or the explicit `json` type.
- [x] Add immutable plan-revision lineage outside executable IR: host-issued
  opaque revision IDs, optional direct parentage, verified execution-definition
  binding, explicit objective/acceptance/validation content identities, bounded
  immutable artifact references, canonical envelope fingerprints, verified
  loading, and an idempotent store that rejects changed reuse of a revision ID.
  Lineage documents remain integrity evidence and never authorize activation,
  precedence, execution, or artifact reuse.
- [x] Close the first schema/identity portability slice: reject recursive named
  schema graphs with deterministic bounded diagnostics, count reference depth,
  validate enum literals against their serialized member values, reject lossy
  numeric fallback, and prove decimal/Unicode/property-order behavior through
  matching .NET and independent Python vectors.
- [x] Define the provider-neutral runtime-value boundary as detached JSON,
  immutable artifact references, and bounded list/object composites; validate
  these values strictly against primitive, optional, list, object, enum, and
  nominal artifact types without coercion or implicit authorization.
- [x] Introduce pre-release IR/fingerprint v3 typed context requirements. Bind
  each inference requirement directly to an earlier same-region context-node
  output with an exact declared type, preserve historical v1/v2 bytes, and keep
  runtime context-snapshot evidence outside the compiled request contract.

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
  fingerprint integrity verification at an immutable content-addressed store
  boundary. `LoadVerified`/`ReadPlan` do not perform semantic admission or
  authorize execution; a future compiler/admission pipeline remains mandatory.
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
- [x] **Provider-neutral runtime values and snapshot references.** Add an
  immutable runtime-value union for detached JSON, artifact identities, and
  bounded list/object composites, plus a bounded context-snapshot reference
  carrying the exact provider descriptor, request/content digests, source
  revisions, policy/budget metadata, creation time, and optional opaque
  provenance receipt. Composite construction recursively snapshots children;
  the compiler validates primitive, optional, list, object, enum, and artifact
  values strictly without coercion, including artifact fields nested in
  composites. Context payload storage, authorization, and dereferencing remain
  host responsibilities.
- [x] **Typed context requirements.** IR v3 adds explicit named context
  requirements, binds each requirement to a direct earlier same-region
  `ContextNode` output, and validates exact type and scope without conflating
  the compiled dependency with runtime snapshot evidence.

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

### Implementation review — admission, usability, and first-consumer fit

Reviewed against the current source, tests, and Qingniao integration needs.
Delivery A proves identity/storage foundations; Delivery B now includes a
working programmatic compiler and explicit host-admission boundary. The existing
P0/P1 gates above remain authoritative.
The items below refine those gates rather than create another milestone list.
All unchecked items are proposed work, not implemented guarantees.

#### Priority 0 — make a verified plan mean something precise (Delivery B)

- [x] **Separate integrity verification, semantic validation, and host admission.**
  `LoadVerified` checks canonical bytes, compatibility, size, and fingerprints;
  `WorkflowPlanValidator` checks structural paths, descriptor closure, scheduled
  dataflow, region boundaries, projections, returns, and identity-critical
  shapes. The compiler adds trusted binding, type, callable, context-reference,
  and capability checks. Distinct outcomes and an admitted-plan boundary prevent
  a caller from mistaking a valid hash for permission to execute. The builder and
  future parser must continue through this same admission pipeline.
  The programmatic compiler performs detached semantic compilation, trusted
  schema substitution, exact capability assertion, trusted callable/effect/
  retry checks, and finite host grant checking. `WorkflowAdmissionService`
  consumes that exact resolution trace once and issues an opaque receipt bound
  to the definition, catalogue snapshot, resolved metadata, policy, grants, and
  effective budget. Cross-process signed admission remains a deliberate later
  extension, not an implied guarantee.
- [x] **Resolve execution order before freezing more IR.**
  `WorkflowPlanIdentity.NormalizeNodes` sorts siblings by structural path; the
  golden fixture consequently places `return_result` before `validate`.
  Bindings express data dependencies, but two side-effecting activities need not
  exchange data. Decide how sequencing, required validation, branch completion,
  and return barriers are represented explicitly. Lexicographic order must never
  decide whether validation runs before a result or which write happens first.
  Preserve order-independent identity for genuinely independent nodes and prove
  that changing a required ordering changes execution identity. Keep control
  structure semantic; do not introduce arbitrary backward edges. ADR 0002 is
  implemented through lexical regions with ordered completion phases in IR v2,
  with a separate fingerprint contract and golden vector.
- [x] **Freeze once, validate once, hash the exact frozen bytes.**
  `WorkflowDefinitionDocument.Create` serializes the caller's plan twice: once
  for bytes and again for the fingerprint. Public records retain caller-owned
  collections, so changing input between traversals can produce an inconsistent
  document. Snapshot deeply, clone literal `JsonElement` values before their
  owning document is disposed, and hash the one canonical byte buffer. Apply
  the persisted size ceiling on creation too. Test mutation during enumeration,
  disposed literals, oversized creation, and matching document bytes/digest.
- [ ] **Close compatibility and resource-limit holes at every entry point.**
  Supported IR/compiler/canonical/fingerprint versions and primitive enums now
  fail closed. Descriptor admission now requires the current canonical
  lowercase SHA-256 identity shape (with a bounded nonblank contract), and the
  core validator and compiler catalogue boundary share the same rule. The
  provider-neutral `ContentDigest` transport remains open for artifact/source/
  evidence contracts, and `LoadVerified` continues to verify historical bytes
  without semantic admission or reinterpretation. This closes the descriptor
  digest portion of this item. Schema/descriptor consistency is checked against
  trusted catalogues. Recursive named schemas now fail closed with stable,
  bounded cycle diagnostics, schema-reference depth contributes to compiler
  usage, and enum literals must match resolved serialized member values.
  Bound programmatically constructed trees before recursive traversal, as well
  as persisted JSON; reject null entries and excessive depth with bounded stable
  diagnostics. Delimiter-bearing identities, recursive schemas, and the current
  collection/depth surfaces have focused fixtures. Complete the remaining
  unknown-enum and adversarial entry-point matrix before closing this umbrella.
  Aggregate trusted-catalogue metadata accounting is complete: resolved schema,
  callable, capability, and descriptor payloads are charged atomically against
  the existing AST-node and UTF-8 string-byte ceilings, including the combined
  plan-plus-catalogue compiler budget. Caller-controlled text and JSON literals
  are now preflighted before snapshot allocation; historical IR v1 is
  bound to its exact compiler-semantics contract; descriptor and capability
  duplicate detection now uses typed identities instead of delimiter-built keys;
  and artifact references cannot carry non-artifact descriptors.
- [x] **Prove numeric and Unicode identity portability before expanding hashing.**
  `CanonicalJson.WriteNumber` previously selected decimal and then a lossy
  `double` fallback. The accepted v1 numeric range and precision are now explicit
  so distinct numeric literals cannot accidentally acquire the same identity.
  The v1 contract now accepts exact decimal values only, normalizes equivalent
  zero spellings, rejects the former lossy `double` fallback, and fixes escaping
  and ordinal ordering through matching .NET/Python vectors. The independent
  oracle covers decimal precision/range boundaries, exponents, negative zero,
  escaped characters, non-BMP text, and property ordering. Compare the existing
  Siming contract before sharing an implementation; a future expansion requires
  a new version and must never reinterpret historical bytes.

#### Priority 1 — shorten the path from a valid plan to a useful workflow (B/C)

- [ ] **Provide an explain/validate experience with the programmatic compiler.**
  Return bounded diagnostics with stable codes, structural path, expected/actual
  type, source span when available, and actionable repair guidance. Offer a
  deterministic plan explanation showing resolved dependencies, descriptor pins,
  required capabilities, effective limits, side effects, and admission failures.
  This can serve tests, CLI users, and model-assisted correction before a parser
  or UI exists. A preview must perform no activity or external side effect.
  The first pure `WorkflowExplanation` projection is complete: it distinguishes
  compilation from admission and exposes detached plan, descriptor pins,
  inferred requirements, effective limits, usage, and bounded diagnostics.
  Trusted callable-effect summaries and repair-oriented diagnostic enrichment
  are now projected from compiler-retained trusted evidence; CLI/consumer
  presentation remains.
- [ ] **Make restart impact inspectable.** Expose a plan comparison that explains
  changed descriptors, inputs, context snapshots, and affected dependents while
  distinguishing structural identity from permission to reuse a result. Zhinu
  remains authoritative for restart and reuse; its adapter checks the proposed
  impact against durable execution state. The deterministic direct-comparison
  surface is complete; transitive dependent-impact analysis plus Qingniao
  candidate/Test/Review and Guyabano focused-retry adapter fixtures remain.
- [ ] **Test runtime values at typed boundaries as well as compiled bindings.**
  Validate actual activity, context, and inference outputs before downstream
  consumption, including optional absence versus JSON null, list limits, artifact
  descriptor/content identity, and outcome schemas. Artifact byte verification
  and access remain host/provider responsibilities; possession of an artifact
  reference or `ResourceHandle` is not an authority grant. Document the verifier
  seam and test wrong-type and unauthorized-reference provider responses.
- [ ] **Use recorded provider failures as the execution-port contract tests.**
  Distinguish malformed JSON, repaired-but-schema-invalid data, tool mapping
  failure, truncation, provider error, cancellation, and policy rejection.
  Preserve successful sibling evidence and use declared typed outcomes to choose
  correction or escalation. Keep retries, cancellation, and execution budgets
  enforceable by Zhinu/host policy and keep model review separate from
  deterministic validation. Test adapter conformance with fakes before Baize.
- [ ] **Add two small end-to-end consumer fixtures before broad syntax.**
  Use Qingniao's candidate -> seal -> Test/Review -> decision as the coding fixture,
  plus an artifact-driven document or media fixture with no filesystem-specific
  node types. The complete concurrent Qingniao replacement needs structured
  parallelism; a sequential Delivery C pilot must be labeled as such. One
  explicitly unrolled correction can prove the bounded policy before general
  loop syntax. Do not hide unimplemented parallelism or waits in opaque activities
  and then claim equivalent Fuwen semantics.

#### Scope and delivery clarifications

- The supervisory P0 overlay requires a checkpoint/input node; the original
  non-interactive first preview does not. Apply that requirement when enabling
  supervisor-authored interactive plans, not as a prerequisite for every simple
  read-only compiler fixture. Keep interactive acceptance disabled until its
  gate passes.
- Structured parallelism is necessary to replace the full Qingniao Test/Review
  flow; keyed fan-out additionally serves Guyabano decomposition. Neither is
  implemented merely because runtime identity helpers exist.
- Before implementing loops, inspect the current Zhinu package and its loop
  conformance tests. The old reminder to wait for Zhinu loop work is a dependency
  verification task, not evidence that the upstream feature is still missing.
- Keep workflow revision lineage in Fuwen and run/session history in
  Zhinu/Hongxian. Review whether deployment routing changes belong in execution
  identity or runtime provenance; require an explicit compatibility decision.
- Defer a language server, graphical editor, optimizer, plugin marketplace, and
  extra adapter packages until the builder and executable pilot establish demand.
  Fuwen does not implement a scheduler, session ledger, distributed transaction
  manager, artifact store, sandbox, or independent authority over host grants.
- At delivery boundaries, align README/first-batch status with this roadmap and
  replace overlapping checklist wording with links to the completed gate and its
  tests. Test counts demonstrate coverage only for exercised behavior, not a
  percentage of product completion.

### Delivery milestone C — Prove durable execution with fakes (complete)

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

Progress:

- [x] Define bounded provider-neutral activity, context, inference, result,
  failure, publication-receipt, observation, and invocation contracts.
- [x] Keep the stable downstream operation key independent of Zhinu lease
  generation. Lease generation is changing fencing evidence across claims;
  structural/runtime path, durable step revision, admitted execution identity,
  and effective request identity define idempotency.
- [x] Add `Penghou.Fuwen.Zhinu` and prove the admission/registration boundary:
  exact definition fingerprint, provider-runtime identity, immutable-store
  round trip, and Zhinu `IWorkflowFingerprint` binding. The checkpoint refuses
  node execution explicitly rather than presenting registration as a runtime.
- [x] Interpret the IR v3 sequential context, inference, activity, conditional,
  and return subset as stable Zhinu steps over deterministic fake providers.
  Unordered phase members are serialized in ordinal structural-path order for
  this first adapter; parallel execution remains deferred.
- [x] Bind node inputs, context snapshot evidence, and dependency edges, then
  validate workflow input and every provider or persisted output with Fuwen's
  authoritative runtime validator before downstream use.
- [x] Prove crash recovery, replay, selective restart, retry-safe restart
  receipts, run-scoped operation-key reuse, changed-definition rejection,
  cancellation, malformed persisted evidence, and best-effort observer behavior
  against the real SQLite Zhinu store.
- [x] Bridge admitted, operation-bound provider publication receipts into
  step-owned Zhinu artifact references. Prove the crash window after artifact
  publication but before step completion remains idempotent and produces one
  durable artifact revision.
- [x] Complete the deterministic fake vertical slice from request through
  context snapshot, inference, conditional selection, selected activity,
  artifact evidence, and typed return. Unselected branches do not execute and
  replay does not repeat provider calls.

The current conditional is deliberately control-only. Branch-local values may
not escape their closed execution region. Before the source language exposes
value-producing conditionals, add an explicit typed branch-result/merge
contract in a versioned IR rather than weakening cross-region reference rules.

### Delivery milestone D — Add the minimal Fuwen source language

Scope: Milestone 3 compiled to the already-proven IR.

Activities:

- Define an explicit typed conditional result/merge contract, or keep the first
  source conditional control-only with diagnostics that make the limitation
  unmistakable. Do not permit raw cross-region output references.
- Add schemas, enums, typed workflow input/output, immutable bindings, named
  context/activity/inference nodes, restricted expressions, `if/else`, and
  complete typed returns.
- Implement the resource-bounded lexer/parser, canonical formatter, source
  maps, stable diagnostic codes, malformed-input tests, and fuzz tests.
- Publish concise machine-readable grammar and catalogue descriptions suitable
  for LLM authoring and diagnostic-driven correction.

Exit: handwritten and model-produced source compile to byte-identical IR when
they express the same semantics; formatting is idempotent.

Progress:

- [x] Keep conditionals control-only in the first source surface. Branch-local
  values cannot escape their closed region, and branch-local returns receive a
  stable `FWN-CONTROL-001` diagnostic.
- [x] Add typed schemas, enums, workflow input/output, immutable named-node
  bindings, context/activity/inference nodes, restricted literals and
  projections, type-safe conditions, complete root returns, and explicit typed
  context requirements.
- [x] Implement bounded UTF-8-aware lexing/parsing, canonical idempotent
  formatting, authored-document source maps, stable diagnostics, malformed
  input and fuzz tests, and parser recursion guards for composite bindings.
- [x] Publish the machine-readable grammar, trusted catalogue description, and
  concise authoring/diagnostic guidance for model-produced source.

Delivery D is complete for this deliberately small language. Value-producing
conditionals, loops, fan-out, waits, imports, and richer expression syntax
remain later milestones.

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

Complete: the structured-text adapter uses exact host-owned profile/template
bindings, deterministic endpoint fallback, bounded trusted-profile retries,
Nuwa syntax repair followed by authoritative runtime-value validation, and typed
policy/provider/output failures. Evidence records modality, aggregate and
per-attempt tokens, duration, host-priced monetary cost, and pricing revision;
trusted cost ceilings stop further representation retries. Image, video, and
audio generation map through descriptor-bound Baize clients to host-verified
artifact publication receipts. Generation requires endpoints that advertise
idempotent submission and operation retrieval, reuses the Fuwen operation key,
and resumes provider work without duplicating a billable submission. A routed
executor composes structured and media profiles. SQLite tests now exercise the
actual Baize adapter through a durable context-provider, inference, conditional,
and read-only activity/artifact-publication vertical.

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

Progress (2026-09-11): The first F batch adds the versioned IR v4 structured
keyed fan-out contract, bounded programmatic builder/compiler validation, and a
Zhinu adapter mapping that composes durable `StepAsync` primitives because the
preview `FanOutAsync` API derives positional index keys. Item keys are
canonicalized and checked for null/duplicates before child work; outcomes are
persisted independently and aggregates retain source order with a host
concurrency ceiling. The Guyabano decomposition, corpus comparison, and live
dogfood pilot remain open.

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
- [x] Implement IR, schema, descriptor, diagnostics, source maps, fingerprints,
  and capability-manifest contracts.
- [ ] Define a constrained JSON-Schema-compatible dialect: objects, arrays,
  scalar primitives, null, enums, optional fields, and bounded nesting. Reject
  unsupported JSON Schema features.
- [ ] Use nominal typing at named workflow/activity/inference boundaries and
  for enums; restricted structural compatibility for literals/projections.
- [x] Define trusted catalogue resolution for schemas, activities, inference profiles,
  registered templates, tools, and context providers.
- [x] Require exact descriptor kind, name, version, and content-digest resolution
  at the trusted catalogue boundary. The future binder must use these resolved
  pins when constructing a plan.
- [ ] Enforce hard limits for source bytes, tokens, AST nodes, nesting, workflow
  nodes, schemas/depth/fields, expressions, strings, diagnostics, catalogue
  lookups, lookup time, and total compile time.
- [ ] Return stable diagnostics for limit failures rather than exhausting host
  resources.

## Milestone 3 — Minimal source language (complete)

- [x] Brace-delimited `schema` and `enum`.
- [x] Typed `workflow` input/output and immutable named-node bindings.
- [x] Restricted literals, records/lists, field access, boolean conditions,
  equality/ordering, `and`, `or`, `not`, and `exists` where type-safe.
- [x] Named `activity` resolved from trusted descriptors.
- [x] Named `infer` using registered template and profile descriptors.
- [x] Declarative context requests lowered to explicit context dependencies.
- [x] `if / else` with branch typing and definite-assignment analysis.
- [x] `return` with complete result-path analysis.
- [x] Canonical idempotent formatter and golden tests.
- [x] Stable human/machine diagnostics designed for LLM repair.
- [x] Parser/compiler malformed-input, fuzz, and resource-budget tests.

## Milestone 4 — Execution descriptors and safety

- [ ] Activity descriptors declare exact identity/version/hash, input/output
  schemas, side-effect class, idempotency class, retry safety, compensation,
  timeout defaults, and resource-scoped capabilities.
- [ ] Side effects: `none`, `read`, `write`, `external`, `destructive`.
- [ ] Idempotency: `idempotent`, `idempotentWithKey`, `nonIdempotent`, `unknown`.
- [x] Define provider-neutral typed activity, context, inference, and
  execution-observer ports. Requests preserve exact descriptor identity,
  declared output types, bounded detached arguments, typed context values plus
  snapshot evidence, and canonical operation identity. Results use a closed
  failure taxonomy and permit only unambiguous same-operation infrastructure
  retry; the ports perform no retry or authorization themselves.
- [ ] Define registered typed prompt-template descriptors; defer DSL templates.
- [x] Preserve resolved inference modality without forcing Baize text/media
  surfaces into one lowest-level transport API.
- [ ] Define tools with schemas, side effects, idempotency, retry safety,
  capabilities, and host-handle restrictions.
- [x] Define `ContextSnapshotReference`: provider descriptor, request/content
  digests, selected opaque source revisions, policy revision,
  truncation/budget metadata, bounded provenance receipt, and creation time.
  The host-owned store and authorization/dereference contract remain open.
- [ ] Decide the host-owned snapshot/artifact store contract; Fuwen must not
  become a context-content store.
- [x] Treat Nuwa repair and final schema validation as separate typed outcomes.

### Idempotency correction required

For `idempotentWithKey`, a provider operation key must distinguish retry from
selective restart or changed input. Bind at least:

```text
execution fingerprint
structural/runtime node identity
Zhinu step revision
canonical effective request/input fingerprint
```

Worker lease/fencing generation is deliberately excluded from this hash. A
lease can change during recovery of the same logical step revision, while the
downstream idempotency key must remain stable. Fencing remains orthogonal
authority metadata for an adapter/runtime that can expose and verify it.

Infrastructure retry reuses the key; restart or changed input gets a new key.
Execution fingerprint plus node identity alone would incorrectly deduplicate a
legitimate regeneration.

- [x] Specify/test the canonical operation-key identity before write or
  external activities. Provider-neutral typed executor contracts are complete.
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

- [x] Implement structured-text plus image, video, and audio generation behind
  provider-neutral `IInferenceExecutor` contracts and exact descriptor routing.
- [x] Translate host profiles to normalized Baize requirements; keep application
  profile names out of Baize.
- [x] Record actual model/provider, modality, fallbacks, tools,
  repair/validation diagnostics, per-attempt and aggregate usage, timing, cost,
  pricing revision, attempts, and errors as detached evidence.
- [x] Prove bounded fallback reuses the same invocation identity.
- [x] Implement one durable context provider, one read-only activity, and one
  safely idempotent side-effecting activity with provider receipt verification.
- [x] Complete end to end:

  ```text
  Request -> context -> infer -> conditional activity -> Answer
  ```

- [x] Provide deterministic fake inference so CI needs no network credentials.

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
- Formatter rules are now frozen for the bounded source surface; transport
  normalization and Unicode preservation remain frozen.
- Routing-policy revision versus deployment/runtime provenance.
- Minimum artifact-reference contract and its owning package.
- Context-snapshot storage/publication ownership.
- Typed activity and inference failure taxonomy.
- Whether a future grammar revision needs a parser generator; the bounded
  Delivery D parser is intentionally hand-written.
- Whether repeated use eventually justifies `Penghou.Fuwen.Hongxian`.
- The minimum semantic node digest and comparison categories needed by Zhinu to
  evaluate reuse without making Fuwen responsible for runtime artifacts.

## Resume point

Continue with Delivery milestone F:

1. Express one bounded Guyabano decomposition phase through IR v4 keyed fan-out.
2. Run the recorded failure corpus and compare the old and Fuwen paths for
   outputs, diagnostics, repair/retry rate, cost, provenance, and restart scope.
3. Complete one live dogfood run and remove the old path only after behavioral
   parity and focused-restart reuse are demonstrated.

The minimal text grammar is now implemented. Recursive schemas, enum literals,
numeric/Unicode portability, runtime-value validation, and context-snapshot
evidence are now closed for the current programmatic surface. IR v3's declared,
typed context requirements, callable-effect/repair explanations, and canonical
execution invocation identity are also complete. The provider-neutral typed
execution ports, Delivery C's SQLite-backed Zhinu adapter, and Delivery E's
Baize structured/media adapters are complete. The durable vertical proves
context snapshots, real adapter execution, typed cost and provider evidence,
recovery, selective restart, idempotent operation and artifact publication
identity, cancellation, and corruption rejection. The admission receipt remains
its authorization seam; it is not itself an executor. Loops, waits, transition
activation, and artifact-reuse authorization remain outside the first preview.
