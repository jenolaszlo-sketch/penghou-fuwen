# Complete Fuwen–Zhinu integration correctness pass

Date: 2026-10-20. Reviewed baselines: Fuwen `232d2c3` / runtime `631fa64`,
native `2d3071e` / runtime `bf33c05`. The earlier
[selected-profile pass](second-pass-review.md), [native Q33 qualification](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/4b927d23d2b3aef0e3de4057b06d4e7372561f7f/docs/qualification/activity-groups/q33.md)
and [v5 selection checkpoint](selection.md) remain separate immutable records.

## Confirmed defects and fixes

1. **Cross-run provider idempotency collisions (P1).** Repeated identical inputs
   in two independent grouped-repeat runs produced identical operation keys.
   Inspection found the same missing run scope in legacy ungrouped fan-out and
   in activity/context/one-call inference dynamic invocations. New compilation
   emits `fuwen-ir/v6-run-scoped-dynamics`; provider runtime paths include the
   run ID before deriving the existing operation key. Native occurrence and step
   identities are unchanged. Tests cover all three provider kinds in grouped and
   ungrouped repeat/fan-out, independent runs, completed-result replay, and old
   v1/v4 contracts. The old grouped fan-out already used run-scoped invocations.

2. **Ungrouped fan-out mapper did not round-trip (P2).** The public matcher
   returned a hash-only item token, but the resolver failed to restore `sha256-`.
   Runtime-backed tests exposed the same bug under both legacy and v6 plans.
   Resolution now restores the prefix without changing the public matched scope
   representation or any persisted key. Matching rejects noncanonical hashes;
   resolution validates the scope path and accepts both canonical full tokens
   and the existing hash-only representation.

3. **Admission omitted the full provider identity byte budget (P2).** A valid
   deeply nested plan could be stored even though its provider runtime path,
   including the run GUID/slash, exceeded the 1,024-byte execution contract.
   Registration now checks full paths and declared iteration/epoch maxima before
   writing definitions. Static and dynamic boundary tests execute successfully
   at exactly 1,024 bytes and reject oversized identities before storage or
   provider calls. An initial grouped fan-out concern was ruled out: its group
   key limit already rejects the constructed oversized example.

4. **Two ungrouped repeat regions could share native loop state (P1).** Native
   ungrouped loop keys use their authored name. Two separately declared regions
   with the same name and initial state executed only the first body's provider
   and reused that completed loop for the second region. A runtime counterexample
   retains the missing second call. Registration now rejects duplicate ungrouped
   loop names across regions before storage. Expanded grouped repeats retain
   their existing structural namespaces and support repeated local names; their
   positive regression remains passing. No persisted keys are rewritten.

## Compatibility and scope

Changing keys on a retry after an external effect may already have committed
would be unsafe. The correction is therefore versioned. Old v1–v5 definitions
retain their bytes, fingerprints, native keys and provider identity contracts.
Their legacy repeat/ungrouped-fan-out collision behavior is not silently rewritten.
Adopt v6 by compiling/admitting a new definition for new runs; keep existing runs
on their pinned definition. See the [v6 contract](../../run-scoped-dynamic-invocations.md)
and [migration notes](../../consumer-migration.md).

The v6 expanded-group flag is serialized and fingerprinted, deeply snapshotted,
and requires its immutable companion before registration. Core validation retains
expanded path and innermost membership checks. The selection, preflight, progress,
retry/restart and process-loss cases run against newly compiled v6 definitions.
A separate four-epoch proof continues exercising an explicitly pinned v5 bundle.
Legacy canonical vectors remain unchanged. No native algorithm/schema change
or repacking of earlier native/Fuwen candidates is required.

The pass also inspected atomic replacement/retirement, late-claim/completion
fencing, descendant membership guards, current versus historical reduction,
snapshot receipt validation/indexes, authority paths, public mapper scope
qualification, immutable admission and package/binary boundaries. Native source
remains unchanged; its independently qualified Q33 baseline is retained.

## Qualification

The exact runtime source, full tests, package-only consumers, archive hashes,
precompiled compatibility and Windows/Linux results are recorded in the paired
`integration-second-pass.json` ledger. Earlier failing/staging receipts are kept
separate from passing totals. Packages remain unpublished; broader DSL features
and downstream adoption remain independent work.
