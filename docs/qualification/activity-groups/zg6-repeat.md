# ZG-6.2a: grouped single-level repeat integration

Date: 2026-10-19. Status: **passed for the bounded repeat profile** on Windows,
.NET 8/10. Full FM-2/ZG-6, Q45 and ZG-7 remain open. Packages are unpublished.

Production/package source is Fuwen `cde940fa21cb4e0a117814502afb4898b9303819`
(`0.1.0-preview.15`) and Zhinu `45e831a82593048dc1ea7266cbc1e9b306d8c92c`
(`0.2.0-preview.7`). The [ledger](zg6-repeat.json) records archive/binary hashes,
source commits and test receipts. The [native pin](zhinu-repeat-candidate.json) fixes the
seven-package closure. The [static checkpoint](zg6-static.md) retains its own
original source and package evidence.

## Qualified behavior

- Source expansion, immutable bundle storage and byte-only readmission now
  support one repeat level with nested macros/logical groups. Each iteration
  has distinct occurrences and correct explicit parents. No schema or occurrence
  identity codec changes were needed; persisted schema remains 9.
- Activity, context, one-call inference, checkpoint and wait operations are
  independently durable. Loop condition, commit, completion and limit evidence
  use Control membership; body operations retain direct Work/Control ownership.
  Iteration caches are released after use.
- Expanded repeat/body keys include the full structural-path digest and fit
  existing identity length limits. Sibling macro calls can reuse local names.
  Historical macro-free keys remain unchanged. Public mapping covers every
  emitted fixture step and identifies iterations without parsing private keys.
- Runtime dependency probes cover both conditional branches. The loop depends
  on its controlling condition, while consumers of the conditional value depend
  on its merge; the loop never depends on that future merge.
- Recovery of a partial second iteration preserves completed first-iteration
  history and committed effects. Independent process probes hard-kill workers
  at static and repeated waits, query history before recovery, then re-admit only
  immutable stored bytes in a new process. Memberships survive and the committed
  provider effect occurs once on each framework.
- Repeat waits use native iteration wait APIs directly, removing the old
  same-key nested durable callback deadlock. Native regression evidence reopens
  an old-style in-flight claim and preserves its step ID, revision, JSON input,
  input hash and absent membership. Root wait serialization is unchanged.
- Unsupported grouped fan-out, nested repeats, coordinated inference and
  repeat-local inference fallback fail before durable/provider writes. Authority,
  retry and provider outcome policies remain unchanged. Group coverage stays
  conservative; observing current members does not establish expansion closure.

## Verification

The complete Fuwen regression surface passes **1,666 executions**: core 314,
compiler 251, Baize 94 and adapter 174 on each framework. The initial full adapter
run had one malformed negative fan-out fixture per framework. Correcting its
bounded-list source and rerunning the full adapter project produced two clean
receipts. Those replace only the failed adapter receipts in the final total.

The package-only consumer passes **10 additional executions**, including the
public grouped-repeat source/compiler/bundle/adapter/query path. Its assets
contain no project references. All six resolved Fuwen/Zhinu candidate archives
match the task-local cache. Four Fuwen archives identify the production source
above; all eight packaged DLLs match the source build. Formatting and the
independent canonical JSON vector pass.

Staging preview.6 exposed body key length and legacy wait input-contract issues;
the final candidate uses the corrected body encoding and native preview.7.
The new consumer assertions initially used unsupported null propagation in
expression trees; explicit null guards compile and pass in the final receipts.
Staging/focused runs are not added to the final regression total.

Zhinu's corresponding ZG-6.2a record qualifies the native loop-control/iteration
wait APIs, full regression, package closure and precompiled legacy consumer.
The qualification commit also stabilizes an existing authorization test's lock
release scheduling; it changes no packaged authorization implementation.

## Remaining gates

See the [repeat profile](../../activity-groups-repeat-profile.md) for the exact
accepted grammar and runtime behavior. Next is **fan-out per-operation lowering**
with its own durability and process-loss evidence. Nested repeats, broader mixed
composition, grouped coordinated inference, repeat-local fallback, selection
closure, Linux CI, Fuwen legacy binaries and public dependency adoption remain
open. This checkpoint does not complete full ZG-6/FM-2, Q45 or ZG-7.
