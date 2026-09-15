# Fuwen prep plan for the Guyabano migration

Status: **in progress — pilot proof complete, Stage 1 next**. Last updated: **2026-09-15**.

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
- [ ] Run the full `release-checklist.md`: `dotnet format --verify`,
  `tests/golden/canonical_json_v1.py`, pack validation review (build/tests
  already green).
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

## Stage 2 — Value-producing conditionals (new IR v5) — **queued after Stage 1**

Unlocks: review accept/repair branching (`if(CanAccept) … else …` with a
merged outcome), gap-review routing, build no-progress detection.

Current `ConditionalNode` is control-only by design
(`WorkflowPlan.cs:91-96`, `zhinu-adapter.md:57`); branch-local returns are
rejected (`FWN-CONTROL-001`). The roadmap requires an explicit typed
branch-result/merge contract before source exposes value conditionals
(`roadmap.md:660,669`).

- [ ] IR v5: extend `ConditionalNode` with an explicit merge declaration —
  recommended shape: an optional `Merge` binding pair naming one value from
  each branch plus a declared result type, with type-equality enforced at
  compile time.Closed-region rules stay: branches may not leak undeclared
  values; omitting `Merge` keeps today's control-only behavior byte-for-byte.
- [ ] New version triple (`fuwen-ir/v5`, `compiler-semantics/5`,
  `fuwen-execution/v5`); validator accepts v5 and keeps v1–v4 paths
  untouched; golden vectors for v5 canonical bytes and fingerprints plus
  v1–v4 isolation (same pattern as the v2/v3 isolation).
- [ ] Parser/formatter: `if … { … } else { … } merge …` spelling (one
  canonical spelling per Milestone 7); stable diagnostics for
  branch-type mismatch and undeclared merge sources.
- [ ] Adapter: evaluate the merge binding after the selected branch region
  completes (`ExecuteRegionAsync` returns the branch result); persist it in
  the conditional step envelope so replay does not re-execute branches.
- [ ] Tests: merge type mismatch, missing merge source, replay after branch
  completion, nested conditional merge.

Exit: review-decision branching (`accept` vs `repair-requests`) expressed
in `.fuwen` with a typed merged value; pilot covers one such branch
durably.

## Stage 3 — Bounded loops (Milestone 10, new IR v6) — **queued**

Unlocks: architecture review passes (max 5), decomposition-architecture
integration budget (max 2), build/repair cycles (max 6), coherence
re-review — all currently unrolled.

Follow `roadmap.md:1063-1075` exactly: named `repeat` only, positive static
maximum, declared loop state, complete `continue with`, type-compatible
`break with`, explicit outer result, first-class region with structural
body identity and closed scope, typed `LoopLimitExceeded` (never silent
last-state return), loop bounds and carried-state contracts in canonical
plan identity.

- [ ] IR v6: `RepeatNode` (name, path, static max, state declaration,
  body region, exit binding). New version triple; v1–v5 frozen.
- [ ] Parser/formatter/grammar for `repeat ….with` spelling; budget all
  parser dimensions under `CompilationBudget`.
- [ ] Compiler: definite-assignment across iterations, state-type
  compatibility of `continue`/`break`, max-attempts positivity, closed body
  scope; stable diagnostics.
- [ ] Adapter: iteration execution as durable per-iteration steps
  (`RuntimeNodeIdentity` iteration paths already reserved per
  `identity-and-fingerprints.md:12`); iteration restart invalidates the
  selected/later iterations and dependents while preserving earlier valid
  iterations (mirror of the fan-out restart contract).
- [ ] Tests: limit-exceeded typed failure, state-type mismatch, crash
  between iterations, selective iteration restart, artifact evidence per
  iteration.

Exit: one Guyabano bounded cycle (build/repair, max 6) authored as
`repeat` in `.fuwen`, executed durably with per-iteration evidence.

## Stage 4 — Interaction gates (Milestone 9 + P0 external-input) — **queued**

Unlocks: `RequiresUserInput` clarification gates, restart-preview approval,
supervisor checkpoints.

Two contracts, in order:

1. **P0 external-input node** (`roadmap.md:402-407`): typed structured
   node for supervisor-authored values/checkpoints/approvals with declared
   inputs/outputs, timeout, cancellation, duplicate/replay/late-input
   semantics. Required before accepting any supervisor-authored plan.
2. **Milestone 9 waits** (`roadmap.md:1053-1061`): named waits mapped to
   Zhinu idempotent signals with typed timeout/cancellation/duplicate/
   late-signal behavior; host intents for input/selection/approval without
   UI semantics. Coordinate with Zhinu signal-consumption fencing
   (upstream gate noted in Qingniao's dependency plan) and with Guyabano's
   open product-level input/wait/resume policy — do not design the wait
   surface unilaterally.

Exit: one approval gate (restart-preview approve/deny) authored in
`.fuwen`, executed durably against fake signals, with denial leaving the
current generation resumable (the Guyabano invariant from its roadmap
`Phase 7` replan items).

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
