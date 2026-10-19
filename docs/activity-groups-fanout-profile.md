# Activity groups: keyed fan-out bodies

ZG-6.2b extends the static and single-level repeat profiles with a separate,
bounded keyed fan-out profile. The original
[qualification](qualification/activity-groups/zg6-fanout.md) passed on Windows
.NET 8/10 against unpublished Fuwen preview.16 and Zhinu preview.8. The current
preview.18/preview.9 [consolidation](qualification/activity-groups/zg7.md) adds
progress, admission corrections and cross-platform gates. Original archives and
counts retain their separate evidence; candidate packages remain unpublished.

One fan-out scope is supported per grouped plan, optionally within static groups.
Every body operation must belong to one explicit item-local root group or one of
its nested logical/macro groups. Independent activities, context reads and
one-call inference use native durable operations. Control-only conditionals
remain pure decisions over persisted inputs. Preview.18 additionally rejects a
possibly empty item path before definition writes. A logical group need not export a yield, so syntax alone cannot prove
that every selected item will execute durable work. Admission requires an
unconditional provider operation or guaranteed provider work in both conditional
branches, recursively; it does not infer relationships between separate conditions.
Nested fan-out, repeat composition,
item waits/checkpoints, conditional value merges, coordinated inference and
item-local inference fallback remain rejected before definition writes.

```fuwen
macro Pipeline(value: string) {
  activity first = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
  activity second = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: first;) -> string;
  yield second;
}
workflow demo(input: list<string>[2]) -> list<string>[2] {
  group root {
    fanout process over input as item: string key item max 2 concurrency 2 {
      group row {
        use pipeline = Pipeline(value: item);
        yield pipeline;
      }
    } yield row -> list<string>[2];
    yield process;
  }
  return root;
}
```

The selected typed keys and item values are persisted before item admission.
The persisted selection must exactly match its canonical admitted input; changed
completed output fails before dispatch. Duplicate or invalid keys reject before
item dispatch. Results retain selection
order despite concurrent execution. The same key cannot acquire a different
input in an admitted scope. Selection membership is fixed for this profile;
reordering source keys preserves item identity while changing the returned order
only when a distinct admitted selection permits it. No in-place membership
change, implicit retirement or evolving epoch API is introduced.

Each selected item has one `row` occurrence and a nested `pipeline` occurrence.
Both use Zhinu's existing v2 keyed item coordinate. The static `root` occurrence
is shared. Each provider operation has its own step execution, revision,
idempotency key and Work membership. Selection, item completion and aggregate
steps carry Control membership. No synthetic group definition is introduced.

The native body contract pins the immutable execution fingerprint. Each claim is
fenced to its selected item and current selection revision. Body operations run
before the item completion callback, which records the settled result. The item
completion depends on every registered body operation; native closure also checks
the child revisions and memberships. Process loss before the next operation
reuses already committed operations. A dependent restart invalidates completion
and closure. `StepOnly` restart is rejected for body steps and item completions
in this profile, because preserving an old item result could conceal a changed
child result. Legacy fan-out behavior remains unchanged.

Provider results continue through the ordinary envelope validation and publication
pipeline. A definitive provider failure can leave a completed native envelope and
aggregate while the Fuwen run fails. Cancellation, fencing and claim failures do
not become successful provider outcomes. Group progress remains mechanical and
conservative about future work; it is not a domain acceptance verdict.

Expanded native scope prefixes use `fuwen-fanout-v1-` plus the full SHA-256 digest
of the structural fan-out path. Item tokens use Fuwen's existing typed-key digest,
which distinguishes string and integer keys. Native child keys encode the item
token and hashed operation name unambiguously. Consumers use
`FuwenZhinuStepMapper`; mapper matching and resolution are qualified against real
persisted steps. Historical macro-free keys retain their original format.

Admission bounds the maximum item-operation product to 4,096 and occurrence
count to Zhinu's 65,536 limit. Derived provider paths must fit existing runtime
identity bounds. The canonical selection and total outcome vectors each have a
1 MiB budget; selection size is checked while materializing items before writing
the selection step. Outcome overflow prevents item/aggregate completion, while
already committed provider work remains in history. Native concurrency and
enumeration limits still apply independently.

Recovery re-admits the exact stored expansion bundle, with no source recompilation
or macro-library lookup. The repeat and static checkpoints retain their separate
qualification evidence. Broader fan-out forms, mixed dynamic composition and
public package adoption remain future work. Current cross-platform qualification
is recorded separately in ZG-7, preserving the original Windows-only checkpoint.

The qualified store is `SqliteWorkflowStore`. Custom stores need the optional
`IWorkflowGroupFanOutBodyRepository` capability in addition to ordinary grouped
step support. A store without that capability fails when native fan-out begins;
the adapter may already have persisted the selection Control step. No provider
operation is dispatched in that case. Registration alone does not qualify a
custom store's dynamic execution support.
