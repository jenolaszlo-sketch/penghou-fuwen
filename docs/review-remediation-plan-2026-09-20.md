# Fuwen review remediation and product-evolution plan

Date created: 2026-09-20

Status: active planning document; no remediation tasks completed yet

Source review: [Fuwen product, design, and implementation review](review-2026-09-20.md)

## Goal

Make Fuwen's central promise true end to end: a workflow is a reviewable,
bounded contract whose model-visible behavior, authority, execution evidence,
recovery, and revision explanation agree.

The first release objective is to close findings R31–R38 without weakening
exact identities, admission, bounded execution, or package boundaries. Product
work then makes the resulting system easy to try, diagnose, operate, and adopt.

## How to maintain this plan

- `[ ]` means the task is not complete. `[x]` means its implementation,
  required tests, documentation, and verification are complete.
- Check a child task in the same commit that completes it. Add a short evidence
  note after the task: `Completed YYYY-MM-DD in <commit>; verified by <tests>`.
- Check a parent task only when every required child is checked and its exit
  criteria pass. Partial code is not completion.
- If scope changes, edit the task before implementing it. Do not silently make
  the checkbox mean something narrower.
- A deferred task stays unchecked and moves to **Deferred decisions**, with the
  reason, dependency, and condition for resuming it. Deferral is not completion.
- When a finding is complete, update both this plan and the finding status in
  [the review](review-2026-09-20.md). Do not erase the original evidence.
- Record design decisions that change public or persisted contracts in an ADR.
  Record compatibility/version consequences explicitly.
- Keep progress claims reproducible: name the test, command, sample, or release
  run that supplies the evidence.

## Guardrails

- Preserve the separation between Fuwen semantics/admission, Zhinu durability,
  Baize inference integration, and host authority.
- Do not weaken exact descriptor pins or execution identity to improve
  authoring ergonomics.
- Do not add general scripting, unbounded loops, implicit authority, hidden
  credentials, or an allow-all setup path.
- Treat existing v3–v7 persisted plans as compatibility contracts. Version any
  intentional semantic change instead of reinterpreting old bytes.
- Avoid new node kinds, a graphical editor, or marketplace infrastructure until
  Milestones 1–3 are complete and consumer evidence justifies them.
- Product-specific workflow logic stays in consumers such as Guyabano. Fuwen
  may provide reusable contracts, conformance tooling, and examples.

## Milestone 0 — Establish the executable baseline

Purpose: convert the review's isolated reproductions into repository-owned
evidence before changing behavior.

- [ ] **M0.1 — Add regression fixtures for R31–R36.**
  - [x] Capture the exact provider request and evidence for registered-template
    tools: none, subset, full set, and a missing host binding. Completed
    2026-09-20 in the working tree; verified by
    `Registered_template_exposes_only_explicit_tools_and_evidence_matches_provider_request`
    and `Registered_template_missing_declared_tool_fails_before_provider_call_with_evidence`
    on .NET 8 and .NET 10.
  - [ ] Compile and admit a registered prompt alias, then prove its current
    request-construction failure before implementing R32.
  - [x] Compare plans whose only change is a prompt-binding value and assert the
    node-level dependency explanation. Completed 2026-09-20 with literal-value
    and canonical binding-order regressions on .NET 8 and .NET 10.
  - [x] Exercise media generation with a cooperative provider where the plan
    timeout is stricter than the host timeout. Completed 2026-09-21 with a
    cancellation-aware delayed submission and exact deadline provenance.
  - [ ] Capture a workflow-owned prompt request with a unique context value and
    prove whether that value reaches the fake model.
  - [x] Compile descriptor shorthand through both discovery-capable and
    exact-resolution-only catalogues. Completed 2026-09-20; the former emits an
    exact descriptor and the latter fails without acquiring discovery.
- [ ] **M0.2 — Add documentation/release contract checks for R37–R38.**
  - [x] Reject duplicate JSON object keys in `docs/fuwen-grammar.json`.
    Completed 2026-09-20 with a recursive documentation-contract test.
  - [ ] Compile every checked-in `.fuwen` documentation example.
  - [ ] Add a non-publishing validation test for release tag/project-version
    equality and release-job prerequisites.
