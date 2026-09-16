# Fuwen prep plan for the Guyabano migration

Status: **in progress — Stages 0–4 complete, integration pending**. Last updated: **2026-09-16**.

> **Handover note (2026-09-15):** Guyabano pilot `a1161ec` is on `main`
> and proves the sequential spine + one programmatic IR v4 fan-out shape.
> Fuwen `0.1.0-preview.2` (`b7006b8`) is tagged and pushed; NuGet publish
> and the Guyabano `ProjectReference -> PackageReference` switch remain as
> Stage 0 checklist items below.

## Goal

Make Fuwen able to author every phase of Guyabano's `CodeGenerationWorkflow`
as `.fuwen` source, in an order where each stage unlocks one more Guyabano
phase as authored text instead of programmatic IR. The consumer evidence is
the isolated pilot in `Guyabano/docs/fuwen-pilot.md`, which currently proves
the sequential spine plus one programmatic fan-out shape.

Non-goal: replacing any Guyabano production path. Each stage extends the
pilot; old-path removal follows Delivery F exit criteria (behavioral parity
plus a focused-restart dogfood run) and is tracked in Guyabano, not here.

## Sequencing principle

Order by Guyabano unlock value per unit of IR risk:

1. Fan-out DSL surface first — the IR (v4) already exists, so this is
   grammar + parser + compiler only, no new execution semantics.
2. Value-producing conditionals second — required before loops are useful
   (loop exit/repair decisions need merged values).
3. Bounded loops third — subsumes the unrolled review/build cycles.
4. Interaction gates last — depends on Zhinu signal semantics and host UX
   that Guyabano itself has not finalized (product-level input
   request/wait/cancel/timeout/resume policy is still open per its roadmap).

Every new execution semantic gets a new IR / compiler-semantics /
fingerprint version triple. Historical plans keep verifying under their
original rules (`WorkflowPlanValidator.cs:47-62`,
`WorkflowPlanIdentity.cs:51-54`). Packaging-only changes never touch the
triple.

## Stage 0 — Publish `0.1.0-preview.2`

Why first: Guyabano's pilot consumes Fuwen through sibling
`ProjectReference`s because `preview.2` is not on NuGet.org. The
release checklist explicitly requires that downstream projects use public
packages (`release-checklist.md:16,28`).

- [x] **Code changes for `preview.2` — done 2026-09-15:** `Penghou.Nuwa`
  `0.6.2 -> 1.0.0` in `Penghou.Fuwen.Baize` (`src/Penghou.Fuwen.Baize/
  Penghou.Fuwen.Baize.csproj:17`), `Microsoft.SourceLink.GitHub` `8.0.0 ->
  10.0.401` for `CVE-2024-...` (`GHSA-23fw-v26w-5fgq`), `Version`
  `0.1.0-preview.1 -> 2` (`Directory.Build.props:7`), `CHANGELOG.md`
  entry. Verified `build 0W/0E`, `330` tests (`660` runs) `pack`
  `8` artifacts.
- [x] **Push to `origin/main`** — `b7006b8` (Fuwen) and `a1161ec`
  (Guyabano pilot) pushed 2026-09-15.
- [x] **Abandoned Fuwen->Qingniao integration reverted** — the stray
  `PackageReference Penghou.Fuwen` in `Penghou.Qingniao` violated
  `ADR 0017`; reverted, only the `SourceLink` fix `8.0.0 -> 10.0.401`
  (`d75bdc1`) was kept and pushed.
- [x] `dotnet format --verify` — clean 2026-09-16.
- [x] Golden vector tests for v4–v7 canonical bytes/fingerprints — done 2026-09-16.
- [ ] Tag `v0.1.0-preview.2`, publish with provenance; verify indexing.
- [ ] In Guyabano, replace the three sibling `ProjectReference`s with
  `PackageReference 0.1.0-preview.2` and keep the pilot green.

Exit: clean restore of Guyabano from NuGet.org alone; no sibling checkout
required.

