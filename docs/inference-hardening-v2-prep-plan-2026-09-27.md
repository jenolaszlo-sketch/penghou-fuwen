# Sol implementation plan: Fuwen inference hardening and V2 preparation

Created: 2026-09-27. Status: **implementation in progress; remaining gates open**.

First batch (2026-09-27): invocation identity now binds the workflow run and rendered request semantics; terminal turns validate settled aggregate token/cost evidence and declared currency; coordinated registration requires preflight from the selected turn executor; read-tool operation keys are fixed-length and opaque. Seven previously failing regression probes now pass. The full solution suite passed 700 tests per framework on .NET 8 and .NET 10 in an isolated implementation copy. In the target repository, the code suites passed, then the affected documentation contract tests passed after updating one stale assertion. The new interaction and tool-key identity formats require a migration/compatibility decision before upgrading in-flight journals.

Second batch (2026-09-27): `MaxRetainedEvidenceBytes` now caps canonical serialized evidence reports, truncates older operation/tool summaries with explicit flags, and rejects an impossible envelope before model work. The coordinator also narrows each turn request's completion-token ceiling to its remaining aggregate allowance. The combined changes passed 703 tests per framework on .NET 8 and .NET 10 in an isolated copy. Journal state remains outside the evidence-report cap.

Third batch (2026-09-27): usage/cost accumulation now treats overflow as unknown. Authored and host completion-token limits require a selected turn executor that declares a hard completion-token capability; requests narrow to the remaining aggregate allowance. Successful coordinated read-tool results now go through a host-owned durable protected-payload store. Ordinary step/conversation state retains a checked reference, and replay authorizes and verifies the stored bytes before the next turn. Registration rejects coordinated tools without that store. The target solution passed 710 tests on each of .NET 8 and .NET 10 (285 core, 222 compiler, 87 Baize, 116 Zhinu per framework). These are partial FI-02/FI-03 advances: prompt, total-token, and cost ceilings still lack hard pre-call guarantees; initial prompts, context, arguments, model outputs, and other ingress/export paths still need protected-payload treatment. Hosts must implement durable storage and authorization; the in-memory test store is only a fixture. The new coordinator journal semantics cannot resume older in-flight coordinator state without migration or an explicit stop.

Fourth batch (2026-09-27): the coordinator now removes tools from a finalization turn when the allowance is exhausted and passes an explicit zero-proposal ceiling. It rejects a proposed batch that exceeds the remaining allowance before running any tool in that batch. The target solution passed 712 tests on each of .NET 8 and .NET 10 (286 core, 222 compiler, 87 Baize, 117 Zhinu per framework). This is a narrow FI-04/FI-02 improvement, not typed tool validation. The current workflow plan retains tool descriptor references but not their trusted callable signatures; FI-04 must carry an exact versioned signature into durable admission before validating proposed argument names/types and returned values without trusting model or adapter claims.

Fifth batch (2026-09-27): coordinated registration and normalized turn requests reject different tool descriptors that share one provider-visible name. This closes a name-to-descriptor ambiguity before model work, but exact model-facing signatures and typed arguments/results were still open at that point. The README now compares Fuwen with application-led orchestration; the roadmap, capability matrix, complex-activities overview, migration notes, evidence/ports docs, threat model, changelog, and release checklist distinguish current source from the published preview. The target solution passed 714 tests on each of .NET 8 and .NET 10 (287 core, 222 compiler, 87 Baize, 118 Zhinu per framework), and format verification passed. No new NuGet package was published.

Sixth batch (2026-09-27): in-process admission carries exact trusted catalogue tool signatures into the Zhinu registration. The coordinated runtime rejects missing, extra, duplicate, or mistyped argument fields before tool I/O and rejects mistyped results before protected storage. Interaction identity and state semantics bind the admitted signatures for recovery. Five new tool-boundary cases pass; the full solution passes 720 tests per framework (287 core, 222 compiler, 87 Baize, 124 Zhinu). This is partial FI-04: a versioned provider-facing schema contract, resource-level grants, pure transition extraction, and broader failure distinctions remain open. Existing in-flight v2 coordinator state is explicitly unsupported without migration. No new package was published.

Seventh batch (2026-09-27): coordinated registration rejects a host-only monetary ceiling without an authored currency, and the runtime repeats that fail-closed check. Every turn request receives the remaining monetary allowance. Cost settlement retains its pricing revision and marks mixed or missing revisions as unknown rather than summing incompatible evidence. The coordinator state semantics advance to v4 so older in-flight v3 state stops without migration. Three new regressions pass; the solution has 723 tests per framework (287 core, 222 compiler, 87 Baize, 127 Zhinu). This remains partial FI-02: no durable pre-call reservation or strict prompt/total-token/cost guarantee exists.

Eighth batch (2026-09-27): the coordinated model-turn path now sends a linked cancellation token with the declared per-call deadline. A cooperative executor is interrupted at expiry, and the resulting timeout remains marked possibly committed; a late result is still rejected. The full Zhinu suite passed 127 tests on each of .NET 8 and .NET 10. This is partial FI-02 only: prompt, total-token and cost still have no durable pre-call reservation, and cancellation does not establish that remote work stopped.

Ninth batch (2026-09-27): authored prompt, total-token and cost limits retain strict default semantics and now fail coordinated registration before paid work because this runtime has no durable pre-call reservation and trusted maximum-charge contract. `aggregate advisory` explicitly opts into after-call monitoring; it participates in plan identity, and host strict ceilings cannot be downgraded. Existing strict canonical fingerprints remain stable. This removes a false hard-ceiling claim but does not implement the durable ledger, trusted quote, settlement, or provider guarantee required to support strict admission.

Tenth batch (2026-09-27): an optional SQLite leaf ledger atomically reserves a selected executor’s maximum quote before a model turn, survives reopen, fences duplicate operation IDs, settles known usage, and retains unknown or possibly committed charges. The coordinator uses it when both a ledger and a maximum-quote authority are supplied. Concurrent reservation, recovery, conflict, and quote-bound tests pass; the full solution passes 739 tests per framework (287 core, 223 compiler, 87 Baize, 142 Zhinu). SQLite dependencies restore with NuGet audit enabled. Strict prompt, total-token and cost admission remains closed: the configured Baize path has neither a stock coordinated turn executor nor a trusted maximum-charge quote, while replay-bound ledger identity is now integrated. No package was published.

Target repository: `Penghou.Fuwen`. Intended implementer: Sol. Planning baseline: commit `90d2fde73eaacd300aaefd6e347e2e53c49e1858`, plus the existing uncommitted V2 additions to `docs/roadmap.md`. Recheck HEAD and outstanding changes before starting; preserve the user's work.

Source: [27 September review](review-2026-09-27.md). Portable reproduction seed: [nine regression probes](review-evidence-2026-09-27/FuwenInferenceCoordinatorTests.Review.cs.txt). The probes were run only in an isolated copy: nine failures confirming seven defects. The unchanged solution built with zero warnings/errors and passed 693 tests per framework on .NET 8 and .NET 10. Those numbers describe the review baseline, not a future completion gate.

