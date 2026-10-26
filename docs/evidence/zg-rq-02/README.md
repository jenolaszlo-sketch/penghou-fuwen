# ZG-RQ-02: Fuwen evidence packet

Status: **remediation executed; hosted rehearsal attempt 1 failed closed and
attempt 2 passed after owner approval; REL-SQLITE-01 remains OPEN; publication
remains unauthorized.**

This is the Fuwen-side packet of the ZG-RQ-02 milestone (Q6 delta remediation of
ZG-RQ-01 findings RG-01, RG-02, RG-03). Shared, cross-repository documents are
mirrored here from the canonical Penghou.Zhinu packet
(`../Penghou.Zhinu/docs/evidence/zg-rq-02/`, branch `codex/zg-rq-02-zhinu`) with
a mirror note; Fuwen-specific evidence is native to this packet.

## Fuwen-specific evidence

| Item | Class | Location |
| --- | --- | --- |
| Native feed fault matrix (R15a-g, R09, R16, R18) | [LOCAL] | `local/feed-rehearsal-02.json` (valid); `local/feed-rehearsal-01.json` (INVALID, preserved) |
| R11 native dependency-pin targeted fixture | [LOCAL] | `local/feed-r11.json`, `tools/run-feed-r11.ps1` |
| Native rebuild equivalence (payload-identical) | [ARTIFACT INSPECTION] | `artifact-equivalence.md`, `local/native-equivalence.json`, `local/native-rebuild-02/` |
| Hosted rehearsal attempt 1 (REL-SQLITE-01, fail-closed) | [HOSTED] | `local/hosted-rehearsal-40308636700.json`, `-jobs.json`, `-windows-failure.log` |
| Hosted rehearsal attempt 2 (pass, approved checkpoint) | [HOSTED] | `local/hosted-rehearsal-40308636700-attempt2*.json`, `-windows-summary.log` |
| Pre-fix hosted failures | [HOSTED, preserved] | `local/hosted-first-failure.log`, `local/hosted-format-failure.log` |
| REL-SQLITE-01 stress qualification (64 executions) | [LOCAL] | `stress-qualification.md`, `stress/` |
| Scope proof (production delta empty) | [SOURCE INSPECTION] | `scope-check.json` |
| Workflow-boundary captures | [LOCAL] | `local/fuwen-workflow-boundaries.json`, `local/zhinu-workflow-boundaries.json` |
| Recovery records (interrupted state preserved) | [INSPECT] | `recovery/` |

## Mirrored shared documents (canonical copy in the Zhinu packet)

`git-binding.md`, `rg-01-zhinu.md`, `rg-02-environment.md`, `rg-03-fuwen.md`,
`rehearsal-matrix.md`, `permissions-review.md`, `known-findings.md`,
`handoff.md`, `zhinu-artifact-equivalence.md`.

## Key results

- **RG-03:** `release-qualification.yml` stages the pinned native candidate feed
  (source commit `bf33c05`, version `0.2.0-preview.10`, exactly seven packages,
  dependency pin `[0.1.0-preview.2]`) before any restore; `NuGet.config` maps
  the seven IDs exclusively to the staged feed, and the R16 case proves
  nuget.org is not considered (`NU1101`). The hosted `prepare-native-feed` job
  succeeded on both attempts.
- **RG-02:** the `nuget` environment requires owner approval
  (`required_reviewers`, `can_admins_bypass: false`); both exercised directions
  were observed live (attempt 1 skipped the gate on a failed qualification;
  attempt 2 paused, was approved, then ran only the echo step).
- **RG-01:** mirrored in `rg-01-zhinu.md`; the Fuwen publisher consumes only the
  ubuntu-validated package set and re-verifies every archive before
  `NuGet/login`.
- **REL-SQLITE-01 — OPEN:** attempted-1 recurrence preserved; attempt 2 passed
  with no change; 64/64 local batch passed; cause still unestablished.
- No tag was created, no package was published and no branch was merged at any
  point. The workflow-contract, formatting and fixture failures are preserved,
  not hidden (`observations.md`).
