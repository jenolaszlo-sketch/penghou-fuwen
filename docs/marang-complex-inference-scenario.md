# Marang complex-inference planning scenario

Status: checked-in CI-0 scenario; descriptive contract only. This document does
not claim that IR v9 syntax, the complex-inference coordinator, or the Marang
adapter already exists.

## Purpose and boundary

This is the deterministic consumer proof for the first complex activity: one
logical Fuwen `infer` node used by a Marang-supervised planning workflow. It is
product-neutral at the Fuwen boundary. Marang authenticates and authorizes the
remote request, selects an immutable compiled definition, obtains host
preflight/admission, and supervises a Zhinu run. It does not interpret the
internal model/tool protocol. Zhinu owns durable execution and recovery;
Baize owns normalized model turns; a host-owned exact read-tool port executes
the admitted tools.

The workflow produces a typed planning result and a typed proposal for any
external mutation. It never commits, pushes, publishes, deploys, sends a
message, changes workflow state, or otherwise performs a side effect inside
the inference activity.

## Scenario input

The supervisor submits one immutable request, after Marang authentication and
workspace authorization:

| Field | Type | Fixed scenario value |
| --- | --- | --- |
| objective | `string` | `"Prepare a bounded implementation plan for the requested change."` |
| acceptanceCriteria | `list<string>` | `[`"identify affected components"`, `"identify verification"`, `"separate proposed mutation from execution"`]` |
| workspace | opaque `WorkspaceReference` | host-resolved reference; no ambient path crosses the Fuwen boundary |
| revision | `string` | `"main@scenario-fixed-revision"` |
| constraints | `list<string>` | `[`"read-only inspection"`, `"no credentials"`, `"no external writes"`]` |
| requestedChange | `string` | `"Add bounded complex-inference planning support without changing the public write boundary."` |

The exact values above are fixture data, not a required Marang request DTO.
The host binds the workspace and revision to immutable context/artifact
evidence before execution. Inputs are detached typed runtime values and are
included in the effective request identity.

## Authored workflow meaning

The authored workflow contains one logical `infer` node, followed by explicit
workflow-visible handling of its result. The following is an illustrative
shape, not committed syntax and not an executable example:

```text
infer planning : PlanningResult
  prompt = registered "marang/planning/v1"
  bindings = objective, acceptanceCriteria, requestedChange, constraints
  context = workspaceRevision
  tools = [repo.readFile@1, repo.search@1, graph.impact@1]
  limits = {
    maxTurns: 3,
    maxModelCalls: 3,
    maxToolCalls: 4,
    maxPromptTokens: 12000,
    maxCompletionTokens: 3000,
    maxTotalTokens: 15000,
    maxDuration: 90s,
    maxToolBytes: 24000,
    maxEvidenceBytes: 32000
  }

return planning
```

The names and syntax are placeholders for the CI-2 contract ADR and parser
work. Until then, the scenario is a semantic fixture: it must not be copied
into source and assumed to compile. Existing v3–v8 one-call plans retain their
current bytes and behavior.

## Exact read-tool allowlist

The model-visible set and executable host set are exactly the following
versioned descriptors. No legacy default tools are inherited, and a null or
unknown descriptor is a preflight failure.

| Descriptor | Typed input | Typed output | Scope and bounds |
| --- | --- | --- | --- |
| `repo.readFile@1#scenario` | `{ path: string, startLine: int32?, endLine: int32? }` | `{ path: string, revision: string, content: string, startLine: int32, endLine: int32, truncated: bool }` | authorized workspace/revision only; path ≤ 256 UTF-8 bytes; at most 200 lines and 16 KiB result |
| `repo.search@1#scenario` | `{ query: string, pathPrefix: string?, maxResults: int32 }` | `{ matches: list<{ path: string, line: int32, preview: string }>, truncated: bool }` | authorized workspace/revision only; query ≤ 256 bytes; `maxResults` ≤ 20; result ≤ 8 KiB |
| `graph.impact@1#scenario` | `{ symbol: string, direction: enum<callers|callees>, maxNodes: int32 }` | `{ nodes: list<{ id: string, label: string, kind: string }>, truncated: bool }` | authorized graph snapshot only; `maxNodes` ≤ 20; result ≤ 8 KiB |

