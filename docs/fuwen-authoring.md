# Fuwen authoring contract

The parser and compiler are the executable authority for the language.
`fuwen-grammar.json` is its reviewed, machine-readable syntax catalogue and
`fuwen-catalogue.json` describes the trusted descriptor pins and callable
metadata consumed by the compiler. The handwritten catalogue is guarded by
strict JSON checks and by the checked-in `.fuwen` corpus under
`tests/fixtures/documentation`; every fixture is compiled and canonically
formatted in CI, as is every README block tagged `fuwen`. A syntax change is
complete only when the parser, catalogue, corpus, and user-facing examples
agree.

Source is deliberately small: schemas,
enums, capabilities, workflow-owned prompt declarations with typed parameters,
typed workflows, named context/activity/inference nodes (inference either with
a registered template or with a prompt reference plus typed bindings, plus an
optional `tools` clause naming a toolset, an inline tool list, or `none`),
restricted bindings, control-only `if/else` with an optional explicit
`merge <then>, <else> -> <type>` for one value-producing result, bounded
keyed `fanout` regions with context/activity/inference/conditional bodies,
bounded state-carrying `repeat` regions, `checkpoint` and external `wait`
interaction gates, and a complete `return`. Prompt declarations, inference
tools, and per-call inference `limits`
(`maxTokens` and/or `timeout`, either or both) are part of the current IR; only
effect-free, read-only, and idempotent retry-safe write tools admit.
`maxTokens` participates in execution fingerprints; `timeout` bounds
wall-clock time per attempt without retry.

That tool effect rule describes compiler admission, not every execution
strategy. The current coordinated model/tool loop executes read-only tools
only, requires unique provider-visible tool names and a host-owned protected
result store. It validates proposed argument names/types against admitted
callable signatures before tool I/O and validates results before storage. Check the
[capability matrix](capability-matrix.md) before choosing an adapter.

An inference may also declare aggregate limits after the per-call limits:

```fuwen
limits maxTokens 800 timeout 30 aggregate advisory turns 8 modelCalls 6 toolCalls 4
  promptTokens 12000 completionTokens 4000 totalTokens 16000 durationMs 300000
  cost "USD" 250000 toolArgumentBytes 65536 toolResultBytes 262144
  retainedConversationBytes 524288 retainedEvidenceBytes 524288
```

Aggregate dimensions are positive, unique, and bounded. The `cost` form
requires a quoted currency and positive integer microunits. Aggregate limits
are part of the current inference protocol; authors do not select a protocol
revision or adapter implementation. Source bounds may only narrow finite host
ceilings. Per-call limits retain their per-attempt meaning. Without the
`advisory` keyword, authored prompt-token, total-token and monetary limits
are strict: coordinated registration rejects them until a trusted provider maximum quote is integrated. The ledger
store identity is now bound to replay identity. An optional SQLite leaf ledger now supports durable
reservation for custom quoted executors. Explicit
`aggregate advisory` permits after-call monitoring and failure on measured
overrun; it cannot prevent a provider charge. Host prompt, total-token and
monetary ceilings remain strict and cannot be downgraded by source advisory
mode. Completion-token limits retain their separate hard-executor gate.

An inference can select definitive failure codes and a static fallback value after its output type:

```fuwen
infer answer = infer "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" using "sample.prompt@1#eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" (request: input;) -> string on failure [SchemaMismatch, TurnLimitExceeded] fallback "manual review";
```

The fallback must have the exact declared output type and may contain only literals, lists, and objects of literals. Its selected codes are checked at compile and admission time. The runtime uses it only when the actual failure reports no possibly committed effect. It records a separate durable `$fallback` step while retaining the failed provider/protocol evidence. It does not retry inference, reset its budget, treat a failed operation as a provider success, or handle cancellation, timeout, fencing loss, ambiguous commitment, unknown budget, or unavailable evidence. Authors should give the fallback value an explicit status field when their output schema represents both normal and fallback outcomes. Plans with this clause use `fuwen-ir/v2-inference-fallback`; plans without it retain their existing IR version and fingerprints. Fallbacks in repeat and fan-out bodies are currently rejected because those paths do not yet retain item-scoped failed-operation evidence. A static fallback may be followed in a root region by an evaluator activity, a checkpoint holding its explicit disposition, and a human `wait` with an acceptance conditional. A missing decision leaves the run explicitly blocked; approval is a separately persisted wait result and recovery does not rerun settled provider or evaluator work. General handler regions remain future work.

Inline prompt declarations contain at least one `system` or `user` message.
A registered prompt alias instead declares `uses registered <descriptor>` and
may have no local messages because the host resolves its exact template.

Use exact descriptor pins (`name@version#sha256-value`) when source must compile
against a catalogue that is not the in-memory test catalogue. The catalogue is
the authority for schemas, callable signatures, capabilities, and effects.
Keep diagnostics and their spans when repairing generated source; each
`FWN-*` code is stable and budget failures are expected to be actionable.

Formatting is canonical and idempotent. Reformatting may remove comments and
change whitespace, but it does not alter the executable plan. Source maps use
the authored document's UTF-8 byte spans, while the canonical plan fingerprint
excludes source locations.
