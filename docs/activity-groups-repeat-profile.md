# Activity groups: single-level repeat integration

ZG-6.2a extends the [static profile](activity-groups-static-profile.md) with one
explicit repeat level. Nested macros/logical groups inside the body expand to
independently durable activity, context, one-call inference and checkpoint steps,
plus native iteration-scoped signal waits. Conditions and continue/break remain
the adapter's existing pure control decisions; conditional merge is a durable
Control step. Grouped coordinated inference and repeat-local inference fallback
remain rejected, as do nested repeats and fan-out. This is not full ZG-6/Q45.

```fuwen
macro Echo(value: string) {
  activity work = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
  checkpoint saved value work -> string;
  yield saved;
}
workflow demo(input: string) -> string {
  group stage {
    repeat loop max 3 state current: string = input {
      use call = Echo(value: current);
    } continue call break iter == 3 -> string;
    yield loop;
  }
  return stage;
}
```

The `stage` group has one root occurrence. `call` has a separate occurrence for
each iteration, parented to that same stage. Groups nested inside `call` have
the matching iteration parent. No synthetic group is inserted for the repeat.
The iteration's native lineage establishes identity; the adapter does not derive
occurrence IDs from input values, source positions or a new identity codec.

Expanded repeats require v4 IR and their pinned companion. Native loop names use
`fuwen-repeat-v1-` plus the SHA-256 hex digest of the structural repeat path.
Body names use `fuwen-node-v1-` plus the complete SHA-256 digest encoded as
unpadded base64url; merge adds `-merge`. The resulting names fit both native loop
and Fuwen runtime identity bounds. Two macro invocations can therefore reuse
local loop/body names without aliasing. Historical macro-free plan keys are
unchanged. Consumers use `FuwenZhinuStepMapper`; they need not reconstruct keys.

Loop condition, state commit, completion and limit evidence carry Control
membership in the repeat node's enclosing group. Independently durable body
operations carry their direct innermost Work/Control memberships. Body binding
dependencies resolve to the current iteration's actual durable keys; dependencies
on repeat results resolve to native completion keys. Completed iterations replay
from native commits; a partially completed iteration reuses committed body steps.
Occurrence caches are scoped to a workflow execution and released per iteration.

Signal waits use the native iteration wait API directly, including its condition
and ambient dependency edges. They do not wrap a second wait inside a durable
callback holding the same step lock. This also fixes the legacy ungrouped
repeat-wait deadlock: the new native iteration API preserves the original
ordinary-step JSON string input/hash contract and the adapter keeps historical
step keys. Recovery from an old-style in-flight iteration claim is qualified
separately; root signal-wait serialization remains unchanged.

Recovery continues to load the exact immutable expansion bundle and re-admit it
against the host catalogue, without consulting source or macro libraries. Group
membership changes no authority, retry policy or provider outcome interpretation.
Current repeated members do not prove expansion closure or an exact future total;
existing Unknown/Partial coverage remains conservative.

The [qualification record](qualification/activity-groups/zg6-repeat.md) defines
the exact source, package and process-loss evidence.
The current candidate closure is recorded in
[the package pin](qualification/activity-groups/zhinu-candidate.json).
The earlier static checkpoint retains
[its original preview.5 pin](qualification/activity-groups/zhinu-static-candidate.json).
Packages remain unpublished. Fan-out per-operation lowering, broader composition,
Linux CI, public dependency adoption and full cross-repository qualification
remain open.
