# Fuwen threat model

Status: maintained design threat model for the current preview source tree.
This is not an external security audit, certification, penetration-test report,
or claim that every integrating host is secure. It records the boundaries the
libraries enforce and the responsibilities they deliberately leave to hosts.

## Scope and security objectives

This model covers Fuwen source parsing and compilation, trusted catalogue
resolution, host admission, immutable plan storage and identity, Zhinu adapter
registration/execution, Baize inference bindings, durable evidence, and
artifact-publication receipts. It does not cover the internal security of
Zhinu, Baize, model providers, databases, artifact stores, operating systems,
or an embedding application except at their Fuwen-facing boundaries.

The principal objectives are:

- untrusted source cannot introduce arbitrary executable code or grant itself
  authority;
- executed semantics match one admitted, immutable, exactly identified plan;
- descriptor, policy, provider-runtime, input, and artifact identities cannot
  be silently substituted;
- retries and recovery do not silently duplicate unsafe effects;
- resource use and retained evidence remain bounded;
- diagnostics and explanations do not become authorization credentials.

## Assets

- canonical plans, fingerprints, source identities, and immutable definitions;
- trusted catalogue snapshots, callable contracts, schemas, exact descriptor
  pins, capability policies, and routing policies;
- admission receipts and provider-runtime identities;
- runtime inputs, context snapshots, prompt bindings, model outputs, and tool
  declarations;
- operation keys, execution evidence, retry state, checkpoint/wait state, and
  plan-revision records;
- artifact references, content digests, publication receipts, and provider
  receipt identifiers;
- host credentials, resource handles, provider secrets, and policy data.

Credentials and artifact bytes are intentionally not Fuwen plan assets: they
remain behind host/provider ports and must never be embedded in source or IR.

## Actors and untrusted inputs

Workflow authors, model-generated source, catalogue publishers, host
integrators, model/providers, artifact providers, and external signal senders
may be benign, faulty, or malicious. Treat source text, programmatic plan
objects, runtime values, model output, context payloads, external signals,
artifact metadata, persisted records, diagnostics loaded from elsewhere, and
descriptor material obtained outside the configured trusted catalogue as
untrusted.

The configured catalogue, finite capability policy, immutable stores, provider
bindings, artifact publisher, and execution ports are trusted host components.
Fuwen validates their contracts but cannot make a malicious implementation
trustworthy.

## Trust boundaries

```text
untrusted source / plan object
          |
          v
bounded parser + semantic compiler <-- trusted catalogue snapshot
          |
          v
canonical immutable definition
          |
          v
host admission <-- versioned finite grants + limits
          |
          v
opaque in-process receipt + exact provider-runtime identity
          |
          v
Zhinu adapter --> host execution ports --> providers / artifact publisher
          ^                                      |
          +------ durable typed evidence <-------+
```

Compilation proves language and catalogue validity; it does not authorize
execution. Admission proves a configured host accepted an exact definition;
it does not make providers, retrieved content, or model output trustworthy.
Plan comparison and revision lineage explain change; they never authorize
reuse. Artifact references identify claimed content; only the host/provider can
verify and authorize access to the bytes.

## Threats, controls, and residual risks

