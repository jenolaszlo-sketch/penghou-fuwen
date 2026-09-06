# Fuwen IR v1

## Purpose

The IR is a provider-neutral, immutable executable-plan contract. Source is an
authoring format; the IR is what an execution adapter loads and verifies.

V1 proves a deliberately narrow graph:

- `ContextNode` resolves one immutable context snapshot;
- `InferenceNode` invokes a resolved logical inference profile and template;
- `ActivityNode` invokes a trusted catalogue activity;
- `ConditionalNode` chooses one named lexical branch deterministically;
- `ReturnNode` produces the workflow result.

Fan-out, loops, waits, interaction, and child workflows are deferred, but their
tagged runtime identity segments are reserved now.

V1 is acyclic. Node identifiers and bindings express data dependencies and
named lexical branches; they cannot encode backward jumps. Future loops,
fan-out, retry regions, polling, and waits remain first-class structured IR
regions with declared inputs and outputs. Canonical JSON serializes those
semantics through identifiers and references, never serializer `$id`/`$ref`
object cycles or arbitrary graph topology.

## Type model

The static type system is intentionally smaller than JSON Schema:

- scalar: `string`, `bool`, `int`, `number`, `duration`, and bounded `json`;
- nominal enum and object schema references;
- `optional<T>`;
- bounded `list<T>` (an effective maximum is mandatory);
- nominal `artifact<TArtifactDescriptor>`.

Named workflow, activity, context, and inference boundaries are nominal.
Record/list literals and projections are structural and must type-check into a
nominal boundary. Arbitrary JSON Schema features are not accepted.

Resolved IR embeds constrained `ObjectSchemaDefinition` and
`EnumSchemaDefinition` records. Object fields are unordered by name, use Fuwen
types recursively, and express absence only through `optional<T>`. Enum members
have a source name and a unique stable serialized string value. Every named
schema reference must have an exact resolved definition and matching pinned
catalogue binding.

## Bindings

Bindings are immutable expression trees. V1 permits workflow-input references,
node-output references, field projections, literals, bounded lists, and records.
Dependencies are derived from node-output references; a second `dependsOn`
collection is deliberately absent because it could disagree with dataflow.

Conditions are a restricted expression tree. There is no embedded code,
reflection, function invocation, arbitrary arithmetic, lambda, or collection
query language.

## Resolved descriptors

All trusted catalogue dependencies are pinned by kind, name, version, and
content digest. Activities, context providers, inference profiles, prompt
templates, tools, schemas, and artifact types share this reference shape.
Implementations and credentials are not serialized into IR.

The capability manifest is inferred by compilation and checked against host
grants. Workflow source cannot add capabilities by editing the manifest.

## Context semantics

A context node produces a typed immutable snapshot reference. An inference
infrastructure retry reuses that exact snapshot. Refreshing context is an
explicit restart of the context node and invalidates dependent work.

## Serialization and compatibility

IR JSON is canonicalized under `penghou-canonical-json/v1`. Collections without
semantic order are normalized before serialization. The root declares its IR,
language, compiler-semantic, canonical-JSON, and fingerprint contract versions.
Polymorphic records use a `$kind` discriminator; ordinal canonical sorting keeps
that metadata first for portable reading on every supported .NET target.

Readers reject unsupported contracts. Definition stores persist canonical bytes
under the execution fingerprint and verify both when loading. Historical bytes
are never silently upgraded in place.

`IWorkflowDefinitionStore` is immutable and content addressed. Repeating a
byte-identical write is idempotent; conflicting content under an existing
fingerprint is rejected. `WorkflowDefinitionDocument.LoadVerified` reparses
persisted JSON, enforces the size and supported-contract envelope, rejects
noncanonical bytes, and recomputes the execution fingerprint before returning
it. This is integrity verification only: canonical bytes and a matching hash
do not prove binding/type correctness, catalogue membership, capability grants,
authorization, or permission to execute. `ReadPlan` likewise only
deserializes the verified bytes. A host must pass the plan through the future
compiler/admission pipeline before execution. The in-memory provider is the
reference behavior for future durable stores.

Programmatic plan freezing is resource-bounded before it allocates snapshot
collections. It rejects null/hostile entries, excessive collection counts,
aggregate nodes/bindings/schema fields, excessive nesting, and caller-owned
reference cycles with stable bounded failures. These construction limits are
admission/resource-safety guards and do not add fields or change canonical v1
bytes.

## Source maps

Source maps and diagnostics refer to structural node paths but live beside the
executable plan. Changing a file name, line, column, or diagnostic text cannot
change execution identity.

Each source map binds an execution fingerprint and source fingerprint to exact
source-document digests. Spans use zero-based UTF-8 byte offsets and lengths;
line and UTF-16 column are display metadata. Validation rejects out-of-range
spans, unknown documents, unknown node paths, and a mismatched plan fingerprint.

## Rejected alternatives

- A grammar-first design would force unresolved semantics into parser code.
- CLR type names and polymorphic runtime objects are not portable IR.
- Raw provider configuration in IR would make host policy source-controlled.
- Source order as node identity makes harmless edits unsafe for restart.
- Arbitrary JSON Schema would turn type checking into a much larger language.
