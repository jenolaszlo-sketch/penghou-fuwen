# Bounded complex inference implementation plan

Date created: 2026-09-21

Status: implementation in progress; CI-0 through CI-7 complete

Primary consumer: Marang supervisor-authored Fuwen workflows

Source design: [Bounded complex activities](complex-activities.md)

## Outcome

Evolve the existing `infer` node into one bounded, durable logical activity
whose implementation may perform several model turns and exact read-oriented
tool calls without expanding protocol mechanics into authored workflow nodes.

The delivered vertical slice must let Marang submit an admitted workflow that
expresses:

```text
perform bounded inference
using this exact prompt and profile
with these exact read tools
producing this typed result
```

The runtime may execute model → tool → model internally, but every paid or tool
operation remains bounded, durably identified, recoverable, and explainable.

## Decisions already made

- The first complex activity is the existing `infer` node. Do not add a new
  generic activity kind or public `IComplexActivityHandler` extension surface.
- Fuwen owns authored meaning: prompt, typed bindings/context, output contract,
  exact tools, aggregate limits, dependencies, and semantic control flow.
- Zhinu owns durable execution, fencing, internal operation journaling,
  cancellation, restart, and recovery.
- Baize owns provider/model transport and one normalized model turn. It does not
  become a workflow runtime or the authority for durable replay.
- The host owns tool implementations, authorization, credentials, capability
  scopes, pricing, and aggregate ceilings.
- Nuwa-compatible repair remains limited to representation recovery. Semantic
  rejection, replanning, and repair stay explicit workflow behavior.
- Hongxian/Siming may retain and correlate evidence, but neither is the
  authoritative execution journal.
- Only effect-free and read-only tools are supported in the first slice.
  Model-driven writes remain unsupported until a separate ADR proves durable
  idempotency, approval, unknown-commitment, and compensation semantics.
- Workflow-owned and registered prompts remain first-class Fuwen definitions.
  The protocol runtime never invents or silently replaces the business prompt.
- The current IR evolves in place while Fuwen has no external persisted-plan
  consumers. Do not retain version branches, protocol-revision checks, or
  migration machinery solely for repository-internal history.
- Canonical identity still changes whenever authored plan semantics change;
  the repository keeps one golden contract for the current IR.

## Marang boundary

Marang is the first product proof, not the owner of these primitives.

```text
Marang MCP/service policy
    -> verifies/selects an immutable Fuwen definition
    -> obtains current admission and adapter preflight
    -> starts/supervises a Zhinu run

Fuwen infer node
    -> exact prompt/profile/tools/types/limits

Zhinu complex-inference coordinator
    -> durable model-turn and tool-call operations

Baize + host read-tool port
    -> provider calls and authorized read execution
```

Qingniao continues to own one delegated execution and its supervision lifecycle.
It is not the Fuwen inference-protocol runtime. A workflow may invoke a future
Qingniao-backed explicit activity, but complex `infer` must not leak Qingniao or
Marang types into Fuwen contracts.

The Marang proof should use a useful planning/review scenario with exact
repository read/search/graph tools and a typed result. Deployment, Git commit,
push, publication, workspace mutation, and workflow activation remain explicit
authorized activities outside `infer`.

## Contract shape to freeze

The names below are provisional until Milestone CI-0 records the ADRs. The
semantics and ownership boundaries are requirements.

### Authored protocol limits

The current IR should add aggregate inference bounds alongside the existing
per-call `MaxTokens` and wall-clock timeout:

- maximum protocol turns;
- maximum model calls;
- maximum tool calls;
- maximum aggregate prompt, completion, and/or total tokens;
- maximum duration for the logical activity;
- optional maximum cost in host-selected currency and pricing revision;
- maximum tool argument/result bytes;
- maximum retained conversation/evidence bytes.

Every bound is finite after source and host policy are combined. Source limits
may narrow host ceilings and may never expand them. Compiler budgets, model
output limits, protocol totals, deadlines, estimated cost, and committed spend
remain distinct concepts and types.

Unknown token or price usage is never zero. If an unknown value prevents proof
that another paid operation fits the remaining budget, the coordinator stops
before that operation and returns a normalized budget outcome.

### Adapter feature manifest and preflight

Add a provider-neutral immutable feature manifest that reports at least:

- registered-template and workflow-owned prompt support;
- structured-content and synthetic structured-output support;
- context delivery support and maximum payload;
- exact tool-call support and supported tool effects;
- supported inference semantics and current IR contract;
- supported limit dimensions and maxima;
- resumable/reconcilable operation support;
- usage and pricing evidence quality.

