# Changelog

Notable changes to Penghou.Fuwen are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise source syntax and public contracts; immutable plans
remain governed by their explicit IR, canonicalization, and fingerprint
contract versions.

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
