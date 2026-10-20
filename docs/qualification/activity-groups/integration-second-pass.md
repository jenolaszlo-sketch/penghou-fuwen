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

Frozen runtime/package source is
`eca1213796d9fa4ad08423e2691cb409c892f63b`, unpublished Fuwen preview.20.
The exact seven original native preview.10 archives remain pinned to
`bf33c05cf2390af6924bbe3761e09f9e377c7a97`, schema 10. Native source, tests and
CI are unchanged from that independently qualified Q33 runtime.

Local Windows qualification passes **1,770 regression executions** across
.NET 8/10 (314 core, 251 compiler, 94 Baize and 226 adapter cases per framework),
plus **32 package-only consumer executions** (7 Marang and 9 review cases per
framework). All eight packaged Fuwen DLLs match their Release binaries. Archive
and restore-cache checks verify the eleven-package closure. The earlier 151
Fuwen selection and 74 referenced native Q33 evidence files retain their hashes,
as do all four original Fuwen preview.19 package archives.

The package consumer initially overclaimed reverse resolution for synthetic
loop-control descriptors, which classify multiple terminals without selecting
one in their public scope. Qualification source
`3d8ee2b9573bce350f4c05584da26eaa167dbca4` corrects only that assertion: every
persisted step must classify, and descriptors with resolvable forms round-trip.
Its runtime, build, eng and CI trees are identical to the frozen package source.
The original failed receipts are retained; the passing totals use the corrected
harness. Local preview.20 packages were not repacked after this test correction.

[CI run 39408318656](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/39408318656)
uses the corrected qualification source. Each Windows/Linux producer runs the
1,770 regression executions, formatting/canonical checks, native closure/legacy
probes and exact-source candidate preparation. Both OS consumers use the same
eleven Ubuntu producer archives for their 32 executions, archive/cache checks
and precompiled legacy probe. CI package version is `0.0.0-ci.39408318656`;
its archive hashes are recorded separately from local preview.20. Native source
rebuilds retain the original native pin and identify their separate archives.

The first Windows attempt failed one .NET 10 process-loss case during
`lease.recover` with SQLite error 10, immediately after worker termination.
The cause was not established. Both loss barriers pass a separate local recheck
on both frameworks (four additional executions, excluded from the 1,770 total).
The failed/dependent CI jobs were rerun with identical source; neither runtime
nor tests received an I/O retry. The ledger retains the earlier failure status,
log and receipts separately from the final passing evidence.
The successful Windows producer artifact is ID `12258452559` (attempt 2),
distinct from failed-attempt artifact `12259015486`; GitHub retained both under
the same display name. The ledger selects the successful archive by ID.

The unchanged published preview.11 consumer binary executes with the candidate
assemblies. Its schema-4 database is rejected without changing bytes; the fresh
candidate database reaches schema 10. This is a binary-compatibility proof, not
a schema-4 migration claim. Native Q33 retains its independent supported
migration and older-worker rejection evidence.

The paired [ledger](integration-second-pass.json) records exact source trees,
receipts, package hashes and all four final platform jobs. Confirmed failures are
selected by test name; ruled-out group-budget probes, the initial incorrectly
sized dynamic-path fixture and the consumer assertion mistake do not count as
runtime defects or passing evidence. Packages remain unpublished; broader DSL
features and downstream adoption remain independent work.
