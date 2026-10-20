# REL-SQLITE-01: Windows I/O failure after worker termination

Status: **open reliability follow-up; cause unestablished**. Recorded 2026-10-20.
This is separate from the four fixed integration defects and from the decision
to publish preview.20. A successful rerun does not close this incident.

## Observed evidence

In [integration CI 39408318656](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/39408318656),
attempt 1 failed the .NET 10 case
`Explicit_selection_process_loss_retires_partial_history_and_recovers_durable_replacement(blockedItem: "b")`.
After killing and awaiting the worker, the parent called `RestartStepAsync`.
Lease initialization failed at `lease.recover` with a `WorkflowPersistenceException`
wrapping SQLite error 10, `disk I/O error`. The log did not establish an extended
SQLite error code or a filesystem, VFS, hardware, harness or runtime cause.

The .NET 8 Windows cases and the Linux producer passed. A local Windows recheck
passed both barriers on both frameworks, four additional executions. Attempt 2
reran the failed/dependent CI jobs with identical source and passed all four
platform jobs. No runtime or test I/O retry was added.

The [sealed integration report](../qualification/activity-groups/integration-second-pass.md)
and [ledger](../qualification/activity-groups/integration-second-pass.json) retain:

- failed Windows artifact ID `12259015486`, status, log and test receipts;
- successful Windows artifact ID `12258452559`, downloaded by ID because the
  attempts share a display name;
- the separate local recheck, excluded from the 1,770 regression total;
- runtime source `eca1213796d9fa4ad08423e2691cb409c892f63b`, qualification harness
  `3d8ee2b9573bce350f4c05584da26eaa167dbca4`, and native
  `bf33c05cf2390af6924bbe3761e09f9e377c7a97` / preview.10 / schema 10.

The failed test's cleanup deleted its temporary database. There is no retained
database from which to establish corruption or inspect the failed WAL state.
The current evidence establishes a single failure and subsequent passes; it
does not establish either a recovery race or environmental innocence.

## Recurrence response

On another comparable failure, retain the failing source and exact package
archives before modifying them. Capture the exception chain, SQLite primary
and extended codes, SQLite/native-library versions, OS/runner image, filesystem,
free space, pooling/WAL/busy-timeout settings, worker exit status and timestamps
around the barrier, termination, connection open and failed operation.

Before cleanup or database repair, preserve a coherent diagnostic copy of the
database and any WAL/SHM files after workers have exited. Record file existence,
sizes and hashes; preserve stderr and the original TRX. Run integrity diagnostics
on a separate copy. Treat any retained database as potentially containing
workflow payloads; keep it outside the public evidence ledger unless reviewed.

Run a bounded Windows stress batch over the existing blocked-item `a` and `b`
cases, each retaining both initial and replacement loss barriers. Use 16
repetitions per case/framework: 64 case executions across .NET 8/10. Use a new
database and output directory for every repetition on `D:/temp/activity-groups`.
Start serially with the current WAL/pooling settings, then isolate concurrent
runner load as a separate variable. Keep each failed receipt and any completed
history even if later iterations pass. This batch has **not** been run or added
to the qualified totals; it is triggered by recurrence or a separate request.

Recheck durable selection membership, completed history, parent occurrence
identity and exactly-once committed provider observations after recovery. A test
retry, delay, database replacement, relaxed barrier or blanket SQLite retry is
not a root-cause fix. Any proposed retry must establish which operation is safe
to repeat after uncertain transaction completion.

## Closure

Close this follow-up only with a reproducible cause and qualified correction,
or concrete external incident evidence with an explicit disposition of the
remaining runtime risk. Record source, artifacts and stress scope separately
from the original preview.20 checkpoint. A further unexplained recurrence
requires release triage before promoting additional builds.