## Stage 1 — Fan-out DSL surface (Milestone 8, grammar only) — **in progress (grammar + parser done 2026-09-15)**

Unlocks: decomposition wave (`decomposition/{ver}/{parent}`) and generation
wave (`generation/{parent}/{leaf}`) as authored source.

**Already done (pilot, 2026-09-15):** IR v4 `FanOutNode`
(`WorkflowPlan.cs:116`) exists with `MaximumItems`/`MaximumConcurrency`
validation; `CodegenFuwenPilot.BuildFanOutPlan()` + Zhinu adapter
per-item `StepAsync` mapping works and is proven by
`FuwenPilotParityTests.Fanout_pilot_preserves_source_order…` (single-item
restart preserves siblings, matching Guyabano `Task.WhenAll` waves).
Guyabano pilot doc `docs/fuwen-pilot.md` records the phase mapping and the
four tracked gaps (M8/M9/M10).

IR v4 already defines `FanOutNode` bounded source, `FanOutItemBinding`,
key, closed body, yield, `MaximumItems`/`MaximumConcurrency`. The Zhinu
adapter already executes it with per-item `StepAsync` mapping because
Zhinu `preview.12` `FanOutAsync` derives positional index keys
(`docs/keyed-fanout.md:20`). So this stage adds no execution semantics —
only text.

- [x] Grammar (`docs/fuwen-grammar.json`): `fanout` production
  `fanout identifier over binding as identifier : type key binding max number
  [concurrency number] { fanoutBody* } yield binding -> type` with
  `fanoutBody = activity | conditional` and semantic rule for closed body /
  source-order aggregation. Authoring doc updated.
- [x] Lexer/parser (`FuwenSource.cs:SourceParser`): `ParseFanOut` +
  `ParseFanOutBody` with `fanOutSeen` -> `BuildV4()`, `fanOutItemName`
  for `FanOutItemValueBinding`, `ReadIdentifier` fix for `->` without space
  (`upper->` lexed as `upper` + `->`), `AddRegion`/`CountNodes` for
  `$body`, closed-region enforcement (no outer refs, no item shadowing,
  `FWN-CONTROL-002` for unsupported body nodes).
- [x] Compiler (`WorkflowCompiler.cs`): parser-built nodes reuse the existing
  fan-out validation path (key is `FanOutItemValueBinding`, bounds,
  item/result types, body boundary); no new IR needed.
- [x] Formatter: `FuwenFormatter` already handles `{`/`}`/`;`/`->` with
  `NeedsSpace`; verified idempotent via `Fanout_formatter_is_idempotent`.
- [x] Tests: `FuwenSourceFanOutTests.cs` — `Fanout_over_string_list…`
  compiles to v4, `Fanout_body_rejects_context…`, `Fanout_key_must_be_item…`,
  `Fanout_formatter_is_idempotent`, `Fanout_body_item_shadowing…`; all
  `138` compiler tests pass (was `133`). Remaining: one Baize-JSON-list-
  to-fan-out durable test through the new syntax (mirroring
  `FuwenZhinuFanOutTests.Baize_json_list…`) plus source-order aggregation
  check via DSL.
- [x] Docs: `fuwen-authoring.md` (`fanout` added) + `fuwen-grammar.json`
- [ ] Guyabano example: component context → inference → per-parent fan-out
  → aggregation in `.fuwen` (pilot wave rewrite).

Explicitly out of scope: dynamic ready-set scheduling. Guyabano computes
ready parents/leaves at runtime; Fuwen fan-out consumes a bounded list
value evaluated once. The supported composition is host-computed waves:
an activity produces the ready list, fan-out consumes it, and repetition
across waves is Stage 3's loop. Do not add runtime-computed dependencies
to fan-out itself.

Exit: the Guyabano pilot expresses one decomposition wave in `.fuwen`,
compiles to IR v4, executes durably with sibling reuse on single-item
restart, and the `.fuwen` formatter round-trips it.

## Stage 2 — Value-producing conditionals (new IR v5) — **done 2026-09-15**

