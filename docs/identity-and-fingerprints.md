# Identity and fingerprints

## Structural node identity

Every executable node has an explicit source name. Names are unique in their
immediate lexical scope. The structural path is the workflow name followed by
the named lexical scopes and node name, separated by `/`.

V1 names are ASCII identifiers matching `[A-Za-z_][A-Za-z0-9_-]{0,63}`.
They cannot start with `$`. A complete path is at most 1,024 UTF-8 bytes.
Segments beginning with `$` are reserved for runtime identity tags:
`$then`, `$else`, `$iteration`, `$item`, `$wait`, `$interaction`, and `$child`.

Identity never uses a source line, column, AST ordinal, or unrelated sibling
position. Moving or inserting an unrelated sibling preserves existing node
paths. Renaming an executable node intentionally changes its path.

Future runtime instances append tagged segments to structural identity:

```text
repair/repair_loop/$iteration/2/apply
video/scenes/$item/<canonical-key>/render
```

Fan-out keys must be deterministic, non-null, scalar, and unique before child
execution starts. Positional identity requires an explicit opt-in.

V1 runtime keys are typed canonical values: bounded ordinal strings, signed
64-bit integers, nominal enum values, lowercase D-format UUIDs, and explicit
non-negative positional indices. Type is part of key identity, so string `"1"`
and integer `1` are different items. The safe path segment is
`sha256-<lowercase hex>` over the canonical typed key bytes. The original key
remains execution input/evidence; the path does not pretend to be reversible.
Duplicate canonical keys fail before any fan-out child begins.

## Canonical JSON

Canonical JSON contract `penghou-canonical-json/v1` is the input to hashes:

- object properties are ordered by ordinal name;
- array order is preserved;
- no insignificant whitespace is emitted;
- strings use deterministic JSON escaping and UTF-8;
- integers use their shortest decimal representation;
- finite non-integers use the shortest round-trippable representation with a
  lowercase exponent, no exponent plus sign, and no redundant exponent zeroes;
- null, booleans, strings, arrays, and objects retain normal JSON meaning;
- duplicate object properties and non-finite numbers are rejected.

IR collections whose order has no semantics are normalized before canonical
JSON is produced. In particular, nodes are ordered by structural path and
catalogue bindings, schemas, capabilities, and object fields by stable key.

## Three separate identities

`sourceFingerprint` hashes canonical formatter output. Until the parser and
formatter exist, the frozen transport normalization removes a UTF-8/UTF-16 BOM,
normalizes CRLF and CR to LF, guarantees exactly one final LF, rejects NUL and
invalid UTF-16, and preserves exact Unicode code points and all other text.
It deliberately does not apply whole-document Unicode normalization because
that could change string-literal semantics. The source fingerprint identifies
authored text and is not needed to execute a plan.

`executionFingerprint` hashes canonical resolved IR. Its envelope includes the
IR version, language version, compiler semantic version, all resolved schema
and descriptor hashes, routing-policy revision, and capability manifest. It
does not include source locations or runtime outcomes.

Runtime provenance records what happened: resolved provider/model, snapshots,
attempts, tool calls, artifacts, usage, timing, and errors. It never changes an
execution fingerprint.

Fingerprint strings are self-describing:

```text
sha256:fuwen-execution/v1:<lowercase-hex>
sha256:fuwen-source/v1:<lowercase-hex>
```

The definition store binds that value to byte-identical canonical IR. A hash
match with different bytes is an integrity failure. Unknown IR, canonical JSON,
or fingerprint contracts are rejected rather than reinterpreted.

## Golden behavioral cases

- The same plan produces identical canonical bytes across processes.
- Moving or inserting an unrelated sibling preserves existing node paths.
- Moving siblings does not alter canonical IR or its execution fingerprint.
- Inserting a node changes the plan fingerprint but not existing node paths.
- Renaming a node changes its path and plan fingerprint.
- Source-location-only changes do not change the execution fingerprint.
- Changing a descriptor hash or routing-policy revision changes it.
