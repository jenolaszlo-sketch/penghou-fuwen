# Penghou.Fuwen implementation and design review

Reviewed: 2026-09-13.

Scope: correctness, compiler/runtime contracts, Baize adapters, Zhinu durable execution, artifact publication, OOP/design patterns, usability, usefulness, and test coverage. This includes the recently completed Baize delivery.

## Assessment and verification

Release-hardening status (2026-09-13): R01-R05 are implemented with focused
regressions. R06-R12 remain curated follow-up work; the roadmap records the
parts that affect the first preview and the later architectural cleanup.

The compiler/admission/runtime separation is appropriate. Exact descriptor identities, canonical definitions, opaque admission receipts, bounded runtime values, explicit execution ports, and durable invocation evidence provide useful boundaries. Preserve these distinctions. The strongest opportunities are consistent value representation across adapters and more precise accounting/recovery contracts.

- Full solution tests passed: 330 logical tests on both .NET 8 and .NET 10, totaling 660 passing executions with zero failures or skips.
- Per framework: core 148, compiler 133, Baize 21, Zhinu 28.
- Temporary fake-provider probes outside the repository reproduced the list representation mismatch, prompt substitution problem, and missing-usage cost-policy behavior below. No paid provider calls were made.
- Numeric comparison probes initially appeared suspicious, but RuntimeValueValidator rejects the non-representable inputs before execution. They are not reported as reachable correctness defects.
- Python golden tests, live generation providers, and external publication services were not exercised in this review.
- No implementation files were modified. This document is the review deliverable.

## Correctness findings

### R01 — P1: Valid inference lists cannot be consumed by Zhinu fan-out

Status: resolved. Fan-out now normalizes any admitted list representation at
the typed execution boundary, and a durable Baize-to-Zhinu test proves replay
without reinvoking the provider.