Related contracts: [complex inference plan](complex-activities-implementation-plan.md), [ADR 0010](decisions/0010-zhinu-owns-inference-operation-journal.md), [ADR 0011](decisions/0011-aggregate-inference-budgets-reserve-unknown-usage.md), [revision identity](decisions/0003-plan-revision-lineage-is-separate.md), [explanatory comparison](decisions/0004-plan-comparison-is-explanatory.md), [graduation requirements](graduation-requirements.md), and the [cross-project V2 specification](../../Penghou.Guihua/docs/evidence-driven-workflow-evolution-v2.md). Cross-project links assume sibling checkouts.

## 1. Assignment and completion rules

Make complex inference meet its existing advertised contract, implement the review's usability suggestions, and establish the minimum boundaries that would be expensive to retrofit for V2. This is a sequence of reviewable changes, not permission to build the whole V2 ecosystem at once.

Use these delivery classes:

- **Fix now:** correctness, authority, recovery, retention and truthful capabilities. Ship a corrective slice once the urgent FI-00–FI-04 invariants and matching FI-10 documentation pass. Complete FI-05 and FI-06 before calling the broader provider and operator experience complete.
- **Prepare now:** identity distinctions, explicit outcomes, evidence subjects, dependency categories, budget scope and serialization rules touched by those fixes. Complete the no-regret FI-09 checks for contracts actually being changed before public API stabilization; the comparative-execution fixture may follow its own dependency gate. Preparation includes small executable fixtures, not a V2 runtime.
- **Deliver next:** FI-07 workflow-visible failure handling and FI-08 fan-out composition. These are requested implementation work, with their own dependency gates; they need not delay an earlier truthful corrective preview.
- **Deliver later:** actual cross-project V2.1–V2.3 activation, experiments and historical preference behavior. Keep these unchecked until owning-project prerequisites exist.

For each task, record code/API changes, tests, contract/version decision, documentation, and commit/PR evidence. A declaration, enum, fake, or ADR alone does not satisfy a runtime acceptance gate. Do not turn a failing contract probe green by weakening its assertion without documenting why the original expectation was wrong.

Check applicable `AGENTS.md` files in every repository touched. Do not edit unrelated working-tree changes. Use the existing project structure and public API analyzer. Do not create a generic `IComplexActivityHandler`, add unrestricted scripts, enable model-driven writes, or add dependencies from core Fuwen to Guihua/Hongxian/Cangjie/Marang. Keep human-readable public diagnostics bounded and free of payload values.

## 2. Ownership and architecture decisions

| Concern | Owner and implementation direction |
| --- | --- |
| Authored semantics, type checking, exact descriptor pins, deterministic comparison | Fuwen core/compiler. No provider calls or live memory queries during compilation. |
| Durable claims, fencing, operation recovery, runtime generation and activation | Zhinu. Fuwen.Zhinu adapts these primitives; do not create a competing workflow store. |
| Provider transport, native tool messages, model identity and usage mapping | Baize, with Fuwen-specific mapping in Fuwen.Baize. |
| Resource grants, evaluator trust, protected bytes, credentials, pricing and ceilings | Host-owned ports/policies with exact admitted identities and runtime authorization. |
| Proposals, comparative strategy and advisory history | Future Guihua/host integration, using immutable snapshots. |
| Optional evidence delivery and projections | Hongxian/Siming adapters. They do not become execution authority. |

Keep the data-oriented immutable IR. Refactor the coordinator into a small set of cohesive components: invocation identity, effective contract/preflight, budget ledger, durable operation boundary, pure protocol transitions, typed validation, and evidence/payload persistence. Interfaces belong at external storage/provider/policy boundaries; deterministic internal helpers need not all become interfaces.

**Contract/version decision required in FI-00:** the current package uses one current IR after removing older branches, and consumer migration notes show real downstream adoption. Inspect actual persisted consumers. Do not silently change the interpretation of their stored bytes. Version changed operation-key, journal, evidence and runtime-contract formats where their meaning changes. Preserve supported readers or reject incompatible records explicitly; never rerun an old ambiguous operation merely because it lacks new metadata. Do not restore all historical IR branches or create migrations without an actual supported consumer. Package version, executable semantics, journal schema and evidence schema are separate concepts.

## 3. Traceability and delivery sequence

| Review item | Implementation | Required proof |
| --- | --- | --- |
| R1 colliding interaction/operation IDs | FI-01 | Different runs/inputs/semantics differ; recovery stays stable; restart/fork behavior explicit. |
| R2 final-turn budgets and missing reservations | FI-02, FI-03 | Before-call admission plus final settlement; unknown and exceeded usage never silently succeed. |
| R3 declared currency ignored | FI-02 | First and later operations obey currency/pricing contract. |
| R4 raw payload persistence | FI-03 | Ordinary journal/query/history contains references; protected content supports authorized recovery. |
| R5 wrong-executor preflight | FI-01 | Selected executor and effective runtime policy are checked before registration/provider work. |
| R6 retained-evidence limit unused | FI-02, FI-03 | Bound measured and enforced before durable writes. |
| R7 long valid call IDs fail | FI-01 | Fixed-size operation keys; full valid UTF-8 call-ID range. |
| Ambiguous calls, incomplete recovery evidence | FI-03, FI-06 | Every crash boundary reuses, reconciles or stops; query reports truthful disposition. |
| Missing profile/schema/grants in turn request | FI-01, FI-04 | Exact profile/output/tool/context contracts reach the actual executor. |
| Tool signature/output validation and fake scopes | FI-04 | Malformed/mistyped/unauthorized requests produce zero tool I/O. |
| Missing typed provider failures | FI-01, FI-04, FI-07 | Refusal, known-not-submitted, ambiguous and typed contract failure remain distinct. |
| Conversation/message/count limits disagree | FI-02, FI-04 | Per-message, cumulative, serialized and message-count boundaries compose. |
| Coordinator/ports cohesion; duplicated paths; production depends on test helpers | FI-01 through FI-05 | Shared contracts, explicit state, no unused executor requirement, production identities outside conformance helpers. |
| Real provider usefulness | FI-05 | Stock Baize turn adapter and provider-recorded fixtures; controlled live smoke separately. |
| Workflow failure handling | FI-07 | Authored bounded fallback/escalation with durable typed outcomes. |
| Inspection/resume/reference host | FI-06 | One documented credential-free end-to-end operator walkthrough. |
| Fan-out/composition | FI-08 | Item-scoped durable operations, bounded concurrency/accounting, focused recovery. |
| Documentation contradictions and release claims | FI-00, FI-10 | One evidence-backed capability table, all claims match executable tests. |
| V2 preparation and delayed features | FI-09 and section 5 | Contract fixtures now; activation/learning explicitly deferred. |

