# Capability matrix

This matrix describes the current source tree following `0.1.0-preview.11`;
post-tag hardening is listed under “Unreleased” in the changelog. “Supported”
means the checked-in compiler and relevant adapter have conformance or recovery
coverage. It does not mean a host has granted authority, supplied descriptors,
configured credentials, or accepted operational risk.

| Surface | Status | Current boundary |
| --- | --- | --- |
| Schemas, enums, capabilities, typed bindings and returns | Supported | Exact catalogue metadata remains authoritative. |
| Context, activity and inference nodes | Supported | Callables require exact descriptors and host admission. |
| Conditionals and value-producing merges | Supported | Branches have closed scope; only explicit merges escape values. |
| Keyed fan-out | Supported | Bounded, source-order aggregation with stable non-null keys. |
| Repeat | Supported | Positive static bound, explicit state/update/break and durable iteration identity. |
| Checkpoint and external wait | Supported | Typed durable interaction gates; presentation remains host-owned. |
| Inline prompts and registered prompt aliases | Supported | Typed bindings; registered templates are resolved and rendered by the host. |
| Inference tools | Limited | Declaration, identity, admission, one-turn proposals, and exact read-tool execution are supported and covered by shared conformance suites. Stock Baize maps one provider turn to exact proposals with pre-execution rejection; the durable model/tool/result loop is not yet implemented. |
| Bounded complex inference protocol | Supported | The existing `infer` node can declare aggregate limits with the `aggregate` source section. When the host supplies turn/read-tool ports, Zhinu runs one durable model → tool → model loop under the logical node with stable operation identity and replay reuse; external mutations remain explicit workflow activities. |
| Inference limits | Supported with adapter-specific enforcement | Per-call `maxTokens`/`timeout` and aggregate `protocol` limits are fingerprinted; an adapter must reject a limit it cannot honor rather than ignore it. |
| Structured inference | Supported | Provider output remains untrusted until final Fuwen type validation. |
| Image, video and audio generation | Supported | Exact profiles, bounded deadlines, publication receipts and durable evidence are required. |
| Plan comparison and revision lineage | Supported, explanatory only | Neither correspondence nor lineage authorizes execution or artifact reuse. |
| Imports, subworkflows and `secret<T>` | Not supported | Deferred until locking, trust and end-to-end secrecy contracts exist. |
| Generic parallel blocks or arbitrary loops | Not supported | Use keyed fan-out and bounded repeat; arbitrary control-flow cycles are rejected. |
| Built-in artifact storage, credentials or authorization | Not supported by design | These are host/provider responsibilities. |

The authoritative executable behavior is the parser/compiler plus the
compiler-backed source corpus. See [the authoring contract](fuwen-authoring.md)
for syntax ownership and [the release checklist](release-checklist.md) for the
validation required before publishing packages.