Preflight compares one exact inference requirement with the manifest and host
policy. It returns a structured report containing effective limits, matched
features, missing bindings, and stable diagnostics. Registration fails before
definition storage or paid work when the report is not executable.

### Turn and tool ports

Prefer inference-specific provider-neutral ports over a generic complex-activity
framework:

- an inference-turn executor accepts bounded normalized conversation state and
  returns either a final candidate or exact tool-call proposals;
- a read-tool executor accepts one exact admitted descriptor, typed bounded
  arguments, capability scope, stable operation identity, and cancellation;
- both return normalized outcomes and evidence without embedding credentials,
  filesystem paths, provider client objects, or Zhinu contexts;
- deterministic fake implementations are first-class conformance fixtures.

Update the current inference adapters to implement the complex-inference
semantics directly. Do not retain a parallel legacy one-call path without a
real external consumer that requires it.

### Durable operation journal

One logical inference invocation has a stable interaction identity derived from
the execution fingerprint, structural/runtime path, step revision, and
effective request fingerprint. Internal operations add an ordinal and kind:

```text
<interaction>/model/0001
<interaction>/tool/0001/<model-tool-call-id>
<interaction>/model/0002
<interaction>/validation/0001
```

For each operation, Zhinu must durably record claim/fence identity, normalized
request digest, disposition, bounded result or protected payload reference,
usage/cost quality, commitment uncertainty, and terminal failure. Recovery
must replay a completed operation, reconcile an ambiguous external operation,
or stop for operator action; it must never silently issue a second operation
with a new idempotency identity.

The journal stores safe summaries and immutable references by default. Raw
provider content, context, tool payloads, credentials, and chain-of-thought are
not required evidence and must not be copied into ordinary workflow history.

### Normalized terminal outcomes

The public failure vocabulary should distinguish at least:

- protocol turn/model-call/tool-call/token/time/cost limit reached;
- unsupported protocol or provider capability;
- tool rejected, unavailable, malformed, or failed;
- provider failed or returned malformed protocol data;
- final output representation/schema validation failed;
- cancelled or fencing lost;
- ambiguous operation may have committed;
- corrupt or incompatible durable protocol state.

Provider codes remain bounded evidence. Workflow code reacts to stable Fuwen
outcomes, not provider exception types.

## Milestones

Check a task only when its implementation, tests, public API review, and
relevant documentation are complete. Record the commit and verification in the
completion log.

### CI-0 — Freeze decisions and baseline evidence

- [x] Write an ADR for the inference protocol boundary, including the decision
  not to publish a generic complex-activity handler in the first release. See
  [ADR 0009](decisions/0009-inference-protocol-is-one-logical-activity.md).
- [x] Write an ADR for internal operation identity, Zhinu journal ownership,
  fencing, replay, reconciliation, and protected payload references. See
  [ADR 0010](decisions/0010-zhinu-owns-inference-operation-journal.md).
- [x] Write an ADR for aggregate budget reservations and unknown usage/pricing.
  See [ADR 0011](decisions/0011-aggregate-inference-budgets-reserve-unknown-usage.md).
- [x] Capture the pre-CI-2 one-call behavior for no tools, declared read tools,
  structured output, context, repeat, fan-out, replay, and cancellation in the
  executable test suite before replacing it with the current contract.
- [x] Add a checked-in Marang planning scenario and deterministic expected
  trace without adding a Marang project dependency. See
  [the Marang scenario](marang-complex-inference-scenario.md).

Exit: the authority, durability, privacy, and budget decisions are
reviewable before any new public or persisted contract is added.

### CI-1 — Feature manifest and executable preflight

- [x] Introduce the immutable adapter feature manifest and structured preflight
  report in the provider-neutral core.
- [x] Extend exact inference requirements with prompt form, modality, context,
  tools/effects, limits, protocol revision, and recovery requirements.
- [x] Implement manifests/preflight for Baize structured text, media, exact
  routing, and deterministic fakes. Unsupported combinations fail closed.
- [x] Make Zhinu registration require successful structured preflight before
  definition storage, carry the admitted IR version into the requirement, and
  fail closed when an adapter cannot report the requested protocol/IR support.
- [x] Add a human-readable and JSON explanation renderer for the report.

Evidence: unit matrices for supported/unsupported combinations, missing exact
bindings, too-weak limits, legacy executors, and zero-provider-work rejection.