Unlocks: review accept/repair branching (`if(CanAccept) … else …` with a
merged outcome), gap-review routing, build no-progress detection.

- [x] IR v5: `ConditionalNode` gains optional `ConditionalMerge(ThenValue,
  ElseValue, ResultType)` (`WorkflowPlan.cs`); omitting `Merge` keeps
  control-only behavior byte-for-byte. Core validator rejects `Merge` on
  pre-v5 IR; merged conditionals are valid `NodeOutputBinding` sources
  (validator + compiler `sourceType` switch).
- [x] New version triple (`fuwen-ir/v5`, `compiler-semantics/5`,
  `fuwen-execution/v5`); validator/fingerprint/identity accept v5, v1–v4
  paths untouched; `WorkflowPlanBuilder.BuildV5()`; PublicAPI baselines.
- [x] Parser: `if <name> <cond> { } else { } merge <then>, <else> ->
  <type>;` resolves branch-local names to structural paths, registers the
  conditional as a downstream-referenceable value, selects `BuildV5()`.
  Grammar JSON + authoring doc updated. Token-based formatter round-trips it.
- [x] Compiler: merge sides validated as branch-region consumers (existing
  closed-region rule — each side sees only its own branch); exact
  type-equality between sides and declared result; merge on non-v5 rejected.
- [x] Adapter: selected side evaluated after the branch, type-checked, and
  persisted as a durable `$merge` step (`ConditionalMergeRequestIdentity`)
  with no branch-descendant dependencies, so replay reuses the merged value
  without re-invoking providers at the merge step. Factory + interpreter
  accept v5.
- [x] Tests: `FuwenSourceConditionalMergeTests` (7: v5 shape, downstream
  consumption, type mismatch, unknown branch node, cross-branch reference,
  formatter idempotency, core-validator v5 gate) + durable
  `FuwenZhinuConditionalMergeTests` (both branches + conditional restart
  determinism). Suite: compiler `145`, Zhinu `32` per TFM, all green.
- [x] Golden vectors for v5 canonical bytes/fingerprints + v4–v7 isolation
  (done 2026-09-16 as part of the v4–v7 golden pass).
- [ ] Nested-conditional merge + Guyabano review-branch pilot wave (follow-up
  with the decomposition example).

Exit: review-decision branching (`accept` vs `repair-requests`) expressed
in `.fuwen` with a typed merged value — met for the sequential case.

## Stage 3 — Bounded loops (Milestone 10, new IR v6) — **done 2026-09-15**

Unlocks: architecture review passes (max 5), decomposition-architecture
integration budget (max 2), build/repair cycles (max 6), coherence
re-review — all currently unrolled.

Follow `roadmap.md:1063-1075` exactly: named `repeat` only, positive static
maximum (`1..1000` in snapshot, single-state `StateType == ResultType` for
v6), declared loop state (`LoopStateBinding`) + `iter` (`LoopIterationBinding`),
`continue` next-state + `break` condition (`ConditionExpression` on `iter`
or `state`), explicit outer result, first-class `$body` region with closed
scope, typed `LoopLimitExceeded` (never silent last-state), loop bounds in
canonical identity.

- [x] IR v6: `RepeatNode` single-state shape (`MaxIterations`, `StateType`,
  `InitialState`, `Body`, `ContinueWith`, `BreakWhen`, `ResultType`); v6
  triple (`fuwen-ir/v6` etc.); v1–v5 frozen; comparer/source-maps cover it.
- [x] Parser: `repeat <name> max <n> state <s>:<T> = <init> { <body> }
  continue <binding> break <condition> -> <type>` with `LoopStateBinding`
  + `LoopIterationBinding` only inside `$body`; closed body, `continue`/`break`
  see body outputs; `repeatSeen -> BuildV6()`; budget all parser dimensions.
- [x] Compiler: `InitialState` ↔ `StateType` exact, `ContinueWith` ↔
  `StateType` exact in body scope, `BreakWhen` as `bool` condition in body
  scope; `MaxIterations` 1..1000; single-state `ResultType == StateType`;
  `LoopState`/`LoopIteration` bindings only inside repeat; v6 gate.
