# Explicit bounded grouped selection

Fuwen preview.19 adds `fuwen-ir/v5-selection`, using the exact unpublished Zhinu
preview.10/schema 10 candidate in [the current pin](qualification/activity-groups/zhinu-candidate.json).
The [ZG-7 pin](qualification/activity-groups/zhinu-zg7-candidate.json) preserves
the original preview.9 archives. Existing v1–v4 plans retain their canonical
bytes, fingerprints, lowering and keys. This is an opt-in authoring contract.

```fuwen
fanout process over decide.items
  selection epoch decide.epoch replaces decide.previous max 4
  as item: string key item max 2 concurrency 1 {
  group row {
    activity echo = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: item;) -> string;
    yield echo;
  }
} yield row -> list<string>[2];
```

`epoch` and `replaces` are integer bindings evaluated outside the item scope.
Initial admission requires epoch 1 and replaces 0. Each replacement uses a fresh
larger epoch within the declared maximum and explicitly names the prior admitted
epoch. Bounds are 1–1024 epochs, additionally limited by the aggregate occurrence
and 4,096 item-operation budgets across all epochs. Reappearing item keys belong
to new occurrences. Epochs never derive from item positions or process counters.
Duplicate keys and invalid epoch values fail before body dispatch.

The qualified shape contains at most one root selector provider, one terminal
grouped fan-out, and a direct return of that fan-out's entire result. The item
body retains the existing guaranteed-work requirement, explicit item-local root,
nested static groups/macros, independently durable providers and control-only
conditionals. Additional downstream operations, nested/mixed dynamic scopes,
item waits/checkpoints, value merges, fallback and coordinated inference remain
rejected before definition storage. The selector may be omitted when selection
bindings come from immutable workflow input or literals.

## Restart and recovery

The selection boundary's input identity is structural. Its durable output records
the epoch, replaced epoch, ordered typed keys, values and runtime paths. The source
and epoch bindings are evaluated inside that callback; ordinary replay consumes
its stored output. Restarting a provider alone does not implicitly retire work.
The host explicitly restarts the selector and the selection boundary using native
`RestartStepAsync` with `StepRestartMode.StepOnly`, then executes the run. Obtain
the boundary key from the public mapper's `FanOutSelection` descriptor rather
than parsing stored keys. Reusing an admitted epoch after a boundary restart is
rejected by native admission. This is not a public group retry/cancellation API.

The adapter supplies an epoch-qualified ordinary native fan-out key and sets
`ReplacesFanOutKey`. Native admission atomically retires the previous expansion
and unfinished materialized work before replacement callbacks execute. Historical
completed results, occurrence parents and memberships remain intact; an omitted
old item is not silently reinterpreted. Retirement does not compensate effects.
The store must support native replacement; SQLite preview.10 is qualified.

For v5 only, aggregation and the terminal return are pure projections of durable
item outcomes. They emit no separate fixed-key steps whose inputs could drift
between epochs. Run output remains durable. The v2 selection step map describes
the actual persisted selection/provider/item-completion steps and exposes both
selection-epoch and typed-item scopes. Existing maps remain v1. Provider runtime
identity also includes the epoch, so returning to the same item value cannot
reuse the former provider invocation identity.

## Evidence boundary

Qualification covers contraction, reordered/expanded membership, reappearance,
empty replacement, stable historical rows, mapper round trips and current Work
counts. Fresh processes re-admit stored immutable bundle bytes without source
compilation. They are killed during an old branch, then during partial replacement;
recovery completes the persisted replacement even after the external selector
input is made invalid. Native Q33 separately qualifies admission transaction
barriers, schema migration and old-worker rejection. Arbitrary condition changes,
unbounded epochs, forked replacement-bearing runs, broader DSL composition and
package publication are outside this profile.