Suggested order:

```text
FI-00 -> FI-01 -> FI-02 -> FI-03 -> FI-04 -> FI-05 -> FI-06
                     FI-09 preparation accompanies these contract changes
                         FI-07 follows FI-04/FI-06
                         FI-08 follows FI-03/FI-04/FI-06 and Zhinu support
                         FI-10 verifies each release slice and final completion
```

Keep PRs small enough to demonstrate a single invariant. Split contract and implementation work only when the intermediate state rejects unsupported execution. No release may claim support based on a contract stub.

### Product delivery cuts

1. **Corrective preview:** FI-00–FI-04 close the seven reproduced defects and the ambiguous-operation/privacy boundary for every capability still advertised as supported. An unavailable external primitive is a tested admission rejection with a clear reason. A minimal deterministic public-consumer example shows compile, preflight, one safe run, one rejected run and inspection of the resulting failure. FI-10 updates capability claims for this slice. Do not wait for a live provider adapter, a full operator CLI or fan-out support to ship truthful safety corrections.
2. **Usable provider preview:** FI-05 supplies a stock Baize turn adapter and a controlled provider proof; FI-06 adds durable inspection and resume guidance. One non-test consumer can complete model -> read tool -> model -> typed result using only public packages, see actual spend/uncertainty, and understand what operator action is needed after an ambiguous outcome. Keep unsupported provider guarantees visible in preflight.
3. **Workflow authoring and composition:** FI-07 adds bounded failure handling; FI-08 adds item-scoped inference only after its Zhinu and shared-budget gates pass. A per-file or per-document scenario demonstrates value without moving planning decisions into the hidden protocol.

Track product outcomes alongside correctness: time and steps from a fresh package install to the first successful run; proportion of unsupported combinations rejected before paid work; whether an operator can identify the exact failing operation, spend quality and safe next action without raw payload access; and whether a consumer needs custom adapter code for the advertised read-tool path. Record a baseline with the reference consumer, then compare after the provider preview. These are acceptance evidence, not targets invented before a consumer exists.

## 4. Implementation work packages

### FI-00 — Establish regressions, containment and contract decisions

Primary files: existing coordinator/core/compiler/Baize tests, capability documentation, new corrective ADRs. No broad refactoring yet.

- [ ] Rebase the review against current source. Confirm each finding still applies; record resolved findings rather than reintroducing fixes.
- [ ] Port the nine seed cases to normal repository test fixtures. Keep them on the working implementation branch or land each with its fix; do not intentionally leave main's CI red. Give failures stable codes and assert zero downstream/provider work where appropriate, not only workflow status.
- [ ] Establish the small public-consumer path used for the corrective preview. Record its current failure and the exact advertised capability it exercises; avoid using accept-all test manifests as evidence of registration behavior.
- [ ] Add targeted cases for absent profile/schema, typed tool mismatch, constructor-bound composition, misleading recovery disposition, and an interrupted remotely accepted operation.
- [ ] Correct current capability claims immediately: completed-step reuse versus ambiguous reconciliation, stock Baize one-turn mapping versus a working turn executor, current IR identifier, fan-out restriction, raw payload retention and unimplemented evidence limits. Preserve historical completion records with corrective status; do not erase them.
- [ ] Record ADRs for invocation identity/recovery, budget settlement, protected persistence, selected-runtime admission, failure handling, and format evolution. Reuse/update ADR 0010/0011 where appropriate and identify superseded statements explicitly.
- [ ] Inventory Zhinu/Baize package support and actual downstream persisted data. Identify missing primitive APIs by exact capability, owning repository and acceptance test.

**Gate:** review findings have traceable tests; unsupported guarantees are rejected or honestly marked unsupported; no misleading completion claim remains in the affected surfaces. The baseline remains buildable.

### FI-01 — Bind identity, execution contracts and selected-runtime admission

Primary files: `ExecutionInvocation.cs`, `InferenceTurn.cs`, `InferenceReadTool.cs`, `InferencePreflight.cs`, `ExecutionPorts.cs`, `InferenceConformance.cs`, `FuwenZhinuWorkflowFactory.cs`, `FuwenZhinuSequentialInterpreter.cs`, coordinator identity construction.

- [ ] Define a detached admitted inference contract carrying exact profile descriptor, prompt source/digest, output type and resolved schema identities, admitted tool signatures/effects, context snapshot provenance, effective bounds, and runtime capability/policy identities. Pass a verified authority-context reference to the host where needed; never serialize process-bound admission receipts as portable authority.
- [ ] Distinguish definition fingerprint, lineage revision, workflow instance/run, logical execution generation, runtime item/iteration, node revision, retry attempt and lease/fence. Use actual runtime fields. Where semantic generation does not yet exist, preserve absence; do not alias lease generation or fabricate zero.
- [ ] Derive a logical invocation identity from the admitted execution/run namespace, runtime path, semantic node revision and canonical effective request. Include exact descriptor digests and input/context identities. Keep transient attempt number, lease owner, timestamps and later advisory evidence out of the logical operation key.
- [ ] Specify identity behavior for same invocation recovery, explicit restart, changed-input execution, new run, changed-plan fork, and authorized reuse. New work gets new identity; reconciling the same work retains its identity. A comparison suggesting equivalence is not authorization to reuse another run's operation.
- [ ] Hash internal operation kind/ordinal/provider call ID into a fixed-size, versioned key. Retain the original provider call ID separately. Move production key generation out of `InferenceTurnToolConformance`; conformance tests call the production contract.
- [ ] Require structured preflight for the actual selected turn executor. Remove fallback to an unrelated one-call executor. Build requirements from effective host/source limits, prompt form, output requirements, exact tools, context bounds and recovery guarantees. Refuse missing or unsupported required capabilities before definition registration/paid work.
- [ ] Bind behavior-affecting selected-runtime identity to registration: executor/protocol semantics, supported capability snapshot, tool-grant policy and budget/pricing semantics. Recheck drift before new external work. Rotate operational details such as credentials or a healthy endpoint handle under host policy without changing executable-plan identity; runtime authorization is checked again for each call. Reconciliation stays bound to the original provider operation; changed runtime policy cannot reroute an ambiguous call.
- [ ] Replace the growing positional execution-port constructor with validated configuration for the selected strategy. A coordinated-only host must not provide a dummy one-call executor. Keep intentional migration overloads only where real consumers need them.
- [ ] Extend the closed turn-result hierarchy with normalized failure/refusal, commitment status and recoverable handle/reference outcomes. Define which errors occurred before submission. Do not map every adapter exception to an unqualified provider failure or assume cancellation means no commitment.

**Tests/gate:** R1, R5 and R7 pass; different input/run/profile digest/output schema produce distinct effective request identities as applicable; retries and lease changes preserve logical identity; cross-tenant/host namespaces cannot collide; 200-byte and maximum valid multibyte IDs execute; conflicting operation-key reuse fails. An executor advertising unsupported recovery/limits cannot borrow another executor's approval. Two profiles with identical prompt text and different output schemas route correctly through one host.