- [x] Adapter (draft, in tree, builds): `LoopAsync(name, InitialState,
  _ => true, body, max)` with per-iteration `$loop/<name>/<n>/body/...`
  step keys, `GetLoopProgressAsync` for persisted count, `LoopLimitExceeded`
  typed as `Contract/LoopLimitExceeded`. Body dispatch: activity and
  conditional (no merge) nodes, no double-execution.
- [x] Tests: parser 6 (v6 shape, `s`/`iter` visibility, type mismatch,
  shadowing, formatter); durable 3 (break-early + `GetLoopProgressAsync`
  + selective restart, limit-exceeded typed failure, crash-between-iterations
  with `RestartStepAsync(StepOnly)` + resume + persisted progress proof).
  Conditional-inside-repeat with merge deferred (binding-validation gap
  for merge bindings in repeat body scope).

Exit: one Guyabano bounded cycle (build/repair, max 6) authored as
`repeat` in `.fuwen`, executed durably with per-iteration evidence.

## Stage 4 — Interaction gates (Milestone 9 + P0 external-input) — **done 2026-09-16**

Unlocks: `RequiresUserInput` clarification gates, restart-preview approval,
supervisor checkpoints.

- [x] IR v7: `CheckpointNode(Name, StructuralPath, Value, OutputType)` and
  `WaitNode(Name, StructuralPath, SignalName, OutputType, TimeoutSeconds?)`;
  `ApprovalOutcome` enum; v7 triple (`fuwen-ir/v7` etc.); all serialization,
  identity, comparer, bounds, and validator cases.
- [x] Parser: `checkpoint <name> value <binding> -> <type>;` and
  `wait <name> signal <signalName> type <T> [timeout <n>];` with
  `interactionGateSeen -> BuildV7()`. Repeat bodies accept checkpoint/wait.
- [x] Compiler: `GetNodeOutputType`, `CountNodes`, `AddNodeText`, `AddNodes`,
  `CollectNodeDescriptors` for new nodes; admission, repeat, and
  execution-order gates accept v7.
- [x] Adapter: `CheckpointNode` -> `context.StepAsync` (durable write);
  `WaitNode` -> `context.WaitForSignalAsync` (suspends on signal);
  repeat-body variants use `iteration.StepAsync`.
- [x] Factory + Compiler + Validator: v7 gate in all admission/validation
  paths; `RepeatNode` validation accepts v6 or v7.
- [x] Tests: 5 parser/compiler (checkpoint compiles, wait with/without
  timeout, checkpoint+wait, checkpoint inside repeat) + 3 durable
  (checkpoint persists, wait + signal delivery, approval gate deny
  leaves generation resumable with restart).
- [x] Golden vectors: `workflow_plan_v4.json` through `workflow_plan_v7.json`
  with stable fingerprints; v4–v7 isolation tests.

## Stage 5 — Guyabano migration waves (consumer side) — **queued in Guyabano**

Tracked in Guyabano; listed here so Fuwen reviews each wave against the
right contract:

1. Decomposition phase via Stage 1 syntax; recorded-failure corpus
   comparison (outputs, diagnostics, repair/retry rate, token use,
   provenance, restart scope).
2. Generation wave + review branching via Stages 1–2.
3. Build/repair loop via Stage 3; approval gates via Stage 4.
4. Live dogfood run; old-path removal only after parity + focused-restart
   reuse (Delivery F exit).

## What this plan deliberately does not add

- General loops, `while`, recursion, lambdas (`roadmap.md:83-88` non-goals).
- Runtime-computed fan-out dependencies; host-computed ready lists instead.
- Zhinu-side keyed `FanOutAsync`, workflow persistence, retries, or
  compensation — adapter maps to stable per-item `StepAsync` until Zhinu
  supports canonical keys.
- MCP/transport, session ledger (Hongxian), memory/code-graph (Cangjie/
  Hetu), model routing (Baize) — hosts compose these around admitted plans.
