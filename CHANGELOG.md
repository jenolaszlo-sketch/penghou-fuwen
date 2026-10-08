# Changelog

Notable changes to Penghou.Fuwen are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise source syntax and public contracts; immutable plans
remain governed by their explicit IR, canonicalization, and fingerprint
contract versions. Consumer-visible breaking changes are called out under
**Breaking changes** and given a recipe in the
[consumer migration notes](docs/consumer-migration.md).

## Unreleased

**Coordinated inference corrective work** (migration:
[consumer migration notes](docs/consumer-migration.md)):

- Bind logical interactions to the workflow run and rendered request; use
  fixed-length tool operation keys. Older in-flight coordinator journal state
  has unsupported semantics and must be migrated or explicitly stopped.
- Settle final usage/cost, validate currency, treat accumulator overflow as
  unknown, cap serialized evidence reports, and require a selected turn
  executor that declares hard completion-token enforcement when a completion
  ceiling is authored or supplied by the host.
- Require a host-owned durable protected-payload store for coordinated read
  tools. Successful results are stored outside ordinary journal state and
  verified on authorized replay. Other sensitive paths remain outside this
  narrow protection.
- Make exhausted tool allowances explicit no-tools finalization requests,
  reject oversized proposal batches before any tool I/O, and reject duplicate
  provider-visible tool names at registration.
- Carry exact catalogue tool signatures through in-process admission and
  validate proposed argument names/types before tool I/O and returned values
  before protected storage. Durable interaction identity now binds these
  signatures; older in-flight coordinator state stops on the new semantics.
  Provider-facing tool schemas and resource-level authorization remain open.
- Reject currency-free host-only cost ceilings at coordinated registration;
  include remaining monetary allowance in turn requests and mark mixed
  pricing revisions as unknown rather than summing them. Coordinator state
  semantics advance again, so older in-flight runs require migration or stop.
- Enforce coordinated per-call deadlines through cancellation while retaining possibly-committed timeout evidence. Strict prompt, total-token, and cost ceilings now fail admission before paid work until a durable reservation and trusted maximum-charge path exists. Authors may explicitly select `aggregate advisory` for after-call monitoring; host strict ceilings cannot be downgraded.
- Add a stock Baize coordinated-turn executor with exact profile/tool schema preflight, native assistant/tool history, bounded one-call transport and typed commitment failures. Carry explicit strict/advisory budget mode in requests; derive model-facing schemas from admitted callable signatures. Durable coordinator state advances to v5 and older in-flight states stop. Provider continuation blobs and resolved endpoint metadata remain unsupported on this contract.
- Add an authored, typed static inference fallback for selected definitive failures. A separate durable step records the fallback while the original operation remains failed. Fallback-bearing plans use `fuwen-ir/v2-inference-fallback`; existing plans retain `fuwen-ir/v1` and their fingerprints. A root-region evaluator, checkpoint, human wait, and acceptance conditional can convert that disposition into a separate accepted decision without retrying settled work. General failure-handler regions remain open.
- Reconcile README and capability documentation with the still-open budget,
  recovery, privacy, provider, and operator gates. No new NuGet package is
  published by these source changes.

## 0.1.0-preview.13

- Authoritative Fuwen→Zhinu plan-to-step mapping: new public
  `FuwenZhinuStepMapper` builds a plan/revision-scoped `FuwenZhinuStepMap`
  describing every durable step the port can emit (declared, synthetic, and
  repeat/fan-out parameterized) and classifies persisted step keys through
  `TryMatchStepKey(map, stepKey, out match)` with no consumer-side parsing or
  key reconstruction.
- Shared adapter-owned `StepKey` construction: the interpreter and coordinators
  now build every step key through one internal module (`FuwenZhinuStepKeys`),
  so execution, mapping, and matching cannot drift. Because Zhinu's
  durable-loop key helpers are internal, execution-backed exactness tests are
  the compatibility guard against future key-convention drift.
- Authoritative persisted-`StepKey` matching: exactness tests assert every
  persisted `WorkflowStepRun.StepKey` is matched exactly once and every
  declared node is represented even when it never executes.
- Prior additive work included in this preview: `Fuwen.Inspect` read-only plan
  verify/validate/explain consumer with explanatory plan compare, and ADR 0012
  deferring Penghou.Luban and decoupling Hufu from the command language.

## 0.1.0-preview.12

**Additive**: a first-class, neutral `ActivityExecutionIntent` on `ActivityNode`
(a logical profile plus required/preferred neutral guarantees) under the new
`fuwen-ir/v3-execution-intent` IR version. Plans without an intent keep
`fuwen-ir/v1` and their existing canonical bytes, fingerprint, and admission
receipts; an intent is included in the canonical plan and execution fingerprint
and is rejected under the base IR version. Ordinary activity inputs are
unchanged, and the plan still never names a provider, executable, environment,
or authority.

## 0.1.0-preview.11

**Breaking changes** (recipe:
[consumer migration notes](docs/consumer-migration.md)):

