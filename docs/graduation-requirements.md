# Graduation requirements: preview → RC → stable

Status: accepted direction; none of the gates below are met yet. The current
packages (`0.1.0-preview.11`) are preview-grade: usable by downstream pilots
on public artifacts, with no stability promises. A 2026-09-27 corrective review
found defects in the coordinated-inference path; that capability remains limited
until the linked corrective contract gates pass.

## Versioning direction

Graduation moves forward in maturity: preview → release candidate → stable,
following SemVer and NuGet ordering. `alpha` is *earlier* than preview, not a
graduation target; do not rename the prerelease tag backward to signal
progress. If an experimental track is ever needed alongside a stable line,
give it a distinct prerelease label rather than reusing `preview`.

## Where graduation starts from

- Every public API across all four packages still lives in
  `PublicAPI.Unshipped.txt`; every `PublicAPI.Shipped.txt` baseline is empty.
  Nothing is frozen and everything may still break.
- All provider behavior is proven against deterministic fakes only. No test
  has ever touched a real model provider.
- The write-tool gate (CI-8) is closed by design. The current coordinated
  inference path has open corrective findings: protection of prompts/context/
  arguments/model outputs and hard pre-call guarantees for prompt, total-token,
  and cost budgets. Successful coordinated read-tool results now use a
  host-owned protected store; ordinary history retains checked references.
  The serialized evidence-report limit is enforced, with journal state governed
  separately. Corrective batches address cross-run identity, final aggregate
  settlement, currency mismatch, selected-executor preflight, and a hard
  completion-token capability gate. The historical CI completion entries do not close the remaining
  findings. Pricing-revision pinning remains host-owned, and evidence has no
  read-back API without a sink. Fan-out support must be evaluated against the
  current runtime and recovery contract rather than inferred from the old
  admission-rejection statement.
- Review is AI-only so far: no human security review and no external audit.

## RC exit criteria

Each item states how it is verified. RC ships only when all are met.

1. **API freeze.** All public baselines move from `Unshipped` to `Shipped`
   with zero unintended entries, a breaking-change policy is recorded
   (additive-only within a prerelease line; anything else needs a major or
   new contract version), and known-churn surfaces are stabilized — at
   minimum `FuwenZhinuExecutionPorts` construction (options record vs
   positional growth) and the `InferenceProtocolEvidence` shape.
   *Verified by: package validation on pack, PublicApiAnalyzer gates, and a
   diff review of Shipped baselines.*
2. **Live-provider conformance.** The Baize one-turn mapping, turn/tool
   usage reporting, normalization edge cases, and retry behavior are proven
   against at least one real provider in addition to the deterministic fakes.
   Recorded provider usage shapes become regression fixtures.
   *Verified by: a provider-conformance suite with recorded (redacted)
   fixtures running in CI.*
3. **Deferred-item rulings.** For each item below, either implement it or
   record an explicit "not in RC scope" ruling with rationale:
   - CI-8 write-tool gate (approval, idempotency/reconciliation,
     unknown-commitment, compensation, adversarial crash tests);
   - coordinated inference inside fan-out bodies (item-scoped nested-loop
     primitive, or keep admission rejection as the contract);
   - pricing-revision pinning (coordinator-enforced, or host-owned with
     conformance coverage of the unknown-pricing stop);
   - evidence read-back without a sink (query API, or sink-only documented
     as the contract).
4. **Guyabano pilot recorded.** The release checklist permits exactly one
   bootstrap preview before it; RC must close that gap with a bounded pilot
   running on released packages.
5. **Human security review.** A reviewer other than the author walks the
   coordinator, journal replay, ports, failure taxonomy, and evidence
   privacy paths, and signs off the threat model. AI review passes do not
   count toward this gate.
6. **Scoped definition of done.** The roadmap's open items are marked as RC
   blockers or backlog so "done" is decidable; CI (including the isolated
   consumer-proof job) is green on real runners for the release commit.

## Stable criteria

- One RC line ships and downstream consumers (Marang plus the second,
  product-neutral consumer) adopt released packages — not CI feed builds —
  without blocking defects.
- No open P0 defects against the RC surface; any deferred RC item from the
  list above is either closed or re-ratified with a new rationale.
- The changelog, capability matrix, and security guidance describe the
  stable contract, not the preview history.

## Non-goals

Graduation does not require arbitrary feature completeness, additional
complex-activity kinds (still gated on a second proven protocol), or live
provider dependencies in the default test suite. It requires frozen
contracts, proven provider behavior, resolved deferrals, and reviewed
security boundaries.
