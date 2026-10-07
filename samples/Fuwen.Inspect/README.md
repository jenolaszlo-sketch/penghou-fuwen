# Fuwen.Inspect

Read-only inspection over persisted canonical plan files: the first runnable
consumer of the Fuwen plan contracts that never compiles, admits, authorizes,
or executes anything.

```powershell
dotnet run --project samples/Fuwen.Inspect/Fuwen.Inspect.csproj -c Release -- verify plan.json <execution-fingerprint>
dotnet run --project samples/Fuwen.Inspect/Fuwen.Inspect.csproj -c Release -- validate plan.json
dotnet run --project samples/Fuwen.Inspect/Fuwen.Inspect.csproj -c Release -- explain plan.json
```

- `verify` checks integrity against a claimed execution fingerprint
  (`WorkflowDefinitionDocument.LoadVerified`) and reports the fingerprint
  plus the canonical byte size.
- `validate` checks structural invariants (`WorkflowPlanValidator`) and
  reports the node count. This is a shape check, not semantic admission.
- `explain` validates, then projects declared structure: plan identity and
  fingerprints, the node tree with activity descriptors and neutral
  execution intents, declared descriptor pins, the declared capability
  manifest, and execution-order shape.

Pins and capabilities are reported **as declared**, never as resolved,
granted, or admitted; compiling and admitting a plan remains the host
pipeline's job. Each command prints one JSON record and exits `0` on
success, `2` otherwise. This sample is not packed or published.
