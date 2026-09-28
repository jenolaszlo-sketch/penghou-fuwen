# Capability matrix

This matrix describes the current source tree following `0.1.0-preview.11`;
post-tag hardening is listed under “Unreleased” in the changelog. A corrective
source review on 2026-09-27 found defects in bounded coordinated inference.
Until the corrective work in the [implementation plan](complex-activities-implementation-plan.md)
passes its gates, coordinated inference is **limited/experimental**, even where
its protocol path has tests. “Supported” means the checked-in compiler and
relevant adapter have conformance or recovery coverage for the stated boundary;
it does not mean a host has granted authority, supplied descriptors, configured
credentials, or accepted operational risk.

| Surface | Status | Current boundary |
| --- | --- | --- |
| Schemas, enums, capabilities, typed bindings and returns | Supported | Exact catalogue metadata remains authoritative. |
| Context, activity and inference nodes | Supported | Callables require exact descriptors and host admission. |
| Conditionals and value-producing merges | Supported | Branches have closed scope; only explicit merges escape values. |
| Keyed fan-out | Supported | Bounded, source-order aggregation with stable non-null keys. |
| Repeat | Supported | Positive static bound, explicit state/update/break and durable iteration identity. |
| Checkpoint and external wait | Supported | Typed durable interaction gates; presentation remains host-owned. |
| Inline prompts and registered prompt aliases | Supported | Typed bindings; registered templates are resolved and rendered by the host. |
| Inference tools | Limited | Tool declarations, turn/tool contracts, a stock Baize turn executor, and a durable coordinator exist. Exact model-facing schemas are derived from admitted callable signatures and preflighted against configured Baize bindings; native assistant call IDs and tool-result descriptors survive continuation. Endpoints must support combined native tools and structured output; otherwise preflight and submission reject the route. Tool operation keys have fixed length. Coordinated registration rejects duplicate provider-visible tool names; exhausted allowances remove tools from finalization requests; oversized proposal batches fail before tool I/O. Successful coordinated read-tool results require a host-owned durable protected-payload store; ordinary history retains verified references. Exact trusted callable argument names/types are checked before tool I/O and result types before storage. Resource grants, broader provider-shape coverage, and protection of other sensitive paths await corrective gates. |
| Bounded complex inference protocol | Limited / corrective work open | Zhinu contains a durable model/tool/result loop and replay support. Run/request identity, final usage/cost settlement, currency checks, selected-executor preflight, serialized evidence caps, and protected successful tool results are implemented. Older in-flight coordinator state needs migration or an explicit stop. See the corrective status and plan below. |
| Inference limits | Partial; corrective work open | Authored bounds are fingerprinted and checked during protocol execution. Final-turn settlement, currency matching, mixed pricing revisions, overflow handling, and serialized evidence-report size fail closed. Currency-free host-only monetary ceilings fail admission, and remaining cost is passed to each turn request. Authored/host completion-token ceilings require a selected executor declaring hard enforcement, and each request narrows to the remaining aggregate allowance. Authored strict prompt, total-token and cost limits, and host ceilings in those dimensions, fail coordinated registration before paid work because the configured Baize endpoint cannot provide a trusted pre-call maximum quote. An optional SQLite leaf ledger and quote contract exist for custom executors, but strict admission remains closed pending an integrated provider guarantee. The ledger store identity is now bound to replay identity. Explicit `aggregate advisory` permits after-call monitoring and may report an overrun after spend. It is not a hard total-spend guarantee. |
| Structured inference | Supported | Provider output remains untrusted until final Fuwen type validation. |
| Image, video and audio generation | Supported | Exact profiles, bounded deadlines, publication receipts and durable evidence are required. |
| Plan comparison and revision lineage | Supported, explanatory only | Neither correspondence nor lineage authorizes execution or artifact reuse. |
| Imports, subworkflows and `secret<T>` | Not supported | Deferred until locking, trust and end-to-end secrecy contracts exist. |
| Generic parallel blocks or arbitrary loops | Not supported | Use keyed fan-out and bounded repeat; arbitrary control-flow cycles are rejected. |
| Built-in artifact storage, credentials or authorization | Not supported by design | These are host/provider responsibilities. |

The matrix status above supersedes historical completion notes when they
conflict. The authoritative executable behavior is the parser/compiler plus the
compiler-backed source corpus; complex-inference guarantees also require the
coordinator, provider adapter, and persistence path to pass their corrective
contract tests. See [the authoring contract](fuwen-authoring.md) for syntax
ownership and [the release checklist](release-checklist.md) for the validation
required before publishing packages.