Exit: Marang can explain whether a plan is executable, and why, without
starting or paying for it.

### CI-2 — Complex inference limits and identity

- [x] Define inference protocol and aggregate-limit records with bounded
  constructors and snapshot semantics.
- [x] Extend parsing, validation, normalization, canonical JSON, fingerprints,
  source maps, usage accounting, comparison, and explanations.
- [x] Define the source syntax and update grammar metadata, authoring reference,
  capability matrix, and compiler-backed examples.
- [x] Add canonical/fingerprint golden vectors for the **current IR**.
- [x] Update adapters to support the new inference semantics directly.
- [x] Delete obsolete IR-version compatibility code, legacy golden vectors,
  migration paths, and protocol-revision checks.

Exit: authored complex-inference meaning is immutable, comparable, bounded,
and implemented directly by every supported adapter without legacy-version
branches.

### CI-3 — Turn/tool contracts and deterministic conformance harness

- [x] Add the inference-turn request/result contracts, normalized conversation
  state, exact tool-call proposal identity, and bounded continuation data.
- [x] Add the read-tool execution request/result/evidence contracts with exact
  descriptor, typed arguments/result, scope, operation key, and retry safety.
- [x] Implement Baize one-turn mapping for supported provider tool-call shapes.
- [x] Implement deterministic fake model and read tools capable of success,
  malformed calls, duplicate IDs, cancellation, unknown usage, and ambiguity.
- [x] Publish internal/shared conformance suites for turn and tool adapters.
- [x] Prove undeclared, write-capable, duplicate, oversized, or mistyped tool
  calls are rejected before tool execution.

Exit: provider transport and host tools can be tested independently of the
durable coordinator, with exact model-visible and executable tool sets.

### CI-4 — Durable complex-inference vertical slice

- [x] Implement a Zhinu-owned inference protocol coordinator under one logical
  Fuwen `infer` node.
- [x] Journal every model turn, tool call/result, validation/repair operation,
  aggregate usage update, and terminal outcome with stable operation identity.
- [x] Execute one exact read tool, return its result to the model, and validate
  the final typed output.
- [x] Enforce effective source/host bounds before each operation using atomic
  reservations where concurrency or external commitment requires them.
- [x] Reuse completed operations on replay and reconcile ambiguous provider
  handles without restarting the logical activity.
- [x] Preserve current cancellation and fencing semantics at every boundary.

Exit: a crash at any model/tool boundary resumes without duplicate completed
work, and the enclosing workflow still observes one logical inference result.

### CI-5 — Evidence, privacy, and operator explanation

- [x] Extend inference evidence with interaction identity, current inference
  semantics, per-operation summaries, aggregate limits/usage, tool outcomes, validation,
  recovery disposition, and commitment uncertainty.
- [x] Keep sensitive payloads behind explicit host-controlled references and
  access checks; normal reports contain digests and safe summaries.
- [x] Extend the operator explanation report to show effective prompt form,
  context, tools, limits, attempts, cost quality, replay/reconciliation, and
  final outcome.
- [x] Add bounded Hongxian/Siming correlation adapters only as optional sinks;
  loss of a non-authoritative sink cannot alter execution truth.
- [x] Update the threat model for prompt injection through tool results,
  exfiltration, malicious tool metadata, journal corruption, and budget races.

Exit: an operator can answer why the activity produced its output and what it
could access without exposing secrets or requiring raw chain-of-thought.

### CI-6 — Recovery and compatibility matrix

- [x] Cover zero-tool one-turn, one-tool multi-turn, multiple calls, model
  refusal, malformed tool requests, tool failure, validation repair, every
  limit, caller cancellation, fencing loss, and corrupt journal entries.
- [x] Run critical cases in sequential, repeat, and fan-out regions.
- [x] Inject crashes before/after model submission, provider-handle storage,
  model completion, tool claim, tool completion, result persistence, final
  validation, and node completion.
- [x] Cover same-fingerprint and changed-fingerprint forks, copied evidence,
  explicit rejection, and focused restart.
- [x] Verify unknown usage/pricing and changed pricing revisions never
  authorize an unprovably affordable additional operation.
- [x] Keep a pairwise feature matrix and critical full-path Marang scenario in
  CI on .NET 8 and .NET 10.

Exit: compatibility gaps become test failures, not runtime surprises.

### CI-7 — Marang consumer proof and release gate

- [x] Build a credential-free package-consumer sample using only published
  public surfaces and deterministic model/tools.
- [x] Demonstrate compile, explain, preflight, admit, run, fail, resume, inspect
  evidence, fork/restart, and return a typed planning result.
