# Runtime values and context evidence

Fuwen keeps runtime boundaries provider-neutral and deliberately small. A
`RuntimeValue` is either a detached `JsonElement` or an `ArtifactReference`.
It is not a CLR object graph, a provider handle, a credential, a filesystem
path, or dereferenced artifact content. JSON is cloned on construction and
artifact identities are deeply copied, so a host cannot mutate the value after
it crosses the Fuwen boundary.

Construction is bounded before ownership is taken: JSON is limited to
`JsonRuntimeValue.MaximumJsonUtf8Bytes` input JSON UTF-8 bytes,
`MaximumJsonNodes` value nodes, and `MaximumJsonDepth` nesting levels. The
compiler validator walks that detached tree once, so nested schema/list checks
do not repeatedly clone or rescan subtrees.

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
and bounded JSON depth/node counts. It returns stable `FWN-RUNTIME-*`
diagnostics suitable for a caller or an LLM repair loop. Validation does not
grant execution permission and does not replace host policy or provider
authorization.