- [x] **M0.3 — Record baseline results.** Completed 2026-09-20 after the current
  changes: Release build clean; 483 tests pass on each of .NET 8 and .NET 10
  (966 target-framework executions); independent canonical JSON vector passes.

Exit criteria: every R31–R38 defect has a failing repository-owned test or an
equivalent static workflow check that will turn green with the fix. The normal
suite remains green apart from tests intentionally demonstrating known defects.

## Milestone 1 — Restore contract fidelity

Purpose: make model-visible behavior and execution outcomes match the admitted
plan before adding more language features.

### R31 — Enforce the exact declared tool surface

- [x] **M1.1 — Decide and document null/empty/version semantics.** Specify how
  historical v3–v7 registered-template defaults differ from v8's explicit
  allowlist, including `tools none`. Completed 2026-09-20 in the working tree;
  documented in `docs/execution-ports.md` and the execution-port XML contract.
- [x] **M1.2 — Preserve tool intent through Zhinu request construction.** Do not
  collapse an explicit empty v8 set into an ambiguous legacy default. Completed
  2026-09-20 in the working tree; verified by
  `V8_registered_template_tools_none_remains_explicit_at_execution_boundary`.
- [x] **M1.3 — Filter registered-template model tools.** Make both prompt modes
  expose only the admitted request set. Completed 2026-09-20 in the working
  tree; verified for none, subset, full set, and missing bindings on both target
  frameworks.
- [x] **M1.4 — Align evidence with the provider request.** Assert that
  model-visible tools and `AdmittedTools` are identical in every supported
  prompt mode. Completed 2026-09-20 in the working tree; the provenance sink,
  returned evidence, and captured provider request are asserted together, and
  legacy host defaults now appear in evidence.
- [x] **M1.5 — Cover compatibility and recovery.** Completed 2026-09-20. Tests
  cover v8 none/subset/full, missing bindings, historical defaults, repeat and
  fan-out execution, persisted recovery, replay, and repeat-step restart.

Exit criteria: an undeclared host-bound tool cannot appear in the provider
request, and the evidence reports exactly what the model could see.

### R32 — Make registered prompt aliases executable or fail early

- [ ] **M1.6 — Define the runtime contract in an ADR.** Choose an exact,
  host-bound registered-prompt resolution/mapping mechanism with typed
  parameters and immutable source identity.
- [ ] **M1.7 — Add adapter preflight.** Missing or unsupported registered-prompt
  bindings fail before workflow registration or provider work, with an
  actionable diagnostic.
- [ ] **M1.8 — Implement the supported resolution path.** Avoid rendering an
  alias as an empty inline prompt and retain its exact descriptor evidence.
- [ ] **M1.9 — Add end-to-end tests.** Execute through source compiler,
  admission, Zhinu, and a fake Baize provider; cover parameter mapping, missing
  binding, changed descriptor, and no-provider-call failure behavior.

Exit criteria: every admitted alias accepted by adapter preflight executes with
the exact registered identity, and unsupported aliases are rejected before a
run begins.

### R34 — Enforce inference limits consistently

- [x] **M1.10 — Specify deadline coverage.** Define whether submission,
  polling, publication, and recovery are inside the plan timeout and how an
  unknown remote outcome is represented. Completed 2026-09-21 in ADR 0006.
- [x] **M1.11 — Apply the stricter effective deadline.** Media generation uses
  the tighter supported host/plan limit and does not silently ignore a declared
  limit. Unsupported token limits now fail before provider invocation.
- [x] **M1.12 — Preserve honest cancellation evidence.** Distinguish caller
  cancellation, local deadline, provider timeout, and possibly committed remote
  work; do not imply remote rollback. Completed 2026-09-21 with distinct
  provider codes and conservative committed-effect evidence.
- [x] **M1.13 — Add timeout tests.** Cover plan stricter than host, host stricter
  than plan, submission, polling, caller cancellation, publication boundary,
  and non-retry behavior. Completed 2026-09-21 on .NET 8 and .NET 10.

Exit criteria: all supported inference modalities enforce or explicitly reject
each declared limit, and failures accurately describe commitment uncertainty.

### R35 — Define context delivery for workflow-owned prompts

- [ ] **M1.14 — Decide the context contract in an ADR.** Distinguish evidence-
  only dependencies from model-consumed context and define bounded mapping,
  redaction, artifact handling, identity, and evidence.
