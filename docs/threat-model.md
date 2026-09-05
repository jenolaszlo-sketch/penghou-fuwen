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
