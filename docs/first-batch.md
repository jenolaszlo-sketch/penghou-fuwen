# First implementation batch: executable-plan design gate

## Goal

Close Delivery milestone A with a reviewable, provider-neutral v1 IR contract
before implementing the compiler or source grammar.

## Progress — 2026-09-01

Implemented the first reviewable draft of the provider-neutral type, binding,
artifact, descriptor, capability, and five-node IR contracts. Structural paths,
compatibility rejection, Penghou canonical JSON v1, normalized plan bytes, and
versioned execution fingerprints have multi-target tests. The tests cover
canonical round-trip, sibling movement/insertion, descriptor and routing-policy
changes, unsupported IR, duplicate JSON properties, and four artifact domains.

Verification exposed and fixed two contract issues: canonical discriminators
are named `$kind` so they remain first under ordinal sorting and deserialize on
.NET 8, and every referenced descriptor must exactly match a root catalogue
binding. Branch identity reserves `$then` and `$else` to prevent collisions.

The second batch added resolved constrained object and enum schemas, exact
schema-definition/catalogue closure, external source maps with range and plan
binding validation, and canonical typed runtime keys for future loop/fan-out
execution. Source position changes are proven not to affect execution identity;
schema field ordering is normalized; duplicate fan-out keys fail before work.

The final Delivery A batch added the immutable `IWorkflowDefinitionStore`,
idempotent in-memory storage, canonical-byte and fingerprint verification,
transport-level source normalization and a separate source fingerprint, a
complete canonical `WorkflowPlan` golden fixture, and an independent Python
canonicalization/hash check in CI. The public API is frozen in an analyzer
baseline and validated for both `net8.0` and `net10.0`.

Delivery A is complete. The final local verification passed 33 logical tests on
both target frameworks (66 executions), formatting verification, public API
analysis, and Release packaging. SourceLink warnings remain expected until the
new repository has its first commit and remote.

## Required activities

1. Define the minimum type system: scalar, enum, object, bounded list, optional,
   typed artifact reference, and nominal named type references.
2. Define workflow input/output, immutable bindings, and the first node records:
   context, inference, activity, conditional, and return.
3. Define descriptor references for trusted activities, context providers,
   inference profiles, prompt templates, tools, schemas, and artifact types.
4. Specify structural node paths, reserved segments, escaping, length limits,
   collision diagnostics, and rename behavior.
5. Specify runtime identity derivation while reserving unambiguous identities
   for keyed fan-out, loops, waits, interaction, and child workflows.
6. Define provider-neutral immutable artifact identity and verified publication
   receipts without embedding bytes or storage paths in workflow state.
7. Define canonical IR JSON and separate source, execution, and provenance
   fingerprints.
8. Define immutable definition-store verification and explicit compatibility
   rejection for unsupported historical IR.
9. Add golden vectors for canonical bytes, fingerprints, unrelated sibling
   insertion/movement, rename, source-position-only changes, changed descriptor,
   and changed routing-policy revision.
10. Review the contract against four artifact domains: code, cited documents,
    datasets, and generated media.

## Deliverables

- `docs/ir-v1.md` with records, invariants, examples, and rejected alternatives.
- `docs/identity-and-fingerprints.md` with derivation and golden cases.
- `docs/artifact-contract.md` with provider/host boundaries.
- Draft contracts in `Penghou.Fuwen` only after their semantics are accepted.
- Canonical serialization and independent golden-vector tests.

## Exit criteria

- Core contracts depend on no Zhinu, Baize, Hongxian, Guyabano, or domain type.
- The same artifact type system represents code, document, dataset, and media
  examples without new node kinds.
- Unrelated sibling edits preserve node identity; rename changes it.
- Byte-identical IR has the same execution fingerprint across processes and an
  independent implementation.
- Unsupported IR or changed immutable content is rejected, never reinterpreted.
- The reviewed design is sufficient to begin compiler implementation without
  inventing semantics in parser code.

## Not in this batch

Parser/grammar, Zhinu execution, Baize, Nuwa integration, fan-out execution,
Hongxian, UI, general loops, waits, interaction, compensation, imports,
secrets, or a Guyabano migration.

The initial constrained programmatic compile path is now implemented with
trusted catalogue/schema resolution, explicit execution schedules, stable
diagnostics, resource budgets, binding/type validation, exact capability
assertions, trusted callable signatures, conservative
side-effect/idempotency/retry rejection, and canonical definition output.
Complete executable admission still requires an immutable receipt bound to the
catalogue snapshot, host policy/grants, and effective limits. The work does not
begin the text grammar or add loops.
