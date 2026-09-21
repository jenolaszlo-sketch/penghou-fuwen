# Changelog

Notable changes to Penghou.Fuwen are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise source syntax and public contracts; immutable plans
remain governed by their explicit IR, canonicalization, and fingerprint
contract versions.

## Unreleased

- Harden compiler determinism, patch-oriented formatting, registered prompt
  execution, bounded context delivery, media deadlines, catalogue discovery,
  language-reference conformance, and validated release publication.

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
