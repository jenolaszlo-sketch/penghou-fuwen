# FI-04 resource-grant contract

Status: design gate. The current coordinator still uses `tool.Name@tool.Version` as
`InferenceReadToolRequest.Scope`; that value identifies a behavior, not the resource
that behavior may read. Do not claim resource-scope enforcement until the runtime
and host-executor checks below ship and pass together.

## Boundary and authority

The model proposes arguments, not authority. A trusted host grant names the exact
resource set a descriptor may read (for example repository revision and path set,
database/tenant and query class, or a bounded artifact collection). Its opaque
reference is finite, versioned, and bound to the host policy identity used at
admission. The host alone interprets it. A descriptor digest and a capability
*class* remain separate metadata; neither stands in for a grant.

Admission must bind each model-callable tool to an exact grant reference and a
host validator for its resource-bearing arguments. An absent grant, unresolved
reference, changed policy revision, or unsupported validator rejects registration
before a model call. The registration receipt must cover the references and their
host policy identity. A compatible plan/receipt version is required so older
runtimes cannot silently ignore the new bindings.

## One model tool batch

1. Validate every proposal against the exact admitted descriptor and typed
   model-facing contract, including duplicate provider call IDs and the entire
   remaining tool allowance. No tool I/O occurs yet.
2. Resolve the actual resource set from *every* proposal's typed arguments and
   check it against that tool's admitted grant. Reject the whole batch before any
   tool I/O if one proposal is unauthorized. Return a stable `PolicyRejected`
   failure without exposing a private resource name to the model.
3. Journal the authorized batch with exact descriptor digest, canonical argument
   digest, grant reference and policy identity, provider call ID, and operation
   key. On recovery, validate this state against the assistant's durable proposal
   history and the admission receipt. A changed or missing grant stops; it never
   causes a speculative resubmission.
4. Pass the checked grant reference and resolved resource identity to the host
   read-tool executor. The executor rechecks the grant against the requested
   resource at the I/O boundary; a coordinator check alone cannot authorize a
   file or tenant read. A check failure returns `PolicyRejected` before I/O.
5. Keep protected-result storage authorization separate. Its storage scope is
   not a grant to the underlying repository/database resource. Recorded results
   remain protected and are reused only under the same admitted execution and
   grant identity.

Grant checking is read-only and bounded. It must not fetch the protected
resource or call the model. Grant revocation requires an explicit policy:
registration/recovery checks reject a revoked grant before new I/O; previously
durable output may only be replayed according to the host's recorded-run access
policy. Do not silently reinterpret an old grant reference.

## Acceptance cases

- Valid first proposal plus unauthorized second proposal: zero tool I/O.
- Correct descriptor but wrong file/tenant: zero tool I/O; stable
  `PolicyRejected` with no leaked private path.
- Same name/version with different descriptor digest cannot borrow a grant.
- Changed arguments/resource after an authorized journal entry cannot replay its
  tool result; changed grant or policy identity cannot resume as the same batch.
- Crash after batch authorization and before tool submission resumes with the
  same operation identity; crash after submission reconciles or stops as
  ambiguous, never submits under a fresh key.
- Correct grant succeeds; its protected result remains invisible in ordinary
  run/step/event metadata, and replay performs no duplicate tool I/O.
- The standalone Review and Marang consumers exercise an actual resource rule,
  rather than accepting `name@version` as authorization.

## Migration

Keep the current pseudo-scope behavior explicitly labeled unsafe for resource
authorization until the grant-aware IR and adapter land. Do not publish a release
claiming FI-04 resource grants from a descriptor-derived scope. Existing plan
fingerprints need no rewrite; grant-bearing plans use a new compatible IR/receipt
contract, and coordinated tool registrations without grants then fail closed.
