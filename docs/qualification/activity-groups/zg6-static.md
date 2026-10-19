# ZG-6.1 — static Fuwen activity-group integration

Date: 2026-10-19. Status: **passed for the bounded static profile** on Windows,
.NET 8/10. Full FM/ZG remains open. Packages are unpublished.

Production/package source: Fuwen `cab1d71cbce67f75b9cf5711403061fa8daeea67`;
Zhinu `fd1875886e67e3aacd9c03c5689238788575d1e5`. Candidate versions are Fuwen
`0.1.0-preview.14` and Zhinu `0.2.0-preview.5`. The companion qualification
commit adds evidence and a package-only consumer test; it changes no packaged
production implementation. [Original package pins](zhinu-static-candidate.json) and the
[evidence ledger](zg6-static.json) record exact hashes and test receipts.

## Qualified behavior

- Real source compilation expands named local macros and logical groups into
  ordinary v4 IR. Nested invocations retain explicit parent groups and private
  local names. Runtime bindings are substituted structurally, never reparsed.
  Compiler tests cover missing/extra arguments, type/scope errors, direct and
  indirect capture protection, duplicate aliases, recursion, transitive
  fingerprints and expansion limits. Historical IR/golden checks still pass.
- The immutable companion pins hierarchy and safe provenance separately from
  ordinary execution identity. Grouped admission and registration bind the exact
  bundle; storage atomically publishes verified content-addressed files. Reopen,
  concurrent identical writes, corrupt contents and untrusted keys are tested.
- Native memberships preserve sibling identity, explicit parentage and direct
  innermost ownership. Work and Control roles remain separate. The public step
  mapper matches every emitted step in the fixtures. `SupervisedCoding`, `TDD`
  and `AlienProcess42` use the same generic implementation.
- A signal wait retains the same step, revision and group membership after
  interruption/reopen. The independent process probe hard-kills a worker after
  provider completion and durable wait materialization. A separate public query
  reads the hierarchy before recovery; a new worker re-admits only stored
  expansion bytes, resumes the wait, preserves occurrence/parent identities and
  leaves the committed fake-provider effect count at one. Both frameworks pass.
- Missing companion/store capability and grouped dynamic shapes fail before
  storage/provider calls. Native group capability checks remain authoritative.
  Source spans stay in the Fuwen bundle; Zhinu retains generic macro identity
  and invocation provenance. Mechanical state is not a domain acceptance verdict.

## Verification

Fuwen's complete regression surface passes **1,652 executions** across eight
project/framework receipts: core 314, compiler 251, adapter 167 and Baize 94 per
framework. This includes 58 new executions. The initial full run exposed two
release-document assertions tied to preview.13; adding preview.14 candidate notes
and checking the actual candidate's heading corrected those checks. The passing
compiler rerun replaces the failed compiler receipts in the final total; other
project receipts are retained from the full run.

The package-only review consumer passes **8 further executions** (4 per
framework), including the new public grouped-source/compiler/store/adapter/query
path. Its assets have no project references; resolved Fuwen and Zhinu candidate
packages match the pinned archive hashes in the task-local cache. Four Fuwen
packages contain eight binaries matching the exact source build. Formatting and
the independent canonical JSON vector pass. The ledger records these checks.

Zhinu passes **1,901 tests**, formatting, its seven-package consumer closure and
the precompiled legacy-consumer probe on both frameworks, including schema 5→9
migration and old-worker rejection. See its corresponding ZG-6.1 qualification.
No new schema or occurrence identity codec is introduced by this integration.

## Remaining gates

See the [static profile](../../activity-groups-static-profile.md) for grammar,
adoption and recovery instructions. Grouped repeat/fan-out and coordinated
inference are deliberately rejected in this profile. Per-child dynamic lowering,
selection/closure evidence, broader parameter forms, a dedicated expansion CLI,
Fuwen precompiled legacy binaries and Linux/public-package qualification remain
open. In particular, Q45, full ZG-6/FM-2 and ZG-7 are **not complete**. Existing
CI has no published candidate dependency feed yet; local Windows qualification
does not establish CI or publication readiness.
