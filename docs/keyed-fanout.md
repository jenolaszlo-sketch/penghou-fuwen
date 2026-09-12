# Keyed fan-out contract

IR v4 adds `FanOutNode`, a bounded structured region with an
explicit source collection, item binding, key binding, body, yield binding, and
list result type. The compiler validates the source and result item types,
positive item/concurrency bounds, closed body references, and the explicit
execution region. A body may use its current item and earlier body outputs; it
cannot reach into an outer region or leak an implementation node through a
generic reference.

At execution, the adapter evaluates the complete source and canonicalizes every
key before starting child work. Null and duplicate keys fail before any item
step runs. The item path is derived from the fan-out structural path and the
canonical typed key, so source reordering does not change durable item identity.
Outcomes are persisted independently under the item path and aggregated in
source order. The aggregate depends on every item step; Zhinu's dependency-aware
restart therefore reuses successful siblings while invalidating the aggregate
and downstream dependents.

Zhinu 0.1.0-preview.12's `FanOutAsync` derives keys from positional indexes. That
does not satisfy Fuwen's keyed identity contract, so this adapter composes
durable `StepAsync` calls with stable keyed step paths and a source-ordered
aggregate step. The host concurrency ceiling is applied in addition to the
plan's lower bound.
