# REL-SQLITE-01 bounded stress qualification

Result: **PASS for this batch, 64/64 cases; REL-SQLITE-01 remains OPEN**.
This does not explain or erase either historical GitHub-hosted SQLite error 10.

Source: Fuwen qualified runtime `8c7da4cfbc882804d801fe0e8b073733e4ee563c`;
native `bf33c05cf2390af6924bbe3761e09f9e377c7a97`, seven unpublished preview.10
packages recorded by hash in `stress/environment.json`. Harness commit
`101643aacd107042fbc5767bd88e99fd0349442b`. The subsequent harness change in
`5ec24791b6107bab2605ecfe6c45436cd3b74aaa` only splits two initializer properties
across lines to satisfy hosted SDK 10.0.403 formatting; it changes no behavior.
`scope-check.json` proves no production source, package version, NuGet mappings,
or public API changes. Release scripts were under development during the batch;
they are not inputs to the already-built stress executable.

Windows 11 build 26200, NTFS D:, SDK 10.0.401, .NET runtimes 8.0.31 and 10.0.12.
SQLite native library under test: 3.53.3, its DLL hash retained. WAL, pooling=false,
busy timeout=5000ms. Diagnostics use Python SQLite 3.50.4 on separate copies;
this diagnostic version is not the production SQLite version. Free space and
filesystem receipts are retained. This is local Windows evidence, not a claim
to reproduce a GitHub windows-latest runner image or its ambient load.

32 serial test-host invocations: 16 per framework, each executing the existing
blocked-item a and b cases (64 cases total). Each case exercises both original
and replacement loss barriers; assertions still check durable membership,
completed history, parent/occurrence identity and distinct committed effects.
No retries, relaxed barriers, test delays or production corrections were added.
Unrelated offline fixture processing occurred during portions of the batch;
there was no concurrent stress test invocation or controlled concurrent-load
experiment. This batch establishes serial case execution, not host quiescence.

All 64 cases passed. All 64 run.db originals were retained after worker and test
host exit; copy-based `PRAGMA integrity_check` returned `ok` for every database.
Per-case block parameter, duration, framework, batch, stdout, original TRX,
worker start/termination timestamps, exit codes/stderr, file existence/sizes/
hashes, diagnostic-copy table counts and journal modes are in `stress/`.
Raw DB/WAL/SHM files remain local at
`D:/temp/activity-groups/zg-rq-02/stress-batch-01`; they are not in public Git.
No raw database cleanup or repair was performed. Normal non-stress tests retain
their original cleanup behavior. Do not delete raw evidence before review.

No production failure, environmental failure or inconclusive case was observed
in this batch. Separate release-fixture harness defects are recorded in
`observations.md`; they are not production failures or stress executions.
Historical REL-SQLITE-01 cause remains unestablished. A green batch does not meet
the documented cause-and-correction or concrete external-incident closure rule.

## Third occurrence during the hosted release rehearsal

The nonpublishing Fuwen rehearsal run `40308636700` (commit `6565822`,
windows-latest job `121524486103`, attempt 1, 2026-10-26T10:45:38Z) reproduced
the same failure class while executing the existing blocked-item "b" process-loss
case on .NET 10:

```text
Penghou.Zhinu.WorkflowPersistenceException : SQLite operation 'lease.recover' failed.
---- Microsoft.Data.Sqlite.SqliteException : SQLite Error 10: 'disk I/O error'.
   at Penghou.Zhinu.Sqlite.Persistence.Leases.SqliteLeaseRepository.RecoverExpiredLeasesAsync(...)
```

This is the third recorded occurrence (after the ZG-RQ-01 activity-groups
integration failure and the 2026-10-25 Fuwen run `40187614750`). The 64-execution
local batch above passed 64/64, so this recurrence is environment-dependent and
the cause remains unestablished. The hosted job log and run/job metadata are
retained verbatim in `local/hosted-rehearsal-40308636700-windows-failure.log`,
`local/hosted-rehearsal-40308636700.json` and
`local/hosted-rehearsal-40308636700-jobs.json`.

The failure is **fail-closed evidence for the release gate**: the windows
qualification failed, so the `handoff` and `authorization-probe` jobs were
skipped and no publication path ran. A subsequent attempt, if any, is recorded
separately and does not erase this observation. REL-SQLITE-01 remains **OPEN**.

## Attempt 2 outcome (separate evidence, 2026-10-26)

The rerun of run `40308636700` (attempt 2, windows job `121531587369`) passed:
all test assemblies green on both frameworks, including
`Penghou.Fuwen.Zhinu.Tests.dll` at 226/226 on net8.0 and net10.0 (attempt 1
failed in that assembly at blocked item "b"). The `handoff` job re-verified
every archive before requesting credentials, and the environment checkpoint was
approved by the owner and ran only its echo step. Run conclusion: **success**.

This changes the classification from "reproduced on the hosted runner" to
**intermittent/environment-dependent**: the same commit and the same test
passed on attempt 2 without any runtime, test or retry change. A green rerun is
**not** closure under the finding's rules (no established cause and correction,
and no concrete external incident). The recorded 64/64 local batch plus one
hosted failure and one hosted pass are all retained; REL-SQLITE-01 remains
**OPEN** and the release gate must keep refusing when the failure recurs. Raw
attempt-2 records: `local/hosted-rehearsal-40308636700-attempt2.json`,
`local/hosted-rehearsal-40308636700-attempt2-jobs.json`,
`local/hosted-rehearsal-40308636700-attempt2-pending-deployments.json`,
`local/hosted-rehearsal-40308636700-attempt2-approval.json`,
`local/hosted-rehearsal-40308636700-attempt2-windows-summary.log`.

Reproduction: check out the harness commit; prepare the exact pinned native
source using `eng/Prepare-ZhinuSourceCandidate.ps1`; retain the resulting manifest
and package hashes. Build `tests/Penghou.Fuwen.Zhinu.Tests` in Release for both
frameworks. Run `python eng/sqlite_stress.py --output D:/temp/activity-groups/<new-batch>`.
The output must not already exist. Every invocation sets a fresh TEMP/TMP/TMPDIR
under its batch directory and opts into `ZG_SQLITE_STRESS=1`. Preserve a newly
observed failure before any correction, rerun or cleanup; classify it using the
original exception chain and retained state, not its eventual rerun outcome.