- Remove the versioned-vector constant names:
  `FuwenContracts.ExecutionFingerprintVersionV1` → `ExecutionFingerprintVersion`
  and `FuwenContracts.IrVersionV7` (and other `*V<n>`) → `IrVersion`.
- Collapse `WorkflowPlanBuilder.BuildV3()/BuildV4()/BuildV5()/BuildV6()/BuildV7()`
  into `Build()`.
- Inference executors must implement `IInferenceExecutorPreflight` or
  `IInferenceExecutorManifest`; otherwise workflow registration fails with
  `FuwenZhinuAdmissionException`.

**Other changes:**

- Harden compiler determinism, patch-oriented formatting, registered prompt
  execution, bounded context delivery, media deadlines, catalogue discovery,
  language-reference conformance, and validated release publication.
- Add the bounded complex-inference vertical slice: aggregate protocol limits
  and `aggregate` source syntax, provider-neutral turn/read-tool ports with
  pre-execution validation, deterministic conformance fakes and suites, a
  durable Zhinu-owned model → tool → model coordinator with stable operation
  identity and replay reuse (sequential, conditional, and repeat regions),
  bounded privacy-preserving evidence with operator reports, and a recovery
  and compatibility matrix.
- Prove the slice through isolated package consumers restored from packed
  packages with no sibling project references: a credential-free Marang
  planning proof (source compile, explanation, preflight, admission, run,
  fail, resume, evidence, fork/restart, typed planning result with separately
  authorized promotion) and a second product-neutral review consumer, both
  exercised in CI on .NET 8 and .NET 10.

## 0.1.0-preview.10

- Add per-inference `maxTokens` and `timeout` limits to IR v8, canonical
  identity, source syntax, and execution adapters.
- Admit effect-free/read tools and explicitly idempotent, retry-safe write
  tools while continuing to reject unsafe effects.

## 0.1.0-preview.9

- Add IR v8 workflow-owned prompts, typed prompt bindings, registered prompt
  aliases, toolsets, inline tool lists, and `tools none`.
- Carry prompt and tool identity through compilation, admission, comparison,
  Zhinu execution, and Baize mapping.

## 0.1.0-preview.8

- Accept host-declared prior-plan evidence across execution forks without
  treating explanatory lineage as execution authority.

## 0.1.0-preview.7

- Update the durable adapter to Penghou.Zhinu preview.13 and retain the tested
  IR v3-v7 execution boundary.

## 0.1.0-preview.6

- Allow repeat initial state to reference parent-region values safely.
- Publish `RuntimeValueJson` for host-side typed runtime-value conversion.
- Expand repeat condition, projection, resume, and result regressions.

## 0.1.0-preview.5

- Allow context and inference nodes inside bounded fan-out bodies with closed
  per-item scope and durable replay behavior.

## 0.1.0-preview.4

- Make object literals assignable to compatible named schemas.
- Treat optional callable parameters as omittable during source binding.

## 0.1.0-preview.3

- Add IR v5 value-producing conditional merges, IR v6 bounded repeat regions,
  and IR v7 checkpoint and external-wait interaction gates.
- Fix cross-version combinations for merges, context requirements, repeat
  inference, catalogue discovery, conditions, and minimum-IR selection.

## 0.1.0-preview.2

- Upgrade `Penghou.Fuwen.Baize` from `Penghou.Nuwa 0.6.2` to `1.0.0` (verified
  compatible: `IJsonRepairPipeline` / `JsonRepairPipeline.Create` unchanged).
- Add `Microsoft.SourceLink.GitHub 10.0.401` to packable projects for
  deterministic source linking and to resolve the `Microsoft.Build.Tasks.Git
  8.0.0` vulnerability.

## 0.1.0-preview.1

- Introduce provider-neutral immutable workflow plans, canonical JSON,
  fingerprints, source maps, revision lineage, and semantic plan comparison.
- Add trusted catalogue resolution, bounded programmatic compilation, host
  admission receipts, and immutable definition storage.
- Add typed runtime values, execution requests/results, artifact publication
  evidence, context snapshots, and provider-neutral execution failures.
- Add the SQLite-tested `Penghou.Fuwen.Zhinu` durable execution adapter.
- Add the bounded minimal Fuwen source language and canonical formatter.
- Add `Penghou.Fuwen.Baize` for descriptor-bound structured and media
  inference, crash-safe generated-asset publication, typed cost evidence, and
  trusted retry cost ceilings.
- Add preview IR v4 bounded keyed fan-out with stable item identities,
  deterministic aggregation, durable replay, and focused item restart.
- Harden Baize interoperability with representation-neutral list normalization,
  single-pass prompt rendering, conservative unknown-cost handling, bounded raw
  and repaired output, pinned generation identities, and complete failure-path
  evidence.
- Specify and test the execution-port conformance and generated-asset publisher
  contracts, including a durable non-code media fixture.

This is the first preview. No compatibility with an earlier public Fuwen
package is implied.
