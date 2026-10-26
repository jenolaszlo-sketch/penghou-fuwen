# Preserved failures and qualification limits

1. Recovered Fuwen fixture used `Parameters.ResolvedSourceCommit` under strict
   mode even when absent. The first local matrix incorrectly counted that
   harness exception as a rejection. `local/feed-rehearsal-01.json` is retained
   as **INVALID negative evidence**, not a production pass/failure. Corrected
   code checks key presence and the expected rejection message. The second
   matrix proves the real verifier handles the intended faults. Its positive
   fresh-cache restore succeeds and the absent-feed case returns NU1101,
   explicitly excluding nuget.org for the mapped native package.
2. New offline API mock initially used script-scoped variables that resolved in
   the called verifier script; the second attempt omitted the real API's `name`
   property. `api-offline-01/02` are preserved failed harness observations.
   Explicit fixture state and the completed response shape yield 19 passing
   scenarios in `api-offline-03`. No live API refusal is inferred from these.
3. Fuwen hosted run 40302339008 failed its positive clean-restore fixture after
   successful native source build, rejection probes and production feed checks.
   Its table truncated the exact restore error; initial workflow omitted an
   always-upload of that fixture receipt. The next change used portable slash
   paths and retained full errors and receipts. Run 40302665778 passed native
   preparation/fixture checks, so the first observation is a harness/platform
   path defect, with its exact original error text unavailable. The failed run
   and original log are preserved; do not claim an independently established
   deeper NuGet/runtime root cause.
4. Run 40302665778 then failed the mandatory formatting gate at the diagnostic
   initializer (two properties on one line). Local SDK 10.0.401 check had passed;
   hosted SDK 10.0.403 requested a newline. Commit 5ec2479 applies only that
   whitespace correction. This is a harness formatting defect, not production
   SQLite failure. No test or format gate was removed or relaxed.

5. Run 40303275879 failed the repository's workflow-contract test because that
   test still pinned the pre-remediation single-file publisher instead of the
   reusable release-qualification architecture. Commit 6565822 aligns the test
   with the new architecture. The failure was a stale test expectation, not a
   production or release-path defect.
6. Hosted rehearsal 40308636700 attempt 1 reproduced REL-SQLITE-01 on
   windows-latest (blocked item "b", `SQLite Error 10: 'disk I/O error'` at
   `lease.recover`); the failure log and run metadata were copied into `local/`
   before any rerun, and the failed qualification skipped the handoff and
   environment checkpoint (fail-closed). Attempt 2 of the same run and commit
   then passed all suites including 226/226 Zhinu-integration tests on both
   frameworks, and the run completed with an approved environment checkpoint.
   Both attempts are retained; the finding remains OPEN and a green rerun is
   not closure. See `stress-qualification.md`.
7. The worktree's staged feed was rebuilt after the 09:43 matrix for the
   native-equivalence comparison, so the current `artifacts/zhinu-candidate`
   pairs with `local/native-rebuild-02/zhinu-source-candidate-a0ed614f...json`,
   not the 09:43 manifest. The matrix JSON stays the historical record of that
   run; the targeted R11 fixture used the current pairing; the hosted
   prepare-native-feed job builds and verifies its own feed in the runner.
8. Some raw API captures in `local/` were initially written in UTF-16LE by the
   capture shell. They were normalized to UTF-8 (content unchanged) so reviewers
   and tooling can read them; no recorded values were edited.
9. The independent adversarial review corrected one provenance overclaim in the
   shared documents: `eng/test_workflow_boundaries.py` is executed locally, not
   in the hosted rehearsals. The affected sentences in `rg-01-zhinu.md`
   (mirror) and `rehearsal-matrix.md` (mirror) were corrected; the test's own
   output files already stated "STATIC WORKFLOW MUTATION; NOT HOSTED
   ENFORCEMENT".
10. The mirrored shared documents carried pre-existing em-dash mojibake
    introduced by earlier PowerShell 5.1 mirror writes (UTF-8 read as ANSI).
    It was corrected cosmetically; no content or claim changed. The canonical
    Zhinu copies were never affected.

All corrected attempts have distinct commits/run IDs or fresh fixture output
directories. None overwrites a failure, creates a tag, or publishes a package.
