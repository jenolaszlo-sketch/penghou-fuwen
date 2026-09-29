# Ideas for later

These proposals from the 2026-09-29 external review are not current delivery
commitments. Revisit each when a consumer demonstrates the need and the
versioned execution contract is clear. The accepted follow-on work is in the
[roadmap](roadmap.md#follow-on-candidates-from-the-2026-09-29-review).

| Proposal | Why it is parked | Revisit when |
| --- | --- | --- |
| Named `limits profile` syntax | A short alias can hide the effective ceiling, currency, and policy revision unless resolution is pinned into admission and plan identity. Existing explicit source and host limits remain inspectable. | A repeated authored limit set causes real maintenance cost and catalogue resolution can explain exact bounds and provenance. |
| Shared `InferenceTurnExecutorBase` class | One stock Baize adapter does not establish a stable inheritance contract for other providers. Conformance tests and small helpers are easier to evolve. | At least two independent adapters duplicate the same validated preflight or mapping behavior. |
| `wait ... timeout ... fallback default_value` | Waits already support an optional timeout. A default value could silently convert a missing human decision into approval or a normal result. | A reviewed typed timeout disposition and one concrete workflow need a distinct default branch. |
| Source-level `fallback_model` clause | Baize and host policy own model selection; a source clause would need per-model capability, quote, pricing, identity, and ambiguous-operation rules. | A consumer needs authored model choice that cannot be expressed by a pinned host profile and can prove safe recovery. |
| Failed tool-result status in model history | The current coordinator stops on failed tool execution, so only successful tool results enter conversation history. The Baize adapter's `succeeded: true` matches that invariant. | Recoverable tool failures become an explicit, versioned protocol feature with persisted status and replay tests. |

Do not settle usage above a trusted maximum quote as a successful operation.
The coordinator retains that reservation as uncertain, and the ledger can then
finalize without releasing its capacity. Recording the excess as ordinary
settled usage would weaken the strict reservation guarantee.
