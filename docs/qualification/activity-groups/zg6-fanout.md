# ZG-6.2b: independently durable grouped fan-out integration

Date: 2026-10-19. **Passed for the bounded keyed fan-out profile** on Windows,
.NET 8/10. Full FM-2/ZG-6, Q45 and ZG-7 remain open. Packages are unpublished.

Production/package source is Fuwen `86188500f12799bdb1d4675cdf73b513f2bcfbca`
(`0.1.0-preview.16`) and Zhinu `fa0297b9dc21b235f27ac92b979dcf9faeb6b7b6`
(`0.2.0-preview.8`). The [ledger](zg6-fanout.json) pins archive/binary hashes,
source commits, final test receipts and package-only consumer resolution. The
[original fan-out native pin](zhinu-fanout-candidate.json) fixes all seven packages. The
[repeat checkpoint](zg6-repeat.md) retains its [preview.7 pin](zhinu-repeat-candidate.json),
and the [static checkpoint](zg6-static.md) retains its original evidence.

## Qualified behavior

The [bounded profile](../../activity-groups-fanout-profile.md) supports one fixed
keyed fan-out, optionally within static groups. Exactly one explicit item-local
root group encloses the complete body; nested logical/macro groups retain the
correct item parent. Activities, context and one-call inference have independent
durable steps and Work membership. Selection, item completion and aggregate use
Control membership. Control-only conditional decisions remain pure. No synthetic
group, schema migration or occurrence codec redesign is introduced; schema is 9.

Typed key digests distinguish key types and preserve source-order results despite
concurrent dispatch. Native body keys encode selected tokens and hashed structural
operation names. Public mapper classification/resolution round-trips real stored
steps. Historical ungrouped fan-out keys and provider policies remain unchanged.

The selected vector is durable before item admission. Its canonical output must
exactly match the admitted input; a changed value fails before restarted provider
work. Selection and cumulative outcome vectors each have a 1 MiB budget. Admission
also bounds item-operation products, occurrence counts and derived identities.

The native body contract pins the immutable execution fingerprint. Claims are
fenced to current selection/item history and ordinary leases/generations. Each
body executes outside its completion callback, reusing completed operations on
replay. Completion depends on all registered child operations, and native closure
checks their current revisions, input hashes and memberships. Dependent restart
reruns the selected operation while preserving predecessors and sibling history.
`StepOnly` restart of body steps and item completions fails before writes.

Definitive provider failures retain completed native failure envelopes and may
complete native item/aggregate steps while the Fuwen run fails. Cancellation,
claim and fencing errors propagate rather than becoming successful outcomes.
Group progress remains mechanical; current members do not prove future closure.

## Verification

The complete final regression set has **1,690 passing executions**: core 314,
compiler 251, Baize 94 and adapter 186 on each framework. The original complete run
passed 1,688 executions. After the final selection-integrity check and corruption
probe, the whole adapter project was rerun on both frameworks; those 186-test
receipts replace only the earlier 185-test adapter receipts. Staging and earlier
runs are excluded from the final total. Formatting and independent canonical
JSON vectors pass.

Source-level tests cover nested item parentage, Work/Control ownership, mapper
coverage, concurrent output ordering, context/inference replay, conditional
branches, duplicate keys, size budgets, definitive provider failure and admission
rejection. A hard-process-loss worker commits operation one, blocks before the
second provider effect, is killed, then resumes in a new process using only the
stored immutable bundle. Independent hierarchy reads before/after recovery prove
stable parentage/memberships and no repeated first effect on either framework.

The package-only consumer passes **12 additional executions**. Its assets have
no project references; all six resolved Fuwen/Zhinu candidate archives match the
cache. Four Fuwen packages identify the exact production source above, and all
eight packaged DLLs match the source build. The added consumer exercises real
source compilation, bundle storage/byte-only readmission, grouped registration,
execution, query parentage, membership roles and mapper round-trips. Zhinu's
separate full suite passes 1,925 tests plus package-closure and precompiled legacy
consumer/schema migration probes; those are not added to the Fuwen total.

The first final build exhausted C: while copying test dependencies. All generated
artifacts/caches and src/tests bin/obj directories now reside under
`D:/temp/activity-groups`, reached through workspace junctions; TEMP/TMP use D:.
The entire Fuwen solution was rerun successfully after relocation. The package
consumer initially had query-property/assertion compile errors, corrected before
its final run. A proposed empty-body counterexample was rejected by the compiler
itself; no redundant runtime restriction was retained.

## Remaining gates

Nested/mixed dynamic composition, evolving-selection adapter lowering, item waits/
checkpoints, conditional value merges, grouped coordinated inference, item-local
fallback and broader closure/progress qualification remain open. Custom stores
must implement the optional native body repository capability; a missing capability
can be discovered after the selection Control step, before provider dispatch.
The qualified store is SQLite. Linux CI, Fuwen precompiled legacy binaries,
public-package adoption and full ZG-7 are separate gates. No package publication
or full FM-2/ZG-6/Q45 completion is implied.