| Threat | Enforced controls | Residual risk / required mitigation |
| --- | --- | --- |
| Parser or validator exhaustion and diagnostic amplification | Host/caller compilation budgets bound tokens, nodes, schemas, depth, expressions, strings, lookups, time and diagnostics. Collections and payloads are snapshotted and bounded. | Hosts must choose ceilings appropriate to their service and also bound request bodies, concurrency and queueing. Resource exhaustion below configured limits remains possible. |
| Catalogue substitution or digest confusion | Executable descriptors use exact kind/name/version/SHA-256 identities. Trusted schema/callable metadata is resolved and closed over referenced descriptors. Admission binds an immutable catalogue revision. | A compromised catalogue authority can publish malicious but internally consistent metadata. Protect catalogue publication, review changes and rotate revisions deliberately. |
| Capability escalation from source | Source capability declarations must exactly match trusted descriptor requirements; finite host grants are checked separately. Source cannot create grants. `AllowAll` and unversioned policies cannot issue admission receipts. | Capability names/scopes have only the meaning assigned by the host. Over-broad grants or dishonest execution ports defeat the boundary. |
| Plan tampering, identity collision or version confusion | Canonical serialization, versioned fingerprints, structural paths, immutable definition verification, supported-IR checks and provider-runtime identity checks precede registration/execution. | SHA-256 collision resistance and correct canonical implementations are assumed. Persisted plans still require access control and corruption monitoring. |
| Forged or replayed admission | `WorkflowAdmissionReceipt` has no public constructor and binds plan, catalogue, policy/grants and limits. Zhinu registration requires the matching receipt and definition. | The receipt is an opaque in-process token, not signed cross-process authority. A compromised process, reflection, or unsafe deserialization is outside this boundary. Remote execution must authenticate an envelope or repeat admission. |
| Malformed runtime input, provider output or prompt binding | Runtime values are validated without coercion or artifact dereference. Prompt bindings are typed and bounded. Final Fuwen type validation is distinct from optional JSON repair. | Schematically valid content can still be false, adversarial, privacy-sensitive or instruction-injected. Hosts need domain validation and content policy. |
| Prompt injection through context or artifacts | Context dependencies and snapshot identities are explicit; delivery is bounded and adapter-controlled. No implicit artifact dereference occurs. | Fuwen does not determine factual trust or neutralize semantic prompt injection. Hosts must label provenance, minimize/redact context and choose isolation/tool policy. |
| Unauthorized or unsafe tool/effect execution | Tools are exact, admitted descriptors. Destructive/external or unsafe retry contracts are rejected; writes require idempotent, retry-safe metadata. | The stock Baize adapter does not provide a general tool execution loop. A custom loop must enforce exact allowlists, typed arguments/results, finite rounds, per-call evidence and aggregate budgets. Callable metadata cannot compensate for a dishonest tool implementation. |
| Duplicate side effects during retry/recovery | Stable operation identities, retry classifications, idempotency contracts, committed-effect evidence and durable replay tests separate semantic failure from infrastructure retry. | Network ambiguity can leave an effect committed without a response. Providers must support idempotency keys or receipt lookup; hosts must conservatively stop when commitment is unknown. |
| Prompt injection carried by tool results | The coordinated loop admits only exact read-only, declared tools; tool results are data validated/typed at the boundary, never executed as instructions, and each exact operation is evidence-recorded. | Fuwen cannot distinguish trustworthy tool content from adversarial text. Hosts must treat tool output as untrusted input, label provenance, sanitize or quarantine content, and constrain the model's follow-on authority. |
| Data exfiltration through tool arguments or results | Tool calls are validated before execution against the admitted allowlist, bounded by argument/result byte ceilings, and retained evidence is digests and safe summaries rather than raw payloads. | A model may still encode sensitive context into a permitted read call or result. Hosts must apply least-privilege tool scopes, output filtering, and network egress policy; read-only is not harmless. |
| Malicious or dishonest tool metadata | Tools must be exact catalogue descriptors with a read-only effect and idempotent, retry-safe contracts; undeclared, write-capable, duplicate, oversized, or mistyped calls fail before any execution. | Declared metadata is only as trustworthy as the catalogue and the implementation behind the port. A dishonest tool can lie about effects; protect catalogue publication and review tool implementations. |
| Corrupted or forged inference journal | Operation identities are derived from the interaction identity and ordinal, journal state is validated on read, committed operations are reused, and ambiguous or mismatched state fails closed with a typed outcome instead of issuing new work. | Storage integrity, access control, rollback protection, and backup remain host/Zhinu operator responsibilities. Evidence sinks are non-authoritative and cannot alter execution truth. |
| Aggregate budget races and unknown usage/pricing | Per-dimension effective bounds are the minimum of source and host ceilings; the coordinator checks counts, tokens, cost, payload, and durable time before each operation and treats unknown usage/pricing as `BudgetUnknown` rather than zero. Reservations settle from committed evidence on replay. | Concurrent host accounting outside the coordinator, changing pricing revisions, and dishonest provider usage can still misstate true spend. Hosts must pin pricing revisions, reconcile invoices, and treat bounded estimates as estimates. |
| Artifact-reference forgery or publication mismatch | Nominal artifact descriptors, content digests, byte lengths, ordered publication receipts, exact provider receipts and admitted-descriptor checks are validated. | Fuwen does not fetch or store bytes. Providers must verify bytes against digests, authorize access, prevent mutable identity reuse and protect receipt stores. |
| Corrupt or stale durable state | Definition fingerprints, runtime identities, evidence validation, checkpoint/wait identities and corruption/recovery tests reject mismatched state. | Database confidentiality, availability, backup integrity and rollback protection belong to Zhinu/storage operators. External signal authentication is host-owned. |
| Secret or sensitive-data disclosure | The language has no executable-code or filesystem-access construct and no `secret<T>` feature. Diagnostics/evidence are bounded; credentials remain host-owned. | Bounded data can still be sensitive. Hosts must redact source/context/provider messages, restrict logs and source maps, encrypt stores, apply retention policy and never place credentials in bindings. |
| Unsupported feature silently ignored | Version gates, adapter capability failures, exact prompt/tool/limit contracts and the checked-in capability matrix make unsupported combinations explicit. | Custom adapters must reject rather than ignore limits or semantics they cannot honor. Preflight remains a planned usability improvement. |
| Release or dependency substitution | Release CI validates the exact tagged version, builds/tests/formats, runs independent canonical vectors, packages once and passes commit-keyed artifacts to the OIDC publishing job. | Repository, action, NuGet and dependency compromise remain supply-chain risks. Protect maintainers/tags, pin or review actions, monitor published hashes and follow dependency advisories. |

