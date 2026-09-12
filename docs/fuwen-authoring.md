# Fuwen authoring contract

`fuwen-grammar.json` is the machine-readable syntax catalogue and
`fuwen-catalogue.json` describes the trusted descriptor pins and callable
metadata consumed by the compiler. Source is deliberately small: schemas,
enums, capabilities, typed workflows, named context/activity/inference nodes,
restricted bindings, control-only `if/else`, and a complete `return`.

Use exact descriptor pins (`name@version#sha256-value`) when source must compile
against a catalogue that is not the in-memory test catalogue. The catalogue is
the authority for schemas, callable signatures, capabilities, and effects.
Keep diagnostics and their spans when repairing generated source; each
`FWN-*` code is stable and budget failures are expected to be actionable.

Formatting is canonical and idempotent. Reformatting may remove comments and
change whitespace, but it does not alter the executable plan. Source maps use
the authored document's UTF-8 byte spans, while the canonical plan fingerprint
excludes source locations.