- [x] Exercise the same immutable definition through a minimal Marang host
  adapter without moving workflow or protocol semantics into Marang/Qingniao.
- [x] Prove a proposed mutation or external side effect is returned as typed
  data and executed only by a separate explicitly authorized workflow step.
- [x] Validate package APIs, docs, compatibility notes, release workflow, and a
  clean restore in an isolated consumer.
- [x] Record a second non-Marang consumer before considering any generic
  complex-activity abstraction.

Exit: Marang can safely consume complex inference as a supervision feature,
and Fuwen remains independently useful and product-neutral.

### CI-8 — Write-tool gate (deferred)

- [ ] Do not expose write-capable model tools as supported during CI-0–CI-7.
- [ ] Resume only with a real consumer, a separate ADR, provider idempotency or
  reconciliation proof, approval semantics, unknown-commitment handling,
  compensation/forward recovery, and adversarial crash tests.

Exit: none for the initial complex-inference release; this gate remains closed.

## Beyond previews: graduation

Graduating the packages out of preview (preview → RC → stable) is tracked
separately in [graduation requirements](graduation-requirements.md): API
freeze with Shipped baselines, live-provider conformance, rulings on every
deferred item, the Guyabano pilot, human security review, and a scoped
definition of done. Preview releases must not claim any of those gates.

## Required test architecture

Tests should be layered so a failure identifies the broken contract:

1. Core contract tests: construction bounds, snapshots, canonical identities,
   manifest/preflight, budgets, outcomes, and evidence.
2. Compiler tests: source, diagnostics, validation, normalization, current-IR
   golden vectors, comparison, and explanation.
3. Baize tests: one-turn mapping, tool proposal normalization, exact tool
   visibility, usage/cost quality, payload ceilings, cancellation, and provider
   adversaries.
4. Zhinu tests: journal fencing, operation replay/reconciliation, crash
   injection, resume, focused restart, mutation/forks, and corrupt state.
5. End-to-end tests: source → admission → preflight → Zhinu → Baize/fake tools →
   typed output/evidence across sequential, repeat, and fan-out regions.
6. Consumer tests: isolated-package Marang scenario with no sibling checkout.

Every completed milestone must pass:

- `dotnet format Penghou.Fuwen.slnx --verify-no-changes --no-restore`;
- Release build with zero warnings/errors;
- all tests on .NET 8 and .NET 10;
- independent canonical JSON vectors;
- package validation and public API review for changed packable projects;
- documentation contract checks and credential/path scanning.

## Contract cleanup and rollout

- Treat the current IR as the only supported contract until a real external
  consumer creates a compatibility obligation.
- Delete obsolete IR-version branches, legacy golden vectors, migration paths,
  and protocol-revision checks as part of CI-2.
- Update every supported adapter in the same release so there is no split
  between legacy one-call and complex-inference semantics.
- Feature flags may gate preview use, but they cannot bypass admission,
  preflight, exact tool grants, finite limits, or journal verification.
- Publish previews in slices: manifest/preflight, current contracts, fake durable
  vertical, then Baize/Marang proof. Do not wait to review all public contracts
  in one large release.
- Keep generated or mutable provider data out of workflow fingerprints. Bind
  its immutable evidence/reference to the execution journal instead.

## First implementation batch

Start with CI-0 and CI-1. The first code batch should contain no model/tool
loop. It should deliver:

1. the three ADRs;
2. a provider-neutral feature manifest;
3. structured preflight requirements/reporting;
4. Baize and exact-router manifests;
5. Zhinu pre-registration enforcement;
6. deterministic tests proving unsupported complex inference performs no paid
   or tool work;
7. the checked-in Marang planning scenario used by later milestones.

This batch reduces design risk and creates an observable gate for every later
protocol feature.

## Completion log

