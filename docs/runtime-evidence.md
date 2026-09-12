# Runtime values and context evidence

Fuwen keeps runtime boundaries provider-neutral and deliberately small. A
`RuntimeValue` is a detached `JsonElement`, an `ArtifactReference`, or a
bounded immutable list/object composite of those values. It is not a CLR
object graph, a provider handle, a credential, a filesystem path, or
dereferenced artifact content. JSON is cloned on construction, artifact
identities are deeply copied, and composites recursively snapshot their
children, so a host cannot mutate a value after it crosses the Fuwen boundary.

Construction is bounded before ownership is taken: JSON is limited to
`JsonRuntimeValue.MaximumJsonUtf8Bytes` input JSON UTF-8 bytes,
`MaximumJsonNodes` value nodes, and `MaximumJsonDepth` nesting levels. Composite
values additionally enforce `RuntimeValue.MaximumCompositeNodes` and
`RuntimeValue.MaximumCompositeDepth`, with list item, object property, and
property-name bounds. The compiler validator walks detached JSON once, while
composite children are validated recursively without converting them to JSON.

`ContextSnapshotReference` records the durable identity of context selected by
a host-owned provider. It contains:

- the exact `ContextProvider` descriptor;
- opaque snapshot identity plus request and selected-content digests;
- bounded source/revision references;
- policy revision and truncation/budget evidence;
- UTC creation time and an optional bounded opaque provenance receipt.

The reference is evidence, not a context store. Fuwen does not retain raw
context, resolve provider handles, fetch artifacts, or authorize access. A host
verifier must perform those operations and may reject a reference even when
its shape is valid.

When `Budget.WasTruncated` is true, observed item/byte counts may exceed the
configured maxima: the excess is evidence that the host had to truncate or
discard context. Untruncated evidence must remain within its declared maxima.

`RuntimeValueValidator` checks values at typed boundaries without coercion. It
enforces exact primitive JSON kinds, the canonical JSON v1 numeric contract,
optional/list/object/enum rules, exact nominal schema and artifact descriptors,
and bounded JSON/composite depth and node counts. `ListRuntimeValue` can carry
artifact references, and `ObjectRuntimeValue` can satisfy object schemas with
artifact fields; a JSON array/object never masquerades as an artifact-bearing
composite. It returns stable `FWN-RUNTIME-*` diagnostics suitable for a caller
or an LLM repair loop. Validation does not grant execution permission and does
not replace host policy or provider authorization.