- [ ] **M1.15 — Implement explicit delivery or rejection.** Context must reach
  the intended model input through the defined mapping, or preflight must reject
  the unsupported request. Never silently discard it.
- [ ] **M1.16 — Add cross-region tests.** Verify unique context data, snapshot
  identity, truncation/redaction, no implicit artifact dereference, and the same
  behavior in sequential, repeat, and fan-out inference.

Exit criteria: operators can determine which context was intended for model
consumption and which bounded representation was actually supplied.

## Milestone 2 — Make authoring, comparison, and release trustworthy

### R33 — Explain prompt-binding changes accurately

- [x] **M2.1 — Compare canonical prompt-binding values as dependencies.**
  Completed 2026-09-20 with normalized binding-name ordering and full canonical
  binding values.
- [x] **M2.2 — Attribute prompt-definition changes to affected inference nodes.**
  Keep root semantic changes visible while making the affected execution path
  understandable. Completed 2026-09-20 by including the referenced prompt's
  semantic digest in inference-node semantics.
- [x] **M2.3 — Add comparison fixtures.** Cover literals, input/node
  projections, source-node changes, equivalent binding order, and downstream
  effects. Confirm that comparison remains explanatory, not authorization.
  Completed 2026-09-21 with node-level dependency assertions and an explicit
  unchanged downstream result that makes no reuse claim.

Exit criteria: every execution-fingerprint change caused by prompt content or
bindings has an accurate node-level explanation.

### R36 — Preserve catalogue capabilities through decorators

- [x] **M2.4 — Forward optional discovery safely.** The cache exposes discovery
  only when the wrapped catalogue supports it and still emits exact pins.
- [x] **M2.5 — Audit other optional catalogue capabilities.** Completed
  2026-09-20. The optional interfaces are discovery and immutable snapshot
  identity; capability-specific cache decorators now preserve either or both
  without adding them to catalogues that do not implement them.
- [x] **M2.6 — Test custom catalogues and budgets.** Cover discovery-capable,
  exact-only, in-memory, cache-hit, failure, cancellation, and lookup-accounting
  paths. Completed 2026-09-21 across the source-compiler and resolver suites;
  diagnostic-free custom-catalogue failures now receive a stable fallback.

Exit criteria: author-friendly shorthand resolves through declared catalogue
capabilities while compiled plans remain exactly pinned.

### R37 — Make the language reference authoritative

- [x] **M2.7 — Repair `fuwen-grammar.json`.** Remove the duplicate inference
  production and describe prompts, tools, limits, optional context, repeat,
  checkpoint, wait, and current fan-out bodies accurately. Completed
  2026-09-20 and guarded by recursive duplicate-key and production-shape tests.
- [ ] **M2.8 — Update `fuwen-authoring.md` and README examples.** Reconcile tool
  effects and all supported constructs with the compiler.
- [ ] **M2.9 — Add documentation conformance.** Detect duplicate keys and
  compile a syntax corpus with at least one fixture per node kind and inference
  form in CI.
- [ ] **M2.10 — Choose a sustainable source of truth.** Generate reference data
  from shared feature metadata, or document ownership and test the handwritten
  catalogue against parser fixtures.

Exit criteria: common strict JSON parsers accept the grammar, and every claimed
syntax form is exercised against the actual compiler.

### R38 — Enforce release integrity

- [ ] **M2.11 — Reuse validation in publication.** Build, format, multi-target
  tests, golden vectors, and packaging run for the exact release commit before
  publishing.
- [ ] **M2.12 — Reject version disagreement.** A tag must exactly equal the
  checked-in package version; it must not silently override it.
- [ ] **M2.13 — Define manual-dispatch policy.** Manual releases receive the
  same validation and an explicit version/ref contract.
- [ ] **M2.14 — Publish validated artifacts only.** The packages pushed are the
  packages produced by the successful validation job.
- [ ] **M2.15 — Test failure paths without publishing.** Prove that bad tags,
  failing tests, missing artifacts, and wrong commits cannot reach the push
  step.

Exit criteria: the checked-in workflow enforces the preview release checklist
for the exact commit and artifacts being published.

### Documentation reconciliation