| Date | Task | Commit/PR | Verification | Notes |
| --- | --- | --- | --- | --- |
| 2026-09-21 | CI-0 contract decisions and compatibility baseline | `ec8a11c` | Full .NET 8/.NET 10 suite remains green through CI-1 | Three ADRs, v8 baseline, and deterministic Marang scenario |
| 2026-09-21 | CI-1 provider-neutral manifest and report foundation | `8153962` | Core public API, renderer, package, and matrix tests | Legacy constructor retained; write-tool gate closed |
| 2026-09-21 | CI-1 Zhinu pre-registration enforcement | `b9e35d7` | Zhinu tests on .NET 8/.NET 10 | Structured rejection precedes storage/provider work; v3-v8 failure vocabulary retained |
| 2026-09-21 | CI-1 Baize structured-text manifest | `30443a1` | Baize tests and package on .NET 8/.NET 10 | Conservative evidence/recovery claims and exact bindings |
| 2026-09-21 | CI-1 media, exact routing, and deterministic matrix | `95872c3` | 548 tests per framework; format; core/Baize pack; canonical JSON | Legacy modality remains unspecified; explicit mismatches fail closed |
| 2026-09-22 | CI-2 complex inference limits and identity | uncommitted | 561 tests per framework (210 core + 222 compiler + 69 Baize + 60 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | Current IR only; aggregate `limits ... aggregate ...` source syntax; single `workflow_plan_v1.json` golden (`abefd135...`); IrVersions/versioned contracts and v2-v7 goldens deleted |
| 2026-09-22 | CI-3 turn/tool contracts and deterministic conformance harness | uncommitted | 636 tests per framework (268 core + 222 compiler + 86 Baize + 60 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | `IInferenceTurnExecutor`/`IInferenceReadToolExecutor` ports, `InferenceTurnValidation` pre-execution rejection, `BaizeOneTurnMapper`, deterministic fakes, `InferenceTurnToolConformance` suites |
| 2026-09-22 | CI-4 durable complex-inference vertical slice | uncommitted | 647 tests per framework (268 core + 222 compiler + 86 Baize + 71 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | `FuwenInferenceCoordinator` LoopAsync journal, stable per-operation steps, replay reuse proven by crash injection, effective bound enforcement incl. `BudgetUnknown`, durable `TimeBudget`, `AmbiguousOperation` fail-closed; new failure codes and turn/read-tool ports |
| 2026-09-22 | CI-5 evidence, privacy, and operator explanation | uncommitted | 657 tests per framework (274 core + 222 compiler + 86 Baize + 75 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | `InferenceProtocolEvidence` + per-operation/tool-outcome summaries, `ProtectedPayloadReference` (digests only), human/JSON renderer, optional non-authoritative `IInferenceEvidenceSink` wired through `FuwenZhinuExecutionPorts`; coordinator emits evidence on success/failure incl. commitment uncertainty; threat model extended. Concrete Hongxian/Siming adapters are host-supplied through the generic sink and are intentionally not a repository dependency |
| 2026-09-22 | CI-6 recovery and compatibility matrix | uncommitted | 671 tests per framework (274 core + 222 compiler + 86 Baize + 89 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | Coordinator matrix covers every aggregate limit, multiple tool calls, unknown/exceeded cost, argument/result byte ceilings, duration budget, replay reuse, and the deterministic three-model/two-tool Marang trace. Coordinator now nests its loop into repeat regions (per-iteration interaction identity, `ProtocolLoopRunner`); crash points before/after model and tool completion inject via lease recovery. Fork/rejection and corrupt-journal coverage reuse the extended activity/inference recovery suites; unknown pricing is host-owned for revision pinning |
| 2026-09-22 | CI-2–CI-6 correctness review pass | uncommitted | 673 tests per framework (274 core + 222 compiler + 86 Baize + 91 Zhinu) on .NET 8/.NET 10; format clean; Release 0 warnings | Fixed: model turn could throw when its last tool budget was spent (omitted advisory cap); one-call inference nodes no longer required `Protocol` at registration unless a turn executor is configured; oversized tool results rejected before persistence; duration `TimeSpan` overflow clamped; `InferenceProtocolLimits` now capped like `InferenceLimit`; evidence no longer fabricates a currency or matches tools by name only; tools + turn executor now rejected at registration when no read-tool executor is present |
| 2026-09-23 | CI-7 Marang consumer proof and release gate | uncommitted | 8 isolated-package consumer tests per framework (6 Marang + 2 review) restored from packed packages with no sibling project references, plus 691 in-repo tests per framework (285 core + 222 compiler + 86 Baize + 98 Zhinu); format clean; Release 0 warnings | `tests/Penghou.Fuwen.MarangConsumer.Tests` (hand-written Marang adapter: deterministic planning trace, exact repo tools, workspace context, promotion activity, turn manifest) proves compile/explain/preflight/admit/run/fail/resume/evidence/fork/restart/typed `PlanningResult`/explicit promotion; `tests/Penghou.Fuwen.ReviewConsumer.Tests` records the second product-neutral consumer; `consumer-proof` CI job packs and runs both on .NET 8/.NET 10 |