### FI-02 — Enforce budgets before work, during work and on finalization

Primary files: protocol limits/preflight, coordinator budget code, normalized usage/cost contracts, adapter policies; durable settlement integrates in FI-03.

- [ ] Introduce one budget ledger abstraction with explicit reserve, settle, retain-uncertain, release-proven-unused and finalize operations. Persist reservations by operation identity, not worker attempt. A completed replay must not charge again.
- [ ] Compute source/host minimums once, preserving provenance, currency, units and pricing revision. Require finite effective bounds for supported dimensions; reject required dimensions the runtime cannot enforce. Do not replace absent bounds with arbitrary unlimited defaults.
- [ ] Define three distinct monetary policies: an enforceable hard ceiling, a conservative reservation against a trusted maximum charge, and an advisory/estimated budget that can report an overrun but cannot promise to prevent one. Pass remaining monetary allowance and pinned pricing policy to the executing adapter. Known prior spend below a ceiling is insufficient proof for the next call. Advertise a hard ceiling only when transport limits plus trusted pricing establish a real upper bound; otherwise reject strict-cap admission or require an explicitly advisory policy. Provider usage arriving after commitment cannot retroactively enforce a hard ceiling.
- [ ] Treat the existing source `cost` declaration according to its admitted current semantics while designing these modes. An advisory mode needs an explicit versioned source/host contract and clear explanation; do not silently reinterpret an old hard-looking `cost` plan as estimated monitoring.
- [ ] Account for tool/provider fees, adapter retries/fallbacks and repair calls where they incur bounded work. Tool count alone does not prove a paid tool fits a monetary budget. A read-only tool may still have cost.
- [ ] Reconcile every returned usage record, including a final candidate and failed/cancelled operations. Reject overrun before returning success and preserve the measured overrun as evidence; never claim rejecting output refunded the spend. Unknown final usage succeeds only if a valid retained conservative reservation proves the bound under the declared policy; otherwise return `BudgetUnknown`.
- [ ] Compare the first and every later cost currency with the admitted currency. Reject incompatible pricing revisions or mark them unknown according to explicit policy. Use checked arithmetic and bounded counters. A host-only cost ceiling also needs currency/unit identity.
- [ ] Define payload accounting: per-operation argument/result bytes versus cumulative argument/result bytes, retained transcript bytes, retained evidence bytes, message count, per-message bytes and serialized overhead. Version any semantic change to a currently named limit; do not silently reinterpret an old per-call limit as cumulative or vice versa.
- [ ] Enforce all byte/count bounds before storing/appending or constructing the next request. Reserve space for a minimal terminal status/reference, or reject a configuration too small to represent one. Explicitly cap nonessential summaries; never discard authoritative recovery identity/commitment facts to fit a presentation quota.
- [ ] Propagate linked per-call and activity deadlines before dispatch and during calls. Use the persisted activity deadline across restarts. A timeout does not release a possibly committed reservation. Cancellation lag is part of accounting.
- [ ] Keep budget-account identity separate from a worker attempt or run so later candidate/replan work cannot silently reset allowance. Expose a host-supplied parent-account reference only when a concrete host producer and consumer can be demonstrated. Implement and prove the leaf ledger now; concurrent parent-account enforcement is a gate for FI-08 and V2 comparative execution. No distributed transaction is assumed.

**Tests/gate:** R2, R3 and R6 pass, including exact-bound success, one-over-bound failure, unknown final usage without a reservation, currency mismatch on the first call, changed pricing, overflow, crash between reserve/submit/settle, duplicate settlement, timeout and unknown commitment. Test multibyte strings and JSON escaping overhead. Strict and advisory policies are visibly different in admission and evidence. A cost cap cannot disappear from the actual executor request. Two concurrent child allocations against a parent account are required only when that supported account implementation is delivered for FI-08; no leaf-only test may claim shared enforcement.

### FI-03 — Build the durable protected operation boundary

Primary files: coordinator persistence, Fuwen.Zhinu adapter, protected-payload/evidence contracts; use existing Zhinu primitives where they meet the required guarantees.

- [ ] Model each operation through explicit durable states equivalent to prepared/reserved, claimed, submitted-or-possibly-submitted, completed, known-failed and ambiguous. The owning runtime fences each transition. Persist the request digest, reservation identity, provider binding and receipt/handle reference needed for recovery.
- [ ] Select one proven recovery mode per binding: provider-enforced idempotent execution, durable receipt/handle reconciliation, or conservative stop for operator action. Merely sending the same string twice is not proof of deduplication. Define missing-receipt semantics; timeout or “not found yet” is not automatically proof the request never committed.
- [ ] Reuse completed results. Reconcile in-flight ambiguous work before resubmitting. If reconciliation is impossible, stop without a second external submission. Keep cancellation/lease-loss evidence distinct from remote cancellation confirmation.
- [ ] Verify loaded state against format version, invocation, effective request, descriptor/runtime contract and operation sequence. Reject corrupt, copied or incompatible journal entries before I/O. Never deserialize an arbitrary string phase and assume its associated fields are consistent.
- [ ] Add a host-controlled protected payload store/resolver for prompt/context values, tool arguments/results, model candidates and sensitive continuation data. References bind scope, identity, digest, length, retention policy and storage identity. Access and content verification occur on read. Digests are integrity identifiers, not anonymization. Define the storage/retention capability a host must supply for a private-history claim; when it is absent, preflight must not advertise that privacy guarantee.
- [ ] Journal only required bounded summaries and protected references. Audit run input, context/activity outputs, final workflow output, initial inference loop state, step inputs/results, continuation state, failure messages, observer output, snapshots and ordinary query/export surfaces. The immediate defect concerns inference tool content, but a product-wide private-history claim requires every ingress/export path to meet the same policy. Define final workflow-output retention separately so protocol cleanup does not break intentional output delivery.
- [ ] Make payload publication idempotent and recoverable: payload written before journal reference may become an orphan; journal reference is valid only after protected storage confirms it. Recover via reconciliation/garbage collection rather than pretending there is a transaction across stores.
- [ ] Preserve active recovery-required payloads according to policy. Deleted/expired/unavailable content yields a typed unavailable-evidence/recovery outcome; do not reissue provider work to reconstruct it silently. Reconcile a possibly committed operation before permitting disposal of its only recovery handle.
- [ ] Store authoritative terminal evidence even on failure, timeout and cancellation when the runtime still has authority. If the lease is lost, the new owner records from durable state; the stale worker must not bypass fencing to report success. The optional sink remains non-authoritative and cannot alter execution truth.

**Crash matrix:** before reservation; after reservation; before submission; after remote acceptance but before handle persistence; after handle persistence; after remote completion but before result persistence; after protected payload write; after journal write but before settlement; after final validation; before node completion; during cancellation and fence loss.