Evidence: `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`, `ExtractOutputAsync`; `src/Penghou.Fuwen.Compiler/RuntimeValueValidator.cs`, JSON-list validation; `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, `ExecuteInferenceAsync` and `ExecuteFanOutAsync`.

Baize structured output is constructed with `RuntimeValue.FromJson`, including JSON arrays. Runtime validation accepts JsonRuntimeValue arrays for a declared ListType. Zhinu preserves that successful provider output in its execution envelope, but ExecuteFanOutAsync requires the source to be a ListRuntimeValue. The declared type validates while the downstream representation check rejects it.

Probe: fake Baize response `[1,2]`, output type List<Integer> bounded to five items. Execution and runtime validation succeed; the returned type is JsonRuntimeValue and fails the ListRuntimeValue check used by fan-out. The review confirmed this handoff and inspected the rejection path; it did not run a complete durable workflow for this probe.

Other execution ports returning JSON lists and list projections from JSON objects can encounter the same mismatch.

Recommendation: establish a shared typed normalization boundary for provider output, including nested/projection values, or let fan-out consume both validated list representations. Preserve artifact values during normalization. Add an admitted Baize-list-to-Zhinu-fan-out integration test, including replay and projected list input.

### R02 — P2: Prompt substitution rewrites literal argument content

Status: resolved. Prompt placeholders are expanded in one pass, so inserted
argument and context JSON is never scanned as template syntax.

Evidence: `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`, `CreateRequest`.

Template expansion replaces `{arguments}` first, then runs `{context}` replacement over the entire resulting string. Literal `{context}` text inside an argument is replaced with context JSON. This silently changes data supplied to the model; nonempty context can also insert unexpected JSON syntax into the serialized argument region.

Reproduced: template `ARGS {arguments} CTX {context}` and argument text `literal {context}` with empty context. The provider receives `ARGS {"text":"literal {}"} CTX {}`.

Recommendation: expand placeholders only in the original template using a single-pass renderer or pre-parsed segments. Never rescan inserted values. Test placeholder-like text in arguments and context, including all supported template forms.

### R03 — P2: Missing usage allows retries despite a configured cost ceiling

Status: resolved conservatively. Missing or partial billable usage is recorded
as unknown cost and prevents another priced attempt when a ceiling is active.

Evidence: `BaizeTokenPricing.Calculate`, `ShouldRetry`, and cost aggregation in `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`.

Pricing is required on every endpoint when a ceiling is configured, but reported token usage is not. If usage is absent, cost remains unknown while the retry comparison uses the accumulated numeric value, initially zero. Partial usage treats the missing component as zero. Another attempt can therefore be authorized without evidence that prior spending remains within the budget.

Reproduced: a one-microunit ceiling, trusted nonzero pricing, two permitted representation attempts, and a fake provider returning schema-invalid output without usage. Both calls execute and aggregate cost remains null.

Recommendation: make unknown/partial usage an explicit accounting state. Under an enforced retry ceiling, stop or reserve a conservative host-defined estimate before another call. Distinguish a post-response retry threshold from a strict maximum-spend guarantee: the current API cannot prevent a single completed call exceeding the threshold. Add missing/partial usage tests.

### R04 — P2: Zhinu drops inference evidence when success processing fails

Status: resolved. Available inference evidence is retained in the durable
failure envelope when post-provider validation or publication fails.

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, the catch around `buildSuccess(result)` inside `ExecuteProviderAsync`.

A provider can return successful inference with usage/cost evidence, after which output validation or artifact receipt registration fails. The catch creates a failed NodeExecutionEnvelope without preserving that inference evidence. The durable record loses provider/accounting information for an operation that ran. The branch handling an explicit failed InferenceExecutionResult does preserve its evidence.

Status: code-inspection finding; no durable publication-failure probe was run.

Recommendation: retain available inference evidence on every terminal path after a provider result exists, while keeping failed-envelope publication semantics explicit. Test successful provider execution followed by rejected output or publication, and inspect the persisted failure envelope.

### R05 — P2: Generation polling follows replacement handles without checking identity

Status: resolved. Submission must match the configured provider, endpoint, and
model; polling pins those identities plus the operation identity while
permitting opaque continuation metadata to refresh.

Evidence: `src/Penghou.Fuwen.Baize/BaizeGenerationInferenceExecutor.cs`, `ExecuteDurablyAsync`.

Each poll uses the handle from the latest response. The adapter does not retain the submitted handle's stable identity and reject a different identity in subsequent responses. A misbehaving client/decorator can redirect later polling while Fuwen attributes the result to the original invocation. This weakens the documented pinned-operation behavior.

Status: code-inspection contract-hardening finding. The client is a trusted host dependency; no real provider redirect was observed.

Recommendation: validate stable operation identity from submission onward. If Baize permits refreshed continuation metadata, distinguish it from provider/endpoint/operation identity. Test a synthetic mismatched handle. Verify request modality against the binding where possible, or document modality evidence as host-declared rather than validated result provenance.

## Design, OOP, and usability improvements

### R06 — Normalize runtime values through a shared typed boundary

JsonRuntimeValue, ObjectRuntimeValue, and ListRuntimeValue serve useful JSON and artifact needs, but consumers currently must understand representation distinctions beyond the admitted FuwenType. R01 demonstrates the incompatibility.

Recommendation: share normalization between adapters and interpreter. Keep normalization, validation, and wire serialization explicit and separate. Avoid provider-specific branches in fan-out. Preserve immutable value objects and nominal artifact types.

### R07 — Separate interpreter responsibilities internally

FuwenZhinuSequentialInterpreter handles scheduling, projection, conditions, fan-out, provider invocation/retry, receipt publication, observation, and envelope validation. The structured Baize executor combines rendering, schema projection, repair, validation, accounting, and routing.

Recommendation: extract cohesive internal collaborators where they support independent contracts: typed value normalization, prompt rendering, outcome/evidence construction, and durable envelope validation. Keep the public facade and execution ports small. Composition is sufficient; a public hierarchy or interface for every helper is unnecessary.

### R08 — Make accounting and generation recovery contracts explicit

Generation cost resolution is optional and occurs after provider completion. Earlier output-count rejection can omit cost even when generation incurred it. Polling timeout is cooperative with client cancellation; it does not establish that a remote operation stopped. Replay depends on stable host request mapping and provider idempotency retention.

Recommendation: distinguish known, estimated, incomplete, and unavailable cost, and remote operation state from local polling failure. Consider durable provider operation identity for reconciliation. Specify idempotency retention/request-stability requirements and publication replay semantics. Test timeout-after-submission and crash-between-generation-and-publication with stateful fakes.

### R09 — Bound raw provider output before parsing and repair

Status: resolved. Trusted profiles now bound raw and repaired UTF-8 output
before parsing/runtime-value construction, and repair-adapter contract failures
remain typed provider-output failures.

Structured output is parsed and potentially repaired before the bounded runtime-value constructor enforces its final contract. Large provider strings can incur substantial transient work before rejection. Injected repair pipelines can also return malformed or oversized repaired output.

Recommendation: enforce a raw response budget before parsing/repair and carry it through repaired output. Map repair contract failures accurately instead of generic provider failure. Test oversized raw output and invalid repair results. Keep final schema validation after repair.

### R10 — Improve host setup and authoring feedback

Exact descriptor/admission/runtime identities are valuable but require significant host wiring. A minimal complete example should cover source, catalogue admission, durable execution, and replay with structured inference and an artifact result.

Recommendation: add setup diagnostics for configured routes and required descriptors before execution; reject unsupported adapter shapes at registration where possible. Include cancellation, unknown-cost failure, fan-out normalization, and source-mapped runtime failures in examples. Preserve the distinction between compilation, authorization, and execution.

### R11 — Extend cross-adapter conformance

Status: resolved for the first preview. A documented compact matrix and shared
runtime-value result test cover primitive, object, list, optional, and artifact
representations; durable Baize/Zhinu tests cover replay, projection, receipts,
evidence, and failure paths.

The existing durable Baize vertical is useful but does not cover all combinations of provider shapes and downstream control flow. Unit success can coexist with an invalid handoff, as R01 shows.

Recommendation: reuse a compact execution-port conformance matrix for primitive/object/list/optional/artifact results, projections, replay, invalid receipts, evidence retention, and failed output. Apply it to synthetic ports and Baize. Test observable contracts rather than duplicating implementation details.

### R12 — Specify the generated-asset publisher contract precisely

Status: resolved for the first preview. The trusted boundary now specifies
ordered batches, exact-byte verification, atomic-or-resumable publication, and
idempotent replay; tests cover ordering and recovery after ambiguous partial
publication.

The generation adapter checks receipt count, operation key, and artifact descriptor. It cannot establish that each receipt matches the correct bytes or that external content is durable; those guarantees belong to IBaizeGeneratedAssetPublisher. Repeated/reordered receipt semantics need definition.

Recommendation: document the trusted publisher contract and add a reusable conformance fixture for idempotent batches, stable ordering, partial failures, and content verification. Keep remote fetching and verification outside compiler/core contracts.

## Suggested implementation order

1. Fix list/fan-out compatibility and add a durable integration regression (R01).
2. Fix prompt rendering and unknown-cost retry policy (R02–R03).
3. Preserve failure-path evidence and validate generation identity (R04–R05).
4. Consolidate normalization/outcome helpers and improve bounds, recovery documentation, and host examples (R06–R12).
