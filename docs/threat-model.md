# Threat-model starter

Fuwen source, descriptors obtained from untrusted locations, model-produced
source, inline values, artifact metadata, and diagnostics are untrusted input.

The first design and implementation batches must address:

- parser/compiler resource exhaustion and diagnostic amplification;
- catalogue substitution, descriptor hash mismatch, and capability escalation;
- ambiguous node identity, collisions, and fingerprint confusion;
- artifact-reference forgery and content-hash mismatch;
- unsafe retries of side effects and idempotency-key confusion;
- arbitrary paths, resource handles, secrets, or executable code entering IR;
- unsupported IR versions being silently reinterpreted;
- source maps or diagnostics leaking retained sensitive content.

The host remains authoritative for authorization, trusted catalogues, resource
scopes, routing, budgets, and secrets. This document will become a full threat
model before the first public preview.

## Host-admission receipt boundary

`WorkflowAdmissionReceipt` is an opaque in-process capability token with no
public constructor. Its fingerprint binds the canonical execution identity,
immutable catalogue snapshot, exact resolved trusted metadata, versioned finite
policy and grant set, and effective compilation limits. Semantic compilation,
verified definition loading, an unversioned authority, and the test-only
`CapabilityGrantPolicy.AllowAll` mode cannot mint a receipt.

The receipt is not digitally signed and must not be serialized or trusted as a
cross-process credential. Reflection, a compromised host process, or unsafe
deserialization is outside this capability boundary. A future remote execution
adapter must use an authenticated envelope or independently repeat admission;
it must not accept receipt-shaped data merely because the fields and fingerprint
are internally consistent.