All three descriptors are effect-free/read-only for this scenario. The host
still authorizes each call at execution time, applies cancellation and
deadlines, validates exact argument/result types, and records an operation
identity. A model-proposed tool outside this table is rejected before tool
execution. Tool results are untrusted data and may contain prompt injection;
they are returned as bounded typed data, not policy.

## Typed result

The successful result is the nominal `PlanningResult` value:

```text
PlanningResult {
  summary: string,
  affectedComponents: list<
    { path: string, reason: string, confidence: enum<low|medium|high> }
  >,
  verification: list<
    { command: string, purpose: string }
  >,
  proposedMutation: {
    kind: enum<edit|commit|publish|deploy|other>,
    description: string,
    affectedPaths: list<string>,
    requiresExplicitActivity: bool
  }?,
  assumptions: list<string>
}
```

The final schema requires `requiresExplicitActivity == true` whenever
`proposedMutation` is present. It does not authorize the mutation. A typed
failure is returned if the model asks to perform an external effect, omits
required fields, exceeds bounds, or produces an invalid representation after
the bounded repair/validation path.

## Proposed v9 bounds and effective admission

The fixture requests the finite source limits below. The host ceiling is
`maxTurns=4`, `maxModelCalls=4`, `maxToolCalls=6`, `maxTotalTokens=18000`,
`maxDuration=120s`, `maxToolBytes=32000`, and `maxEvidenceBytes=40000`.
The effective limits are the component-wise minimum; source cannot expand a
host ceiling:

| Dimension | Source request | Host ceiling | Effective limit |
| --- | ---: | ---: | ---: |
| protocol turns | 3 | 4 | 3 |
| model calls | 3 | 4 | 3 |
| tool calls | 4 | 6 | 4 |
| prompt tokens | 12,000 | 16,000 | 12,000 |
| completion tokens | 3,000 | 4,000 | 3,000 |
| total tokens | 15,000 | 18,000 | 15,000 |
| duration | 90 s | 120 s | 90 s |
| tool argument/result bytes | 24,000 | 32,000 | 24,000 |
| retained evidence bytes | 32,000 | 40,000 | 32,000 |

No cost ceiling is used in this credential-free fixture. A production host may
add one with an explicit currency and pricing revision. Unknown usage or
pricing is not zero; if affordability cannot be proven, the coordinator stops
before the next paid operation with a normalized budget outcome.

Preflight must prove prompt support, context delivery, structured output,
exact tool calls, all effective limits, protocol revision, and resumable or
reconcilable operations before registration or paid work. A failed preflight
has no model call, tool call, stored registration, or admission receipt.

## Deterministic model/tool trace

The fake model is selected by a pinned provider/runtime manifest and receives
only the exact prompt, detached bindings, admitted context representation, and
the three listed tools. It returns the following deterministic sequence:

| Ordinal | Operation | Deterministic request/result | Required journal assertion |
| ---: | --- | --- | --- |
| 1 | model turn | request digest `m1`; proposes `repo.search@1#scenario` with `{query:"InferenceExecutionRequest", pathPrefix:"src", maxResults:10}` | model operation `/model/0001` is claimed once; usage and provider/runtime revision recorded |
| 2 | read tool | returns two bounded matches: `src/.../InferenceExecutionRequest.cs:1` and `src/.../ExecutionPorts.cs:1` | tool operation `/tool/0001/model-call-1`; exact descriptor, typed args/result, scope, digest, and outcome recorded |
| 3 | model turn | request includes the bounded tool result; proposes `repo.readFile@1#scenario` for the first match, lines 1–80 | model operation `/model/0002`; prior tool result is referenced, not silently re-fetched |
| 4 | read tool | returns the requested bounded source excerpt at the pinned revision | tool operation `/tool/0002/model-call-2`; exact result identity and byte count recorded |
| 5 | model turn | returns a typed `PlanningResult` with affected components, verification commands, assumptions, and `proposedMutation.requiresExplicitActivity=true` | model operation `/model/0003`; final representation and declared-schema validation recorded |

The trace uses two tool calls and three model calls, within every effective
bound. The model does not call `graph.impact` in the success fixture; its
presence in the exact allowlist proves that unused admitted tools remain
bounded and auditable. The fake responses are stable test fixtures, not
provider behavior or a promise about generated text.