**Gate:** R4 passes using semantic store/query assertions plus private-marker scanning. Authorized replay retrieves protected content and performs no duplicate completed calls. Every crash yields reuse, successful reconciliation, proven-safe resubmission, or an explicit ambiguous stop. No missing state causes speculative resubmission. If the installed Zhinu package cannot provide a required primitive, implement it in the owning project or retain an explicit unsupported gate; do not hide a side journal in an in-memory dictionary.

### FI-04 — Separate protocol transitions and enforce typed tool boundaries

Primary files: coordinator, `InferenceTurn.cs`, `InferenceReadTool.cs`, typed runtime validator, compiler callable contracts, prompt/context preparation.

- [ ] Extract a pure transition component with explicit model-pending, tool-pending, validation and terminal states; invalid combinations must fail before effects. Keep serialized state DTOs separate from validated runtime state.
- [ ] First define the exact **model-facing tool contract**: provider-visible name, parameter names/types/schema, result type/schema, descriptor digest and validator identity. Map it to the trusted `CallableContract` and actual host executor binding during admission. The current callable contract alone does not prove that an arbitrary provider JSON object matches the advertised tool: existing coordinator fixtures admit a `request: string` callable while the model proposes `{ "q": 1 }`. Reject a binding whose model-facing and host-facing contracts cannot be mapped exactly.
- [ ] Validate proposed arguments against that admitted model-facing contract, including required/extra fields and nested types supported by Fuwen. Validate tool results before persistence or model visibility. Reuse Fuwen's type machinery and Baize's validator extension where appropriate; do not implement a second arbitrary JSON Schema dialect. Test a genuinely typed contract, not an accept-all fixture with a string request and arbitrary JSON proposal.
- [ ] Separate representation repair, declared-type validation and domain acceptance. Unsupported schema constraints reject preflight or receive explicit unsupported diagnostics. Nuwa repair success is not proof of complete schema validity.
- [ ] Replace descriptor-derived pseudo-scopes with exact bounded grant references and runtime resource authorization. Verify grants still permit the requested resource when the tool is called. A descriptor identifies behavior; it does not identify every file/database/tenant the tool may read.
- [ ] Validate all proposed tools before executing a batch. Enforce exact allowlist, supported effects, unique provider names/IDs, maximum proposals and remaining allowance. If no tool budget remains, expose an explicit no-tools/finalization request; do not use null to accidentally mean unlimited.
- [ ] Normalize bound/type violations into stable failures before constructor exceptions escape. Test the 64-message and 65,536-byte message contracts against larger declared result/transcript allowances. If a supported adapter can consume large content only by reference, make that delivery contract explicit.
- [ ] Unify prompt/context construction and final validation across one-turn and coordinated paths. Support registered prompts through an exact bound renderer when available; otherwise reject that combination honestly in preflight. Preserve native tool-call structure in normalized conversation rather than relying on ad hoc assistant JSON strings that a provider adapter must reverse-engineer.
- [ ] Retain bounded representation repair with operation identity, counters and evidence; paid repair attempts use the same aggregate ledger. Semantic rejection returns to authored workflow logic.
- [ ] Extract deterministic fakes/conformance utilities from production responsibilities. Move them to a testing package only with a consumer migration decision; do not require a package split merely to clean up one helper.

**Gate:** typed-invalid arguments and unauthorized resource scopes cause zero tool I/O; invalid results never reach the model; duplicate/undeclared/write-capable tools fail consistently; normalized failures distinguish refusal, schema mismatch, pre-submission failure, cancellation and ambiguous commitment. Pure state-transition tests cover invariants, while end-to-end tests prove integration. Avoid implementation-mirroring tests for mechanical file moves.

### FI-05 — Deliver a usable Baize turn adapter

Primary files: `Penghou.Fuwen.Baize`, routing/binding policies, Baize adapter tests and isolated consumers.

- [ ] Implement the actual `IInferenceTurnExecutor` strategy and its structured preflight, using the admitted profile/output/tool/context contract. `BaizeOneTurnMapper` alone does not meet this task.
- [ ] Before freezing FI-01/FI-04 public contracts, run a bounded provider-shape spike using recorded redacted responses and one concrete endpoint's documented capabilities. Feed its real tool-message, usage and refusal shapes back into the request/result design. This does not claim live-provider conformance. Keep the later opt-in live smoke test as the separate proof of actual transport behavior.
- [ ] Map native assistant tool calls and corresponding tool results without inventing or dropping call identities. Cover mixed text/tool responses, refusal, malformed requests, duplicate names, finish reasons, truncated output and unavailable usage. Bound opaque continuation data; retain it as protected provider data when required, not as ordinary explanation text.
- [ ] Bind requests to exact provider/endpoint/model capabilities and return actual resolved identity, usage quality, currency/pricing provenance, handle/idempotency guarantees and timing. Keep no hidden retry loop that escapes the ledger; any retry or fallback must be accounted for and legal under the original commitment state.
- [ ] Honor strict supported allowances and deadlines. Where the provider does not support idempotency/reconciliation or provable cost ceilings, report that capability accurately and use the explicit stop/rejection path. Do not advertise reconcilable recovery merely because the workflow can replay completed steps.
- [ ] Preserve media/generation adapters where their transport lifecycle differs, but share identity, authority, budgets and failure/evidence contracts. A one-turn structured call is a bounded instance of these semantics, not an unrelated legacy authority path.
- [ ] Add deterministic transport fixtures from redacted provider shapes and an opt-in live smoke test against at least one provider when credentials/budget are supplied through the normal host configuration. Default CI remains credential-free. Record unavailable live verification as pending, not passed.
- [ ] Run both package-consumer projects using packed packages. Add a nontrivial two-profile/schema consumer, not only accept-all fake manifests.

**Gate:** a stock Fuwen.Baize configuration performs model -> tool -> model -> typed output without a consumer-written protocol adapter; registered/workflow-owned prompt support is explicitly tested; real-provider limitations appear in preflight and operator evidence. A live-provider graduation gate stays open until actually exercised.

### FI-06 — Durable inspection, operator actions and a reference host

Primary files: evidence contracts/renderers, Fuwen.Zhinu query/administration adapters, examples or a small CLI project, optional evidence sinks.

