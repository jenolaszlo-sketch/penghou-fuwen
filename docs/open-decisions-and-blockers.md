# Open decisions and blockers

Status checked 2026-09-28. This register distinguishes an unfinished
implementation from a product-policy decision, an owning-project dependency,
and a release validation gate. A pending decision blocks only the behavior
named in its row. The implementation status remains in the
[inference hardening plan](inference-hardening-v2-prep-plan-2026-09-27.md).

The corrective work has shipped in source, not as a new NuGet package. It
includes exact selected-executor preflight, typed model-tool validation,
stock Baize turns, a leaf budget ledger, protected successful tool results,
recovered-batch validation, and a typed static fallback for selected
definitive inference failures. These are partial contracts, not a claim that
all FI-00–FI-10 gates or V2 delivery are complete. The Release solution last
passed 768 tests per framework on .NET 8 and .NET 10 at commit `ddbd4f3`;
later documentation-only commits did not change runtime behavior.

## Product and host-policy calls to review

| ID | Missing decision and recommended default | Tradeoff and owner | What proceeds meanwhile | Unblock condition |
| --- | --- | --- | --- | --- |
| D-01 | **Grant revocation and recorded output:** reject a revoked resource grant before any new tool I/O. Deny replay of its protected payload unless the host explicitly authorizes recorded-run access; allow bounded metadata inspection. | Strong revocation may prevent recovery of a previously authorized run. **Host/product owner** chooses recorded-run access policy. | FI-04 grant admission, whole-batch authorization and host I/O checks can be implemented with the deny-by-default behavior. | Host records an explicit access/revocation policy and tests both new-I/O and recorded-output cases. |
| D-02 | **Existing in-flight journals:** reject an older unsupported coordinator/grant state by default. Inventory persisted consumers before promising migration. | Older work may need to finish on its existing runtime or be intentionally abandoned. **Product owner** identifies valuable in-flight histories; engineering owns any migration. | New grant-aware version and deterministic tests can proceed. | Inventory says no migration is needed, or identifies exact old state and an approved tested migration. |
| D-03 | **Protected-content retention:** host chooses retention and recorded-run access windows. Missing or expired recovery payload becomes an explicit unavailable-evidence outcome, never a reason to resubmit uncertain provider work. | Short retention limits recoverability; long retention increases exposure. **Host/product owner** sets policy. | FI-03 protected references, unavailable-state handling and private-marker tests can proceed. | A host policy and an expiry/revocation recovery test are recorded. |
| D-04 | **General authored handlers and human escalation:** keep the shipped static typed fallback; prototype the smallest bounded handler and review one executable scenario before expanding public syntax. Keep execution failure, evaluation and acceptance separate. | More expressive handlers add a new language and recovery contract. **Product owner** reviews the scenario; engineering proposes syntax. | Interrupted static-fallback recovery tests and failure normalization proceed. | Reviewed syntax and one bounded end-to-end scenario with recovery behavior. |

## Engineering decisions and work — no product approval needed

| ID | Open work and default direction | Evidence / completion condition |
| --- | --- | --- |
| E-01 | **FI-04 resource grants:** replace `name@version` pseudo-scopes with exact host grant references. Authorize the entire proposed batch before any tool I/O; bind grants, arguments and policy identity to durable recovery; recheck at host I/O. This is implementation work, not an external blocker. | [Grant contract](fi04-resource-grant-contract.md) and `FuwenInferenceCoordinator.cs` current `ScopeFor(tool)` call. Tests prove wrong resource and later unauthorized proposal cause zero tool I/O. |
| E-02 | **Grant identity placement:** keep host authority and grant policy in admission/runtime identity, distinct from reusable executable-plan semantics. Use a required versioned binding so older runtimes reject it; prove changed grants cannot resume as the same authorized batch. | ADR/API review, old-plan rejection, changed-grant recovery test. Escalate to the product owner only if a real portability requirement conflicts. |
| E-03 | **FI-03 recovery/privacy:** protect remaining prompt, context, argument, candidate and continuation paths; run the crash matrix and private-marker scan. | Protected-store tests show reuse, reconciliation or explicit ambiguous stop at each relevant crash point. |
| E-04 | **FI-04 protocol/type work:** extract validated transitions, normalize remaining failure cases, and complete bounded repair and one-call/coordinated consistency. | Pure transition tests and consumer tests at the actual adapter boundary. |
| E-05 | **FI-07 static fallback recovery:** prove interruption between recorded failure and `$fallback` disposition resumes without another provider call. | Durable restart test with exact provider call count and both retained records. |
| E-06 | **FI-05/06 usability:** finish provider provenance/multi-profile consumer proof and standalone bounded inspection walkthrough. Read-only inspection is already possible. | Isolated consumer runs from packed packages; query causes zero provider work. |

## Owning-project dependencies and validation gates

| ID | Status | Unblock condition |
| --- | --- | --- |
| X-01 | **Configured Baize strict prompt/total-token/cost ceilings:** strict admission remains closed because the selected endpoint cannot provide a trusted pre-call maximum-charge quote. Advisory mode is explicit opt-in, never an automatic downgrade. | A provider/host maximum-charge contract with exact endpoint/pricing identity and deterministic plus opt-in live proof. Leaf ledger and quote-port work continue independently. |
| X-02 | **Zhinu fenced admin commands:** read-only inspection can proceed; safe cancel/restart/resume commands need expected-state checks and idempotent receipts. | [Zhinu dependency ticket](zhinu-inference-dependencies.md) implemented and tested in the owning project. |
| X-03 | **Zhinu coordinated fan-out:** admission remains closed until child durable item scopes exist; shared parent reservation is a separate host/ledger dependency. | Two-item crash/reorder proof and concurrent shared-budget proof in the [dependency ticket](zhinu-inference-dependencies.md). |
| V-01 | **Release validation:** live-provider proof, independent human security review and an actual downstream pilot have not happened. Deterministic tests and AI review cannot close them. | Named evidence for each gate; do not infer completion from package-consumer tests. |
| H-01 | **Package publication:** intentionally on hold at the user's direction. | User explicitly resumes publication after docs and release gates are reviewed. |

V2 outcome-aware repair, comparative execution and evidence-informed proposals
remain deferred milestones in [the roadmap](roadmap.md), not hidden work in
this corrective release. Do not treat an explanatory plan comparison as reuse
authorization or a completed runtime decision.
