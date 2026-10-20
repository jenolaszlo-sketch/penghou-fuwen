# Run-scoped dynamic provider invocation identities

Unpublished preview.20 compiles new repeat/fan-out plans to
`fuwen-ir/v6-run-scoped-dynamics`. The provider runtime path includes the workflow
run ID, iteration/item identity and declared operation path. The existing
`fuwen-operation/v1` derivation then separates independent runs with identical
inputs. Retries retain the same run, path and step revision; explicit restarts
retain native revision semantics. Native occurrence identities, coordinates,
admission, authority and schema 10 are unchanged.

This corrects cross-run collisions in older repeat and ungrouped fan-out provider
identities. Top-level providers and independently durable grouped fan-out already
included run identity. The correction covers activity, context and one-call
inference. Coordinated inference already binds its interaction journal to the
run/request contract and keeps that protocol.

A v6 plan fingerprints `expandedGroupScopes: true` when it uses an immutable
expanded-group companion. Core validation preserves strict expanded paths and
innermost membership checks; executable registration requires that exact
companion before storage. The flag is deeply snapshotted with the plan. It is
omitted when false and rejected on older IR versions. The computed
`UsesExpandedGroupScopes` property is not serialized. Existing v1–v5 canonical
bytes, fingerprints and in-flight invocation keys therefore stay unchanged.

Adopt the correction by compiling/admitting a new definition and starting new
runs. Do not change an in-flight operation's idempotency key: its external effect
may already have committed. Older dynamic definitions retain their earlier key
contract. Compile/admit v6 definitions for independent runs against a shared
idempotent provider. The [migration notes](consumer-migration.md)
describe this boundary. New v6 source compilation does not add nested dynamics,
nonterminal reselection, item waits/merges or other excluded adapter shapes.

Registration checks full provider runtime paths, including the 37-byte GUID/slash
prefix and worst-case declared iteration/epoch lengths, before storing any
definition. All provider identities still fit the 1,024-byte contract. The public
step mapper retains actual native keys; its legacy ungrouped fan-out scope still
exposes the hash only, while resolving that scope restores `sha256-`. Malformed
item hashes are rejected.

Ungrouped repeat names must be unique across structured regions: their native
loop identity is the authored name. Ambiguous repeated names are rejected before
storage. Expanded grouped repeats already use structural namespaces and retain
support for repeated local names.

The [integration second pass](qualification/activity-groups/integration-second-pass.md)
records counterexamples, fixes, legacy compatibility, source/package pins and
the exact qualification evidence. The original
[v5 selection checkpoint](qualification/activity-groups/selection.md) remains a
separate immutable baseline.