- [ ] **M2.16 — Bring release-facing documents up to date.** Update the
  changelog from preview.2 through the current release, README tool semantics,
  capability matrix, and release checklist status.
- [ ] **M2.17 — Replace the threat-model starter.** Document assets, trust
  boundaries, concrete controls, residual risks, and host responsibilities
  without implying an external audit.
- [ ] **M2.18 — Reconcile historical findings.** Verify R24 regression coverage
  and mark its historical record accurately; review R20–R23 and R29 against the
  current implementation without erasing history.

Exit criteria: README, changelog, authoring reference, threat model, review,
roadmap, and package version describe the same shipped surface.

## Milestone 3 — Ship an excellent first-run and preflight experience

Purpose: make the stable contract useful to someone who did not build Fuwen.

- [ ] **M3.1 — Add an adapter feature manifest and execution preflight.** Report
  prompt forms, context delivery, tool mode, output modality, missing bindings,
  and limit support before registration or paid work. Keep this separate from
  language validity and admission authority.
- [ ] **M3.2 — Build a credential-free reference host.** Use public package
  surfaces and deterministic fakes; require no sibling checkout.
- [ ] **M3.3 — Demonstrate the whole lifecycle.** The walkthrough must compile,
  inspect, admit, run, deliberately fail, resume, edit, compare, and explain
  reuse/rejection.
- [ ] **M3.4 — Include one useful non-code workflow.** Use bounded document or
  research artifacts to demonstrate that Fuwen is not code-generation-specific.
- [ ] **M3.5 — Add a reusable diagnostic renderer.** Render source excerpts,
  spans, expected/actual types, stable codes, and bounded repair hints; expose
  structured JSON as well as readable text.
- [ ] **M3.6 — Add a minimal CLI surface.** Provide `check`, `format`,
  `explain`, and `diff` equivalents over library contracts. Final names are an
  implementation decision; do not fork compiler behavior into the CLI.
- [ ] **M3.7 — Add reviewed descriptor locking.** Authoring aliases resolve to
  exact identities in a diffable lock/catalogue manifest before admission;
  execution never resolves an implicit `latest`.
- [ ] **M3.8 — Add thin host setup helpers after the sample stabilizes.** Fail
  closed on missing ports/policies, retain low-level APIs, and avoid implicit
  credentials or `AllowAll` defaults.
- [ ] **M3.9 — Test the onboarding target.** Observe first-time users and record
  whether they can complete a local run and recovery within the proposed
  15-minute target; revise the experience based on evidence.

Exit criteria: a new developer can follow one maintained path from source to a
recovered run, understands failures from diagnostics, and does not need internal
test fixtures or hidden host configuration.

## Milestone 4 — Make tools, evidence, and recovery operationally useful

- [ ] **M4.1 — Publish a tool-mode capability contract.** Clearly distinguish
  synthetic structured-output tools, proposed actions returned to a host, and
  bounded executed tools.
- [ ] **M4.2 — Design a host tool-execution port.** Require exact descriptors,
  typed arguments/results, finite rounds, aggregate limits, stable operation
  keys, per-call durable evidence, and explicit effect/retry rules.
- [ ] **M4.3 — Implement the read-tool vertical slice.** Execute one permitted
  lookup, return its result to the model, validate typed output, and recover
  without repeating a completed call.
- [ ] **M4.4 — Gate write tools on durable semantics.** Do not present model-
  driven writes as supported until multi-call replay, unknown commitment,
  idempotency, and approval behavior are proven. Record a separate ADR.
- [ ] **M4.5 — Add an operator explanation report.** Show effective tools,
  context, limits, admission/preflight failures, attempts, usage/cost quality,
  possible committed effects, revision impact, and evidence reuse/rejection.
- [ ] **M4.6 — Protect sensitive provenance.** Default to safe summaries;
  require explicit access for raw provider output or context. Reuse existing
  invocation identities for correlation.
- [ ] **M4.7 — Verify fork/reuse cases.** Cover same-fingerprint/new-run and
  changed-fingerprint forks, runtime-path checks, copied evidence, rejection,
  and focused restart.

Exit criteria: the reference workflow performs a useful admitted tool call with
bounded, durable, explainable behavior and survives recovery correctly.