- [ ] Expose bounded read APIs over authoritative durable state: invocation/operation identity, current phase, requested/effective bounds, reserved/settled/unknown usage, exact admitted and actual provider bindings, tool outcomes, validation, failure and recovery disposition. Use stable cursor/checkpoint semantics, not an unbounded log dump.
- [ ] Derive `Fresh`, `Replayed`, `Reconciled`, `Reused` and `Ambiguous` from actual journal behavior. Define whether a query describes original execution or the current recovery observation; do not overwrite an original success receipt to make a later replay look fresh.
- [ ] Separate safe inspection from authorized protected-payload retrieval. Queries and projection rebuilds must perform no model/tool work. Show missing/expired data and projection lag explicitly.
- [ ] Offer explicit resume/reconcile/restart commands bound to exact run/revision/current state, with idempotent command IDs and stale-state checks. Resume recovers the pinned definition; restart creates allowed new work; inspect never executes. Operators cannot transform an unverified ambiguous outcome into a proven success by changing a flag.
- [ ] Keep sink failures harmless to execution. If reliable external mirroring is needed, use an owning-store outbox or durable cursor with idempotent delivery; expose lag. Do not add a cross-store transaction or make Hongxian a hard dependency.
- [ ] Ship one credential-free walkthrough: author/compile -> explain -> preflight -> admit -> run -> interrupt -> resume/reconcile -> inspect -> typed output -> explicit separate proposed-effect activity. Include a budget failure and an unsupported-runtime example with actionable diagnostics.
- [ ] Use the same public package surfaces as a consumer. Provide one-command setup/run instructions; avoid making users extract a sample host from internal test helpers. Human and JSON reports must derive from the same bounded model.

**Gate:** the walkthrough runs from a clean package consumer and accurately explains recovery and expenditure; sink unavailable still leaves authoritative evidence queryable; inspection does not execute; duplicate admin commands return the same result and stale commands fail without new work.

### FI-07 — Make bounded failure handling authorable

Dependency: FI-01–FI-04 and a minimal safe failure/operation read path from FI-06. The full operator CLI is not a prerequisite for authored failure handling. This is workflow functionality, not V2 automatic replanning.

- [ ] Design typed success/failure binding semantics before choosing syntax. Consider a closed outcome value or an explicit failure region; select the smallest representation that preserves current success-only behavior and bounded control flow.
- [ ] Keep execution failure, domain evaluation, acceptance decision and revision disposition distinct. Catching a failure does not make a failed external operation successful; rejecting an artifact does not rewrite a completed execution as failed.
- [ ] Let source handle a declared safe subset of normalized failures with explicit fallback, checkpoint/wait or terminal escalation. Fencing/cancellation/unknown commitment are not blanket-retryable; unresolved operations cannot be hidden by a successful fallback. Any new work uses admitted bounds/grants and explicit retry policy.
- [ ] Support a reusable example: inference exhausts a bound -> produces an inspectable failure -> a bounded authorized fallback or human gate runs -> the workflow returns a typed outcome. Do not implicitly retry indefinitely or reset cumulative allowances.
- [ ] Update IR, parser, compiler, formatter, canonical identity, source maps, diagnostic codes, explanations, comparison, execution and docs together. Preserve lexical scopes and typed merges; validate all branches before activation.
- [ ] Add an explicit completed-artifact -> evaluator activity -> acceptance decision example using existing typed activities. Missing evaluation, timeout or evaluator fault produces pending/indeterminate handling, not automatic acceptance or rejection. A changed rubric can request re-evaluation without pretending the producer failed.

**Gate:** compile/format/round-trip tests and durable interrupted-handler tests pass; existing workflows retain behavior; handlers cannot broaden capabilities or silently replay uncertain effects. Outcome values can be used by future V2 integration without inventing runtime status enums.

### FI-08 — Item-scoped complex inference and bounded composition

Dependency: FI-03/04/06 plus an actual Zhinu item-scoped durable boundary and shared budget enforcement. Keep the current admission rejection until those exist.

- [ ] Identify the exact missing Zhinu primitive for nested durable operations under a fan-out item. Implement it in Zhinu if necessary; do not emulate it with a root loop, positional item index, process-local map or unjournaled inner calls.
- [ ] Bind every operation to the stable canonical item key and structural/runtime path. Preserve key identity through reordering. Support focused restart without recomputing unaffected items.
- [ ] Enforce per-item and shared host/parent allowances atomically across in-flight items. Reserve before scheduling calls, retain uncertain spend on cancellation, and include evaluation/repair work when present. A per-item ceiling is not a workflow-wide ceiling.
- [ ] Prove concurrency, cancellation, recovery and aggregate output behavior using two items that deliberately return identical provider call IDs. Crash one item, reorder source input and resume without crossing journals or returning another item's output.
- [ ] Demonstrate per-file review or per-document research with typed results and a sequential fallback for hosts without supported item-scoped durability. The fallback must be explicit and preserve identities/bounds, not silently change requested semantics.
- [ ] Demonstrate reusable review/evaluate/rework composition through existing typed constructs and sample builders. Document a child-workflow identity/admission/lineage boundary for later reuse; do not add imports, subworkflow syntax or a generic activity plugin system until consumers show the missing semantic need.

**Gate:** the runtime actually supports coordinated inference inside fan-out, preflight advertises it truthfully, shared bounds cannot be exceeded by simultaneous reservations, and crash/focused-restart tests pass. If the required Zhinu change is deferred, record FI-08 as blocked/deferred; do not mark its parent complete.

### FI-09 — V2 preparation fixtures and contract review

Dependency: integrated incrementally with FI-01–FI-06; no dependency on a functioning memory system or cross-project cutover. Section 5 defines what to establish now.

- [ ] Add a small credential-free conformance corpus of versioned, bounded artifact references and evidence envelopes using existing schemas/ports. Prefer fixtures and adapters over speculative public types. Each newly public field must have a current producer, reader or concrete fixture that proves why it is needed.
- [ ] When FI-07 introduces authored outcomes, prove a completed execution, rejected evaluation, later acceptance decision and superseding proposal coexist without rewriting execution history. This fixture is not a gate for the earlier corrective preview.
- [ ] Prove an evidence-only proposal-envelope change preserves executable identity while changing lineage/provenance identity. Conversely, changing an executable evaluator/rubric binding or selection rule changes executable identity when it changes behavior.
- [ ] Preserve what comparison already knows about producer/input/control changes and expose incomplete dependency coverage explicitly when changing its contract. When FI-07 supplies real evaluation semantics, extend explanatory comparison for validation-only changes. Fixture: `A -> B -> C -> D` with independent `B -> E`; changing C affects D, not automatically E; changing a rubric requests revalidation rather than claiming new production. Fuwen still emits no reuse authorization.
- [ ] Record the exact candidate/decision identity and bounded-accounting requirements for a future two-candidate fixture. Build that fixture when FI-08 supplies supported item isolation and shared accounting, or at the V2.2 start gate. Do not add speculative public candidate types or block the corrective preview on a future experiment shape.
- [ ] Bind proposal provenance to a supplied immutable retrieval checkpoint/scope/policy/planner context. Compile twice with the same supplied snapshot and obtain the same executable definition; live external history must not affect compilation. Do not implement a live recall client here.
- [ ] Test missing/old/unknown provenance as explicitly incomplete. It remains readable where supported, but cannot assert equivalence, authorize reuse, grant capabilities or become a zero-cost measurement.
- [ ] Record required owning-project dependencies and mark actual V2 activation/selection/recall delivery unchecked. Link the current V2 roadmap rather than duplicating a competing authority.

