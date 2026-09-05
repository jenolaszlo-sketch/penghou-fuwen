# Artifact contract

## Decision

Fuwen carries immutable, typed artifact references. It does not carry artifact
bytes, filesystem paths, connection strings, signed URLs, or provider SDK
objects in workflow state.

An artifact reference contains:

- a provider namespace and opaque provider-issued artifact ID;
- a nominal artifact-type descriptor reference;
- a versioned content digest;
- optional byte length and logical name.

The tuple is evidence about one immutable value. Providers must issue a new
reference when content changes. Fuwen never interprets the opaque artifact ID
or assumes that the digest is a storage address.

Artifact types are host-catalogued nominal descriptors. The same core contract
therefore represents `artifact<source-tree>`, `artifact<cited-document>`,
`artifact<dataset>`, `artifact<image>`, and `artifact<video>` without adding
domain-specific workflow nodes.

## Access is not identity

A resource handle grants scoped access to a host resource for one execution.
It is not an artifact reference and is not part of artifact identity. Handles
may expire or be revoked while an artifact reference remains meaningful.
Workflow source cannot construct a handle, path, or new capability.

## Publication

An activity or inference executor may request publication through a host port.
The returned publication receipt binds an idempotency key to the verified
artifact reference and a provider receipt ID. A receipt says whether the
provider created the publication or replayed an existing one.

The host must verify provider output before returning the receipt. Fuwen does
not treat a model-produced ID or hash as a publication receipt.

Runtime step IDs, attempts, actors, timing, and session evidence are runtime
provenance. They do not belong in immutable artifact identity.

## Integrity and confidentiality

Digests use an explicit algorithm and contract; existing digests are never
reinterpreted under a newer contract. A content digest proves equality under
that contract, not authorization, authenticity, confidentiality, or safe media
decoding. Those remain provider and host responsibilities.

Sensitive material should be represented by an opaque artifact or resource
handle with host policy. Fuwen v1 deliberately has no `secret<T>` type.

## Rejected alternatives

- Embedding bytes makes durable workflow state large and provider-specific.
- Persisting local paths is neither portable nor a capability boundary.
- A single universal artifact lifecycle would wrongly couple code, documents,
  datasets, and media.
- Treating publication as a mutable artifact update breaks reproducibility.