## Milestone 5 — Systematize compatibility and run budgets

- [ ] **M5.1 — Maintain a cross-feature compatibility matrix.** Cover prompt
  form, adapter, execution region, tools, context, limits, restart, and IR
  version with pairwise coverage plus critical full-path cases.
- [ ] **M5.2 — Track each feature across every representation.** Parsing,
  validation, normalization, fingerprinting, snapshotting, comparison,
  accounting, request mapping, evidence, and replay each require coverage.
- [ ] **M5.3 — Extract internal traversal/request/evidence components.** Reduce
  omissions in the large compiler and interpreter while preserving public APIs,
  canonical bytes, and golden vectors. Do not create an unnecessary public
  extension framework.
- [ ] **M5.4 — Publish an adapter conformance test kit.** Include adversarial
  output, unsupported capabilities, malformed evidence, cancellation, unknown
  usage, and recovery.
- [ ] **M5.5 — Define a host-owned run budget.** Specify reservations and
  accounting across fan-out, repeat, retries, repairs, tools, resume, unknown
  usage, concurrent work, and pricing-policy changes.
- [ ] **M5.6 — Enforce and explain aggregate limits.** Keep compiler budgets,
  output limits, deadlines, token allowances, monetary estimates, and committed
  spend distinct in APIs and evidence.

Exit criteria: feature additions cannot silently disappear between layers, and
a host can bound total run exposure rather than only individual calls.

## Verification required for every completed milestone

- [ ] `dotnet format Penghou.Fuwen.slnx --verify-no-changes --no-restore`
- [ ] `dotnet build Penghou.Fuwen.slnx --configuration Release`
- [ ] `dotnet test Penghou.Fuwen.slnx --configuration Release --no-build`
- [ ] Independent canonical JSON vectors pass.
- [ ] Changed packable projects pass package validation and public API review.
- [ ] New behavior is tested on .NET 8 and .NET 10.
- [ ] Relevant source, authoring, adapter, threat, and release documentation is
  updated in the same change.
- [ ] No test, example, diagnostic, or evidence fixture contains credentials,
  sensitive payloads, or machine-specific absolute paths.
- [ ] The completed task and its parent status are updated in this plan with
  evidence; corresponding review findings are marked resolved when applicable.

These verification boxes are a reusable checklist. Check them for a milestone
only when recording that milestone's completion, then add the dated evidence
below rather than leaving ambiguous global checkmarks.

## Completion log

Add one entry per completed parent task or milestone:

| Date | Task | Commit/PR | Verification evidence | Notes |
| --- | --- | --- | --- | --- |
| 2026-09-20 | M0.1 tool regression subtask; M1.1–M1.4 | Working tree; commit pending | Format clean; Release build 0 warnings/errors; 475 tests pass on each of .NET 8 and .NET 10; independent canonical JSON vector passes | R31 remains in progress until M1.5 cross-region and replay coverage is complete |
| 2026-09-20 | M0.1 comparer/catalogue subtasks; M0.2 duplicate-key subtask; M0.3; M1.5; M2.1–M2.2; M2.4–M2.5; M2.7 | Working tree; commit pending | Format clean; Release build 0 warnings/errors; 483 tests pass on each of .NET 8 and .NET 10 (966 executions); independent canonical JSON vector passes | R31 resolved; R33 and R36 core defects resolved; broader M2.3, M2.6, and R37 documentation work remains |
| 2026-09-21 | M2.3; M2.6 | Working tree after `4e2b07e`; commit pending | Format clean; Release build 0 warnings/errors; 488 tests pass on each of .NET 8 and .NET 10 (976 executions); independent canonical JSON vector passes | R33 and R36 acceptance matrices complete; custom catalogues cannot produce silent exact-resolution failures |
| 2026-09-21 | M0.1 media-timeout subtask; M1.10–M1.13 | Working tree after `2bfe03b`; commit pending | Format clean; Release build 0 warnings/errors; 495 tests pass on each of .NET 8 and .NET 10 (990 executions); independent canonical JSON vector passes | R34 resolved; ADR 0006 defines one effective deadline across submission, polling, and publication |

## Deferred decisions

Move intentionally deferred items here; do not check them as complete.

| Task | Reason | Resume when |
| --- | --- | --- |
| None | — | — |