**Gate:** the applicable no-regret preparation checks in section 5 pass for contracts changed in the corrective slice; later evaluation/candidate fixtures are assigned to FI-07/FI-08 or V2 with explicit dependencies. No new mandatory core dependency, planner, evaluator policy or workflow activation engine is introduced.

### FI-10 — Documentation, release and completion evidence

- [ ] Maintain one capability matrix linked to executable conformance cases. Distinguish compile-time support, adapter support, configured runtime support, deterministic proof and live-provider proof.
- [ ] Reconcile README IR claims, authoring contract, complex-activities overview/plan, roadmap status, consumer migration, execution ports/evidence docs, threat model and release checklist. Preserve the user's V2 additions. Annotate the old CI-0–CI-7 claims with this corrective plan and actual remaining gates.
- [ ] Document per-call/cumulative limits, requested/effective ceilings, reservation versus settled spend, uncertainty, deadline/cancellation semantics, retention, recovery modes, typed failure handling and exact unsupported combinations.
- [ ] Update public API baselines and pack validation intentionally. Do not equate empty `Shipped` baselines with permission to silently reinterpret persisted histories. Include consumer recipes for changed turn requests/results/port configuration and rejected old journal formats.
- [ ] Run the appropriate targeted checks per change; then perform one full completion pass: Release build, format verification, .NET 8/.NET 10 solution tests, canonical JSON vectors, package validation and both isolated consumer projects. Compile every documentation example. Record live provider checks separately.
- [ ] Preserve graduation gates that this implementation cannot itself satisfy: independent human security review, actual downstream pilot/adoption and live conformance evidence. No fake, document or AI review substitutes for those gates.
- [ ] Record evidence in the completion table below. Final handoff states fixed, prepared, delivered-next, deferred and blocked items separately, including the specific external dependency for any remaining block.

## 5. V2 decisions that must not be painted into a corner

The cross-project V2 specification remains deferred. Do not implement unrelated V2 behavior merely to fill optional fields. For the corrective slice, prove VF-01, VF-03, VF-04 and VF-07 to the extent their current producers/consumers exist; preserve unknown dependency coverage under VF-05. VF-02's evaluation fixture follows FI-07, VF-06's shared parent-account proof follows FI-08, and VF-08's two-candidate fixture follows FI-08 or V2.2. VF-09 is an immutable-reference design check until a real planning-context producer exists. VF-10 follows the inspection/admin work. A deferred fixture is not a failed corrective-slice gate.

| Seam | Establish at its stated dependency gate | Expensive mistake prevented | Defer |
| --- | --- | --- | --- |
| VF-01 Identity namespaces | FI-01 distinguishes definition, lineage, run, semantic generation, node generation, retry, fence, runtime item and provider operation; unknown values remain absent. Recovery/new-work matrix is executable. | Collisions, retry double billing, treating lease renewal as a plan change, cross-candidate reuse. | Workflow-instance cutover implementation and automatic activation. |
| VF-02 Evaluation subjects/outcomes | FI-07/09 fixtures reference exact artifact/input digest, evaluator/version, rubric/version and explicit pending/inconclusive/fault outcomes separately from runtime status. | A single success/failure enum that loses rejection, disagreement and supersession history. | Model quality policies, universal scoring, automatic repair selection. |
| VF-03 Evidence envelope | FI-03/06 evidence has explicit schema/producer identity, stable evidence/operation ID, subject, source sequence/checkpoint, occurred/recorded times where available, and bounded correlation/causation references. Duplicate append is idempotent; conflicting reuse fails. | Unattributable history and non-idempotent external projections. | A giant shared evidence package or mandatory Hongxian dependency. |
| VF-04 Semantics versus provenance | FI-09 pins proposal evidence separately from executable IR. Runtime-affecting evaluator/rubric/policy selections enter the correct admitted/executable contract. Demonstrate both change classes. | Either every new observation invalidates execution identity, or behavior-changing criteria evade identity. | Broad history migration and live evidence lookup during compilation. |
| VF-05 Dependency categories | FI-09 comparison distinguishes data, control, validation and effect-relevant dependencies where known; incomplete coverage is explicit and conservatively handled. | Forced regeneration for rubric-only changes, or unsafe reuse based on a simplistic graph diff. | Runtime reuse authority, cutover and automatic preservation decisions. |
| VF-06 Budget lifetime and lineage | FI-02 leaf ledger keeps immutable reservation/settlement IDs and attempted/failed/cancelled/repair work attributable. Introduce a host-issued parent account only with a concrete producer/consumer; FI-08 supplies concurrent shared-account proof. | Resetting aggregate budget on restart, candidate fork, new run or replan. | A new distributed financial ledger or a claim of shared enforcement without runtime support. |
| VF-07 Protected content and availability | FI-03 refs carry scope, digest, length and retention/access policy; query shows unavailable/expired data. Missing content blocks unsupported recovery. | Permanent raw-prompt retention and false reproducibility after deletion. | Indefinite storage, implicit artifact dereference and global content visibility. |
| VF-08 Candidate and decision identity | FI-09 records the namespace and decision-evidence requirements without public candidate types. A two-candidate fixture follows FI-08 isolation/shared accounting or V2.2; it then distinguishes experiment candidates, provider candidates, attempts, artifacts and decisions, including selected/none/inconclusive data. | Confusing one model response candidate with a workflow alternative, or recording only the winner. | Real candidate scheduling, statistical claims and atomic winner selection until Zhinu/host gates exist. |
| VF-09 Pinned planning context | FI-09 uses supplied bounded snapshot references with scope, retrieval checkpoint/policy, planner/evaluator versions and supporting/contradicting references. | A hidden live-memory dependency that changes an admitted run during recovery. | Recall implementation, reputation routing, knowledge promotion and learning. |
| VF-10 Fenced administration and extensibility | FI-06 admin operations name exact targets and expected state, with idempotent command IDs. Unknown required schema/capability versions reject new execution. Pure inspection stays separate. | “Replay latest” semantics, stale commands activating different work, silent acceptance of unknown required fields. | Fuwen-owned cutover; blanket compatibility frameworks for hypothetical consumers. |

**Fingerprint rule:** runtime data values and request provenance belong in invocation identity/evidence; declaration changes that alter workflow behavior belong in executable identity; explanatory lineage changes belong in the revision envelope. Admission/runtime identity binds host-selected capabilities, pricing and implementation semantics. Do not put timestamps, leases or unrestricted historical evidence in executable-plan identity. Do not treat any fingerprint as an authorization token.

**Schema evolution rule:** distinguish optional explanatory fields from required executable/recovery semantics. Supported older evidence is readable as incomplete; unknown required execution semantics fail before work. Correlation/reference extension must be bounded and typed, not an unbounded `Dictionary<string, object>` escape hatch.

## 6. Future V2 delivery, explicitly separate from preparation

