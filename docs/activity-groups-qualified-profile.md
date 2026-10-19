# Activity groups: supported cross-repository profile

The current unpublished candidate pair is Fuwen preview.17 and exact Zhinu
preview.9. The [native pin](qualification/activity-groups/zhinu-candidate.json)
identifies the original Windows archives and source. Earlier static, repeat and
fan-out qualification records retain their original source and archive pins.
Cross-platform qualification and the separate second correctness pass are in
progress; candidate preparation is not package publication.

## Supported authoring and execution

Local immutable macros and logical groups expand into ordinary typed Fuwen
primitives. Nested static groups/macros, multiple independent invocations,
static conditions, checkpoints and signal waits use native durable boundaries.
A grouped plan can contain single-level bounded repeats, or one fixed keyed
fan-out with an explicit item-local root and nested static groups/macros.
Fan-out activity, context and one-call inference operations are independently
durable; control-only item conditionals are supported. Groups change neither
authority nor provider policy. The DSL has no child-workflow syntax.

Nested/mixed dynamic scopes, evolving selection, fan-out item waits/checkpoints
or value merges, grouped coordinated inference and item-local fallback remain
preflight rejections. These are explicitly allowed exclusions in the Q43–Q45
qualification contract, not implicitly supported native combinations. Broader
language support is separate work. No public group cancellation/retry API or
first-class selection epoch API is introduced.

## Inspection and recovery

Public mapping identifies real persisted steps, item/iteration scope and direct
Work/Control membership. Counts measure mechanical native execution rather than
domain acceptance: a completed failure envelope can coexist with a failed Fuwen
run. An open parent has no promised future total. A completed item and its nested
groups remain Completed while an independent sibling runs or is cancelled; the
reader proves that seal from current selection, exact input identity, dependency
and membership facts. Older snapshot providers omitting the new optional hashes
remain conservative. Historical/current revisions and frozen snapshot tokens
retain their separate meanings.

Recovery re-admits the exact immutable stored bundle without source recompilation
or macro-library lookup. Changed execution or manifest pins are rejected before
provider re-execution and cannot rewrite the original admitted hierarchy.

## Reproducible candidate and compatibility checks

`eng/Prepare-ZhinuCandidate.ps1` verifies the exact seven original archive hashes
by default. The optional explicit source-rebuild manifest must preserve source,
version and package identities and bind back to the original archive pin.
`eng/Prepare-ZhinuSourceCandidate.ps1` requires a clean checkout at that exact
source commit and builds only the seven shipped projects. Rebuilt archives get
their own hashes and provenance; their bytes are not claimed to equal the original
Windows build. The tracked original archive pin is never overwritten by CI.

Both OS package consumers download the same Ubuntu producer archives. The
consumer verifier checks absence of project references, exact versions/source,
all eleven supplied package archives and cached archive hashes, plus passing
receipts for both target frameworks. Native package closure and precompiled
legacy probes run separately on both OS producers.

The precompiled public Fuwen preview.11 consumer also runs against candidate
assemblies with its unchanged DLL and the required neutral runtime dependency.
Its old adapter uses native preview.14 and creates schema 4. Native preview.15
and the current candidate reject that unsupported database without changing its
bytes; the fresh-candidate binary proof is separate from persisted-state upgrade.
Supported schema 5–9 migration remains covered by the native legacy consumer.
There is no new schema 4 migration in this feature.
