# Fuwen authoring contract

`fuwen-grammar.json` is the machine-readable syntax catalogue and
`fuwen-catalogue.json` describes the trusted descriptor pins and callable
metadata consumed by the compiler. Source is deliberately small: schemas,
enums, capabilities, workflow-owned prompt declarations with typed parameters,
typed workflows, named context/activity/inference nodes (inference either with
a registered template or with a prompt reference plus typed bindings, plus an
optional `tools` clause naming a toolset, an inline tool list, or `none`),
restricted bindings, control-only `if/else` with an optional explicit
`merge <then>, <else> -> <type>` for one value-producing result, bounded
keyed `fanout` regions with activity/conditional bodies, and a complete
`return`. Prompt declarations, inference tools, and inference `limits`
(`maxTokens` and/or `timeout`, either or both) require IR v8; only
effect-free, read-only, and idempotent retry-safe write tools admit.
`maxTokens` participates in execution fingerprints; `timeout` bounds
wall-clock time per attempt without retry.

Use exact descriptor pins (`name@version#sha256-value`) when source must compile
against a catalogue that is not the in-memory test catalogue. The catalogue is
the authority for schemas, callable signatures, capabilities, and effects.
Keep diagnostics and their spans when repairing generated source; each
`FWN-*` code is stable and budget failures are expected to be actionable.

Formatting is canonical and idempotent. Reformatting may remove comments and
change whitespace, but it does not alter the executable plan. Source maps use
the authored document's UTF-8 byte spans, while the canonical plan fingerprint
excludes source locations.