| Stage | Prerequisites | Actual acceptance gate | What this plan supplies now |
| --- | --- | --- | --- |
| V2.1 outcome-aware repair | V1 gates; host evaluator/acceptance contracts; Zhinu authoritative single-owner transition/recovery; Guihua exact-base proposals | Completed artifact is rejected; one explicitly authorized replacement activates; true dependents rerun; compatible independent branch is retained; late old-generation results cannot progress the superseded plan. | Exact identities, typed evidence/outcomes, explanatory dependency distinctions, immutable proposal provenance, safe inspection. |
| V2.2 comparative execution | V2.1; supported item-scoped isolation; aggregate reservations across candidates/evaluators; durable decision ownership | Two bounded candidates run; frozen policy selects once or yields no winner; losing/partial evidence and all cost remain; recovery does not repeat uncertain external work or select again. | Candidate identity requirements and provider evidence now; FI-08 infrastructure and a two-candidate compile/contract fixture only after isolation and shared accounting are supported. |
| V2.3 evidence-informed proposals | V2.2 evidence plus Hongxian bounded recall/projection and host compatibility/freshness policies | A later compatible case uses an immutable preference snapshot; wrong scope, changed versions, stale/conflicting/missing evidence use the declared fallback. | Pinned planning-context and availability contracts; no live memory in compiler/executor. |

No new `experiment`/`select` syntax before two consumers demonstrate a necessary semantic gap. No automatic model/tool writes, live irreversible experiments, universal reputation score, topology optimizer, knowledge-promotion service or reinforcement-learning framework in this work. Reusable workflow definitions may be explored through samples and exact-bound host composition before adding imports/subworkflow language features.

## 7. Cross-repository dependency tickets

Create concrete owning-project work only when source inspection proves it is missing. Each ticket must include the required API behavior, version/packaging dependency and the Fuwen test it unblocks. This document does not assert that a roadmap item is implemented.

| Owner | Potential required work | Until available |
| --- | --- | --- |
| Zhinu | Fenced/idempotent administration and item-scoped nested durable work; see [concrete dependency tickets](zhinu-inference-dependencies.md). Existing IWorkflowReader already supplies bounded run/step/event queries. | Conservative ambiguous stop; unsupported fan-out/strict shared limits rejected. No process-local substitute. |
| Baize | Native turn tool-call/continuation representation, exact capability manifests, provider usage/commitment metadata and required tool schema validation | Support only proven provider shapes/capabilities; retain explicit unsupported diagnostics. |
| Host/artifact provider | Protected payload storage/read/retention, exact resource grants, trusted pricing and parent budget account | Reject execution needing unavailable policies; deterministic implementations support tests/sample only. |
| Guihua/Guyabano/Marang | Real consumer scenarios and migration to new requests/configuration; domain evaluator/acceptance ownership | Package-consumer tests plus documented pending pilot; no product types in core. |
| Hongxian/Siming/Cangjie/Hetu | Future bounded evidence delivery/recall/context mappings | Opaque exact references and optional adapters; no mandatory dependency or invented authority. |

## 8. Sol execution checklist and handoff

Start with FI-00 and the nine repro cases, then FI-01's identity/preflight/key fixes. Prefer a small corrective preview that accurately rejects unsupported guarantees over waiting for FI-07/FI-08. Continue through the requested delivery packages; report an external prerequisite when it actually blocks a package and complete independent work in the meantime.

Every completed package must have a concrete behavior claim and evidence. Tests should exercise the contract boundary: paid/tool call count, stable/different identity, actual stored payload, exact usage/reservation state, authority rejection, and recovery disposition. Acceptance tests must not use an accept-all manifest to prove that unsupported production capabilities are rejected.

Use representative full paths across sequential, repeat and fan-out when supported, plus adversarial crash points. Keep recorded provider fixtures and default CI deterministic. Run broad verification when the integrated change warrants it, not after every mechanical edit. Do not publish packages, activate workflows, or consume live-provider budget merely because the plan mentions those gates; those remain explicit release/test actions under the project's normal authorization.

| Work package | Status | Commit/PR | Contract/API decision | Verification and remaining limitation |
| --- | --- | --- | --- | --- |
| FI-00 baseline/containment | Partial | `14b9c54` | Review and portable regression seed recorded | Consumer and additional contract probes open. |
| FI-01 identity/admission/contracts | Partial | `14b9c54` | Run/request identity and selected-executor preflight | Migration and remaining admission gates open. |
| FI-02 budgets/bounds | Partial | `14b9c54` plus current work | Final settlement, overflow handling, evidence cap, hard completion-token capability, currency-bound host cost ceilings, revision consistency, remaining-cost requests, optional SQLite leaf reservations and quote/settlement contracts | Prompt, total-token and cost strict admission remains closed: the configured Baize endpoint has no trusted maximum quote; the stock turn executor is present and the leaf ledger identity is bound to replay. |
| FI-03 protected durable recovery | Partial | `14b9c54` | Host protected store for successful tool results and verified replay | Other sensitive paths and crash matrix open. |
| FI-04 protocol/type validation | Partial | `14b9c54` plus current work | Zero-tool finalization, whole-batch allowance rejection, unique provider-visible names, admitted callable signatures, typed arguments/results, model-facing schemas and native tool history | Resource grants, pure transitions and broader failure distinctions open. |
| FI-05 Baize integration | Partial | Current work | Stock single-turn Baize executor, exact configured profile/tool schemas, native call identities, typed failure, deterministic transport tests and public-package model → tool → model review consumer | Provider continuation/provenance fields, multi-profile consumer, live-provider proof open. |
| FI-06 inspection/reference host | Partial | Current work | Review package consumer renders human/JSON preflight and post-run evidence, including a failed run; stock Baize proof queries bounded Zhinu run/step/event state without more provider work | Durable query/admin commands and standalone walkthrough open. |
| FI-07 authored failure handling | Not started | — | — | — |
| FI-08 fan-out/composition | Deferred on Zhinu | — | Exact missing nested durable item scope and shared parent reservation are specified in [dependency tickets](zhinu-inference-dependencies.md) | Current fan-out callback cannot journal model/tool operations under a stable item key; admission remains closed. |
| FI-09 V2 preparation | Partial | Existing revision contracts | Evidence-only lineage reference changes preserve executable fingerprint in PlanRevisionDocumentTests | Evaluation outcomes, pinned planning context, dependency categories and two-candidate accounting await their stated FI-07/FI-08 gates. |
| FI-10 documentation/release validation | Partial | Current work | README, capability, migration, scenario and release claims reconciled for this slice | Release solution: 758 tests per framework; local pack; Review 3/3 and Marang 7/7 per framework from packages; format and canonical vector pass. Roadmap remains user-edited; live provider, independent review and downstream pilot gates open. |

Completion of the corrective slice, the requested next features, V2 preparation, and actual V2 delivery are four separate claims. Keep them separate in the final implementation report.