## Host-admission receipt boundary

`WorkflowAdmissionReceipt` is an opaque in-process capability token with no
public constructor. Its fingerprint binds the canonical execution identity,
immutable catalogue snapshot, exact resolved trusted metadata, versioned finite
policy and grant set, and effective compilation limits. Semantic compilation,
verified definition loading, an unversioned authority, and the test-only
`CapabilityGrantPolicy.AllowAll` mode cannot mint a receipt.

The receipt is not digitally signed and must not be serialized or trusted as a
cross-process credential. A future remote execution adapter must use an
authenticated envelope or independently repeat admission; it must not accept
receipt-shaped data merely because fields and fingerprints are internally
consistent.

## Host responsibilities

Before executing an admitted plan, a production host must:

- authenticate callers and external signals, authorize workflow and artifact
  access, and map capabilities to real resource scopes;
- operate a reviewed immutable catalogue and versioned finite grant/routing
  policies; never use test-only allow-all admission;
- keep credentials and opaque resource handles outside source, plans,
  diagnostics and retained model-visible values;
- configure compilation, execution, provider, retry, concurrency, token, time,
  cost, payload and retention limits;
- bind only providers, tools and publishers whose behavior matches their
  declared effects, idempotency, retry safety and receipt contracts;
- verify artifact bytes/digests and secure durable definitions, evidence,
  checkpoints, signals, receipts and backups;
- apply privacy, provenance, prompt-injection, content-safety and domain
  validation appropriate to the deployed workload;
- monitor failures and spend, preserve auditable evidence, and define incident
  response, credential rotation and revocation procedures.

## Out of scope and review cadence

Fuwen does not defend a process after arbitrary code execution, a malicious
host or trusted port, compromised build/repository/provider infrastructure,
cryptographic breaks, denial of the underlying storage/network, or policy that
intentionally grants excessive authority. It does not provide sandboxing,
tenant isolation, malware scanning, data-loss prevention, secret management,
artifact storage, or provider account security.

Revisit this model when adding a remote admission protocol, general tool
execution, imports/subworkflows, secrets, new effect classes, new artifact
providers, or a new IR/fingerprint contract. Security-relevant changes require
tests at the affected trust boundary and corresponding capability/release
documentation updates.
