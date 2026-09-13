# Execution-port conformance

Fuwen execution adapters share one provider-neutral contract. Before a new
adapter is considered compatible, its tests must cover this compact matrix:

| Contract | Required cases |
| --- | --- |
| Values | primitive, object, bounded list, optional/null, artifact reference |
| Validation | admitted type succeeds; wrong type fails closed before dependants |
| Projection | object/list projections retain the declared type and bounds |
| Evidence | success, typed failure, and post-provider failure retain available evidence |
| Publications | immutable receipt snapshot, exact operation identity and nominal descriptor |
| Durability | same-operation replay, crash recovery, focused restart, corrupt-envelope rejection |

The core execution-port tests apply every successful result wrapper to all
supported runtime-value representations. The Zhinu suite then exercises typed
validation, projection, SQLite recovery/replay, corrupted evidence, artifact
publication, and Baize JSON-list fan-out. The Baize suite covers structured,
tool, and generated-artifact outcomes. This is a behavioral contract: adapter
implementations may differ internally, but they must not expose representation
differences to a valid downstream Fuwen node.

Provider-specific handles, credentials, raw bytes, and retry policy do not
belong in this matrix. Those remain behind the host adapter and are represented
at the Fuwen boundary only by bounded values, identities, receipts, and typed
evidence.
