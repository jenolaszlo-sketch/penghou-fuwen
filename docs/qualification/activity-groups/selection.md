# Bounded Fuwen selection qualification

Runtime/package source: `631fa642a78075278ed2f6a58609554a52e33788`, unpublished
Fuwen `0.1.0-preview.19`. Native source:
`bf33c05cf2390af6924bbe3761e09f9e377c7a97`, exact original unpublished Zhinu
`0.2.0-preview.10`, schema 10. The [contract](../../activity-groups-selection.md)
defines the bounded supported shape. [The ledger](selection.json) records file
hashes, source trees, package audits and local/remote receipts.

## Proofs

- A distinct v5 contract survives immutable plan serialization and admission.
  Existing canonical vectors and earlier key/adapter tests remain passing.
- Contraction, reordered expansion, reappearance and empty selection use fresh
  native scopes. Four successive epochs preserve original occurrence rows and
  completed body step facts; current Work counts follow the current expansion.
  Provider runtime identities remain distinct when an item reappears.
- Every persisted selection/provider/completion key round-trips through the
  public mapper. The new map exposes explicit epoch and typed-item scopes;
  aggregate and return are derived projections in v5 only.
- Two process-loss cases kill the old worker either before the second item is
  materialized or after the first item has completed. Replacement retires the
  unfinished old branch while retaining completed history. A second kill occurs
  after replacement operation one has committed. A third process re-admits only
  stored immutable bundle bytes and finishes operation two, preserving occurrence
  parents and committed results. It succeeds after the external selector input
  is replaced with invalid JSON; the selector runs exactly once per admitted epoch.
- Invalid bounds/epoch values and duplicate keys dispatch no item work. Reused
  epochs and missing predecessor scopes dispatch no replacement work. Unsupported
  downstream shapes and aggregate epoch-operation budgets fail before definition
  storage or provider invocation.

## Validation

The local frozen-source Release run passes **1,740 tests** across .NET 8/10,
including **14 new selection cases per framework**. Both package-only consumer
projects pass **28 executions** across those frameworks. Their verifier proves
absence of source project references, exact source/version pins, all eleven
supplied archives and resolved package-cache archive hashes. The packaged
selection consumer executes the four-epoch history/progress/mapper proof.

Four local Fuwen archives contain eight DLLs matching the tested Release build
byte-for-byte. The native archives match the original Q33 pin; none were repacked.
The unchanged public preview.11 consumer binary also executes against the new
assemblies. Its original adapter/native preview.14 database is schema 4, which
preview.15 and the candidate reject without mutation. The fresh-candidate schema
10 binary proof is separate from persisted-state upgrade. Native Q33 retains the
schema 5–9 migration and older-worker rejection evidence.

[CI run 39400969468](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/39400969468)
uses the same frozen source. Each Windows/Linux producer runs 1,740 tests,
formatting/canonical checks, native closure/legacy probes and exact-source package
preparation. Both OS consumers use the same eleven Ubuntu producer archives;
they run 28 consumer executions, archive/cache verification and the precompiled
legacy Fuwen probe. CI packages use `0.0.0-ci.39400969468`, with their own recorded
hashes; those bytes are not claimed identical to the local preview.19 archives.
Source rebuild manifests preserve the original native archive pin and identify
their separately hashed native archives.

The previous ZG-7 [pin](zhinu-zg7-candidate.json) remains byte-identical, SHA-256
`f261909323909976820732fbbc2abccba4e6e221c9017b41f38b7762d9585f9b`.
All seven original preview.9 archives also retain their recorded hashes. The old
sealed ledger's `OriginalNativeArchivePin.File` names its historical path;
`zhinu-zg7-candidate.json` now preserves those exact bytes. The [current pin](zhinu-candidate.json)
explicitly selects preview.10. Earlier ledgers and qualification totals are not
rewritten to include this extension.

## Review and limits

The review found and fixed one validation-order regression before freezing the
runtime: unknown/malformed IR must be rejected before the selection scan. Existing
inspector regression tests caught it. The full release-contract suite also
required the preview.19 changelog/migration entry. Initial failing/staging receipts
are retained separately and are not included in the passing totals.

The integration deliberately avoids two identity hazards: the selection's
evaluated values are durable output of a structurally identified boundary, and
v5 emits no fixed-key aggregate/return steps whose inputs could change after
restart. Epoch bounds apply across historical expansions, preventing a succession
of individually bounded selections from bypassing the admitted operation budget.

This checkpoint qualifies one terminal grouped selection and explicit native
StepOnly restart of its controlling boundaries. It does not qualify arbitrary
condition changes, automatic iterative reselection, downstream operations,
nested/mixed dynamics, item waits/checkpoints or value merges, replacement-bearing
forks, child-workflow syntax, generalized group control APIs, or publication.
No native algorithm, schema or occurrence-identity redesign was needed for this
Fuwen lowering; it uses the independently qualified Q33 model.
