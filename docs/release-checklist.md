# Preview release checklist

Use this checklist for every preview package set. A green test count alone does
not satisfy a semantic release gate.

## Contracts

- [ ] The roadmap identifies the exact completed delivery milestone and all
  deferred behavior.
- [ ] Public API baselines are intentional and contain no accidental framework
  or provider-specific types.
- [ ] IR, canonical JSON, fingerprint, operation-key, diagnostic, and source
  language contract versions are unchanged or explicitly versioned.
- [ ] Golden vectors pass through the .NET and independent implementations.
- [ ] Package dependency versions exist on NuGet.org and match the tested
  dependency graph.

## Verification

- [ ] `dotnet build Penghou.Fuwen.slnx --configuration Release` passes.
- [ ] `dotnet format Penghou.Fuwen.slnx --verify-no-changes --no-restore`
  passes.
- [ ] All multi-target unit, compiler, adapter, recovery, and conformance tests
  pass without network credentials.
- [ ] `python3 tests/golden/canonical_json_v1.py` passes in CI.
- [ ] Every packable project produces an `.nupkg` and `.snupkg`; package
  validation reports no unintended compatibility change.
- [ ] A clean restore and consumer sample build use only public packages.

## Evidence and safety

- [ ] Provider failures, repair attempts, final schema validation, retries,
  artifact publications, and actual provider/model usage remain distinguishable
  in durable evidence.
- [ ] Crash/replay, same-operation retry, focused restart, changed input,
  changed plan, cancellation, and corrupted evidence tests pass.
- [ ] No sample, fixture, package, log, source map, or provenance record contains
  secrets or machine-specific absolute paths.
- [ ] A non-code artifact fixture and the bounded Guyabano pilot are recorded,
  or the release is explicitly held until they are.

## Publication

- [ ] `CHANGELOG.md`, `README.md`, API documentation, security guidance, and
  package descriptions agree with the shipped surface.
- [ ] The version in `Directory.Build.props` is the intended preview version.
- [ ] CI passes on the release commit and the tag exactly matches the package
  version (`v<version>`).
- [ ] NuGet provenance/OIDC publishing completes and both symbols and packages
  are indexed before downstream projects update.