## Failure and recovery checkpoints

The conformance harness runs the same scenario with a crash or cancellation at
each checkpoint. A recovered run reuses completed operations by their stable
interaction/operation identity, reconciles an ambiguous provider operation, or
returns a typed operator-action failure. It never silently starts a second paid
operation with a new identity.

| Checkpoint | Injected condition | Expected result after recovery |
| --- | --- | --- |
| before model submission | worker crashes after journal claim | same `/model/0001` is retried/reconciled under the same key, subject to provider safety |
| after model handle, before completion | acknowledgement is lost | provider handle is reconciled; no second submission |
| after model completion, before result persistence | worker crashes | completed `/model/0001` is reused; proceed to `/tool/0001/model-call-1` |
| after tool claim, before tool completion | host process restarts | exact tool operation is reconciled or returns typed ambiguity; no duplicate claim with a new key |
| after tool result persistence | worker crashes | persisted typed result is replayed into model turn 2 |
| after model 2 completion | cancellation arrives | cancellation is durable; no model 3 or further tool call is issued |
| before final validation | malformed representation fixture | bounded representation repair may run; schema validation remains mandatory |
| after final validation, before node completion | worker crashes | validated result/evidence is replayed; enclosing workflow sees one logical result |
| fence loss at any boundary | stale worker attempts continuation | Zhinu rejects the stale fence; a current worker resumes from the journal |

Forks with the same execution fingerprint may reuse explicitly authorized
completed evidence under the existing fork policy. A changed input, revision,
plan fingerprint, or protocol/runtime admission identity starts a new
interaction and cannot reuse the old paid-operation identity implicitly.

## Evidence assertions

The credential-free evidence view must prove, without raw chain-of-thought or
unrestricted sensitive payloads:

- immutable plan/execution fingerprint, structural/runtime path, attempt,
  protocol revision, provider/model/runtime identity, and effective bounds;
- exact admitted prompt form, context snapshot/payload digest, and the three
  exact tool descriptors;
- interaction identity and each operation identity in ordinal order;
- normalized request/result digests, bounded byte counts, usage quality,
  elapsed time, cost quality (unknown in this fixture), and terminal outcome;
- tool authorization scope and typed argument/result identities;
- validation/repair disposition and the final `PlanningResult` schema result;
- recovery disposition for any injected crash, cancellation, fence loss, or
  ambiguous operation;
- explicit statement that no external side effect was committed.

The evidence may contain protected references for authorized payload access,
but ordinary status/result views expose summaries, digests, and typed artifact
references only. Hongxian or Siming may receive a correlation copy; neither is
the execution journal or a prerequisite for correctness.

## Explicit side-effect promotion

The successful planning result can contain a proposed mutation, for example
“update the implementation plan documentation.” That value is data, not an
instruction to the inference coordinator. Marang presents it as a proposal and
requires a separate explicit, authenticated workflow activity for promotion:

```text
PlanningResult.proposedMutation
  -> supervisor review / policy decision
  -> explicit authorized Fuwen activity (future implementation step)
  -> host-owned write/commit/publish operation
  -> separate admission, idempotency, evidence, and recovery contract
```

The initial complex-inference release has no write-capable internal tool. A
request to edit a file, commit, push, publish, deploy, send, or mutate workflow
state from inside `infer` is a normalized unsupported-effect failure (or a
typed proposal if the model returns it as data). The write-tool gate remains
closed until a separate ADR proves approval, idempotency or reconciliation,
unknown-commitment handling, compensation/forward recovery, and adversarial
crash behavior.

## CI-0 assertions

The checked-in scenario is accepted when later implementation work can prove:

1. the scenario has one logical `infer` node and no authored protocol loop;
2. preflight/admission rejects unsupported combinations before registration or
   paid work;
3. only the exact three descriptors are model-visible and executable;
4. effective v9 bounds are finite and source limits never widen host limits;
5. the deterministic trace and failure checkpoints produce the operation and
   evidence identities above;
6. replay, restart, fence loss, cancellation, and changed-fingerprint fork
   behavior preserve the stated recovery rules; and
7. any external effect is returned as typed data and promoted only by an
   explicit separately authorized activity.

Until those proofs exist, this document is a reviewable scenario and test
oracle, not an assertion that the implementation is complete.
