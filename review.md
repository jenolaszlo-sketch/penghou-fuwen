# Penghou.Fuwen implementation and design review

Reviewed: 2026-09-13 (initial delivery and Baize integration).
Updated: 2026-09-17 (Stages 1–4 review: IR v4–v7 evolution, repeat loops, value-producing conditionals, interaction gates, and Guyabano pilot readiness).

Scope: correctness, compiler/runtime contracts, Baize adapters, Zhinu durable execution, artifact publication, OOP/design patterns, usability, usefulness, and test coverage across all current features.

## Assessment and verification

Release-hardening status (2026-09-17, second pass):
- R01–R05 are resolved with regressions in the test suite.
- R06–R12 remain tracked follow-up items; several parts were hardened (R09 bounded raw provider output, R11 cross-adapter matrix, R12 publisher contract).
- Recent work delivered Stage 1 (fan-out DSL), Stage 2 (IR v5 value-producing conditionals with `merge`), Stage 3 (IR v6 bounded `repeat` loops), and Stage 4 (IR v7 `checkpoint` and `wait` interaction gates).
- Full solution tests pass: 388 logical tests on both .NET 8 and .NET 10, totaling 776 passing executions with zero failures or skips (Core: 160, Compiler: 156, Baize: 33, Zhinu: 39).
- The blocking cross-feature correctness defects R13–R16 are now resolved with regression fixtures, and the abstraction/performance/versioning items R17–R19 are resolved. R07 and R20–R23 remain tracked follow-up items.

## Correctness findings

### R01 — P1: Valid inference lists cannot be consumed by Zhinu fan-out

Status: resolved. Fan-out now normalizes any admitted list representation at
the typed execution boundary, and a durable Baize-to-Zhinu test proves replay
without reinvoking the provider.

Evidence: `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`, `ExtractOutputAsync`; `src/Penghou.Fuwen.Compiler/RuntimeValueValidator.cs`, JSON-list validation; `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, `ExecuteInferenceAsync` and `ExecuteFanOutAsync`.

Recommendation: preserve shared typed normalization across all execution ports.

### R02 — P2: Prompt substitution rewrites literal argument content

Status: resolved. Prompt placeholders are expanded in one pass, so inserted
argument and context JSON is never scanned as template syntax.

Evidence: `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`, `CreateRequest`.

### R03 — P2: Missing usage allows retries despite a configured cost ceiling

Status: resolved conservatively. Missing or partial billable usage is recorded
as unknown cost and prevents another priced attempt when a ceiling is active.

Evidence: `BaizeTokenPricing.Calculate`, `ShouldRetry`, and cost aggregation in `src/Penghou.Fuwen.Baize/BaizeInferenceExecutor.cs`.

### R04 — P2: Zhinu drops inference evidence when success processing fails

Status: resolved. Available inference evidence is retained in the durable
failure envelope when post-provider validation or publication fails.

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, catch around `buildSuccess(result)` in `ExecuteProviderAsync`.

### R05 — P2: Generation polling follows replacement handles without checking identity

Status: resolved. Submission must match the configured provider, endpoint, and
model; polling pins those identities plus the operation identity while
permitting opaque continuation metadata to refresh.

Evidence: `src/Penghou.Fuwen.Baize/BaizeGenerationInferenceExecutor.cs`, `ExecuteDurablyAsync`.

### R13 — P1: Value-producing conditionals (`merge`) are rejected on IR v7 plans

Status: resolved (2026-09-17). `WorkflowPlanValidator` now gates merges via `IrVersions.SupportsConditionalMerge` (IR v5+); regression test combines merge with checkpoint and wait on v7.

Evidence: `src/Penghou.Fuwen/WorkflowPlanValidator.cs`, lines 36–38 (before fix):
```csharp
if (conditional.Merge is not null)
{
    if (!string.Equals(plan.IrVersion, FuwenContracts.IrVersionV5, StringComparison.Ordinal) &&
        !string.Equals(plan.IrVersion, FuwenContracts.IrVersionV6, StringComparison.Ordinal))
        throw new ArgumentException($"Value-producing conditionals require IR v5 or v6, not '{plan.IrVersion}'.", nameof(plan.Nodes));
    ValidateType(conditional.Merge.ResultType);
}
```

When Stage 4 introduced IR v7 (`checkpoint` and `wait` interaction gates), `WorkflowCompiler.cs` line 650 was updated to accept v7, but `WorkflowPlanValidator.cs` line 36 was not updated. Any workflow combining an interaction gate (`checkpoint` or `wait`) and a conditional `merge` will be assigned `fuwen-ir/v7` by the builder/compiler, causing `WorkflowPlanValidator.Validate` to throw:
`ArgumentException: Value-producing conditionals require IR v5 or v6, not 'fuwen-ir/v7'.`

Recommendation: update `WorkflowPlanValidator.cs` line 36 to permit `FuwenContracts.IrVersionV7`. Add a compiler and validator fixture that combines a conditional merge with checkpoint and wait nodes.

### R14 — P1: Typed context requirements are rejected on all IR v6 and IR v7 plans

Status: resolved (2026-09-17). Both `WorkflowPlanValidator.ValidateCompatibility` and `WorkflowCompiler` semantic validation now use `IrVersions.SupportsTypedContextRequirements` (IR v3+); regression tests admit inference with requirements on v6/v7 and still reject v1/v2.

Evidence: `src/Penghou.Fuwen/WorkflowPlanValidator.cs`, lines 88–99 (before fix):
```csharp
if (!isV3 && !isV4 && !isV5 && FlattenNodes(plan.Nodes).OfType<InferenceNode>().Any(static inference => inference.ContextRequirements is not null))
    throw new ArgumentException(
        "Typed context requirements are only supported by IR v3; historical v1/v2 plans are never silently upgraded.",
        nameof(plan.Nodes));
if ((isV3 || isV4 || isV5) && FlattenNodes(plan.Nodes).OfType<InferenceNode>().Any(static inference => inference.ContextSnapshots.Count != 0))
    throw new ArgumentException(
        "IR v3 uses typed context requirements and does not accept legacy context snapshots.",
        nameof(plan.Nodes));
if ((isV3 || isV4 || isV5) && FlattenNodes(plan.Nodes).OfType<InferenceNode>().Any(static inference => inference.ContextRequirements is null))
    throw new ArgumentException(
        "IR v3 requires a non-null ContextRequirements collection on every inference node.",
        nameof(plan.Nodes));
```

The validator uses negative check `!isV3 && !isV4 && !isV5` intending to catch legacy v1/v2 plans. When a plan contains `repeat` (IR v6) or `checkpoint`/`wait` (IR v7), `isV3`, `isV4`, and `isV5` are all false. Therefore, any modern workflow with an `InferenceNode` carrying `ContextRequirements` (the standard way Fuwen connects context snapshots to inference) triggers the exception:
`ArgumentException: Typed context requirements are only supported by IR v3; historical v1/v2 plans are never silently upgraded.`
Furthermore, lines 92 and 96 only check `(isV3 || isV4 || isV5)`, so legacy context snapshots on v6/v7 are not properly forbidden, and null requirements are not checked.

Recommendation: change line 88 to explicitly check legacy versions `(isV1 || isV2)`. Change lines 92 and 96 to check all modern versions `(isV3 || isV4 || isV5 || isV6 || isV7)`. Add test fixtures combining inference nodes with context requirements alongside repeat, checkpoint, and wait nodes.

### R15 — P2: `PlanRevisionComparer` silently ignores `conditional.Merge`

Status: resolved (2026-09-17). `NodeSemantics` now includes `Merge.ResultType` and `NodeDependencies` includes `Merge.ThenValue`/`Merge.ElseValue`; regression tests cover result-type change (Changed) and branch retarget (DependencyChanged).

Evidence: `src/Penghou.Fuwen/PlanRevisionComparer.cs`, `NodeSemantics` and `NodeDependencies` (before fix):
```csharp
ConditionalNode value => new { Kind = "conditional", value.Condition.Operator },
```
and
```csharp
ConditionalNode value => new
{
    value.Condition.Left,
    value.Condition.Right,
    Then = value.Then.Select(static child => child.StructuralPath).ToArray(),
    Else = value.Else.Select(static child => child.StructuralPath).ToArray(),
},
```

Neither helper includes `value.Merge`. If a plan revision adds a `merge`, removes a `merge`, alters `Merge.ResultType`, or modifies `Merge.ThenValue` / `Merge.ElseValue` bindings, `PlanRevisionComparer` will report `PlanChangeKind.Unchanged` for that node. This violates Fuwen's contract that changes between admitted plan revisions produce deterministic semantic difference records.

Recommendation: add `value.Merge?.ResultType` to `NodeSemantics` and `value.Merge?.ThenValue`, `value.Merge?.ElseValue` to `NodeDependencies`. Add comparison test cases for conditional merge mutations.

### R16 — P1: LLM inference cannot execute inside `repeat` loops in the Zhinu runtime

Status: resolved (2026-09-17). Repeat bodies now support `context` and `inference` nodes end to end: DSL parser allow-list, `FuwenZhinuWorkflowFactory` executable subset, and durable `ExecuteRepeatContextAsync` (envelope-persisted snapshots, replay-safe) plus `ExecuteRepeatInferenceAsync` via `iteration.StepAsync`. Loop-local context is required because compiler closed-region rules forbid outer-region bindings inside `$body`. Durable SQLite test proves per-iteration context+inference with replay without reinvoking providers.

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, `ExecuteRepeatRegionAsync` (before fix):
```csharp
switch (bodyNode)
{
    case ActivityNode activity:
        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatActivityAsync(...);
        break;
    case ConditionalNode conditional:
        await ExecuteRepeatConditionalAsync(...);
        break;
    case CheckpointNode checkpoint:
        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatCheckpointAsync(...);
        break;
    case WaitNode wait:
        state.Outputs[bodyNode.StructuralPath] = await ExecuteRepeatWaitAsync(...);
        break;
    default:
        throw new FuwenZhinuAdapterException($"Repeat bodies currently support activity nodes, conditionals, checkpoints, and waits; '{bodyNode.GetType().Name}' at '{nodePath}' is not executable here.");
}
```

The loop region dispatcher explicitly excludes `InferenceNode` and `ContextNode`.
However, the stated purpose of Stage 3 (`guyabano-prep-plan.md`) is unlocking Guyabano's build/repair cycles (max 6) and review passes. In any repair or review loop, the core repeated operation is an LLM inference step (e.g. reviewing code diffs, generating repair patches, evaluating test outcomes). While `InferenceNode` inside `RepeatNode` passes compiler syntax and structural validation, attempting to run it durably under Zhinu immediately crashes with `FuwenZhinuAdapterException`.

Recommendation: implement `ExecuteRepeatInferenceAsync` in `FuwenZhinuSequentialInterpreter.cs` (mirroring `ExecuteInferenceAsync`, but recording steps and durable execution envelopes through `iteration.StepAsync`), and add durable tests proving repeat loops containing Baize inference calls.

## Design, OOP, and usability improvements

### R06 — Normalize runtime values through a shared typed boundary

Status: partially resolved. Fan-out now normalizes lists; new public `RuntimeValueJson.ToJsonElement` lets hosts convert any representation (JSON, nominal composites, artifacts) without assuming `JsonRuntimeValue` — proven by Guyabano stage executors consuming normalized outputs. A comprehensive shared normalization helper across all ports and interpreters remains open.

### R07 — Separate interpreter responsibilities internally

Status: open. `FuwenZhinuSequentialInterpreter.cs` has grown to ~1,700 lines. It combines workflow scheduling, execution phase barriers, condition evaluation, wire serialization (`RuntimeValueWire`), fan-out coordination, loop state management, and envelope validation. Extract cohesive internal collaborators (`FuwenConditionEvaluator`, `FuwenRuntimeValueWire`, `FuwenRepeatCoordinator`, `FuwenFanOutCoordinator`).

### R08 — Make accounting and generation recovery contracts explicit

Status: open. Tracked for provider durable reconciliation.

### R09 — Bound raw provider output before parsing and repair

Status: resolved. Trusted profiles now enforce raw and repaired response ceilings.

### R10 — Improve host setup and authoring feedback

Status: open. See R22 and R23 below.

### R11 — Extend cross-adapter conformance

Status: resolved for the first preview.

### R12 — Specify the generated-asset publisher contract precisely

Status: resolved for the first preview.

### R17 — OOP / Abstraction: `FuwenSource` hard-codes concrete type-cast to `InMemoryTrustedCatalogue`

Status: resolved (2026-09-17). New `ITrustedCatalogueDiscovery` interface (`TryGetDescriptor` by kind/name/version and by exact reference) is implemented by `InMemoryTrustedCatalogue`; `FuwenSource` programs against the interface, so custom catalogues can opt in without the concrete cast.

Evidence: `src/Penghou.Fuwen.Compiler/FuwenSource.cs` (before fix):
```csharp
if (catalogue is InMemoryTrustedCatalogue memory)
{
    var found = memory.Descriptors.FirstOrDefault(item => item.Descriptor.Kind == kind &&
        item.Descriptor.Name == name && item.Descriptor.Version == version);
    if (found is not null) return found.Descriptor;
}
```
and
```csharp
if (catalogue is InMemoryTrustedCatalogue memory)
{
    var found = memory.Descriptors.FirstOrDefault(item => item.Descriptor.Equals(descriptor));
    if (found?.CallableContract is not null) return found.CallableContract.Signature.OutputType;
}
```

`ITrustedCatalogue` is defined as a public interface (`ResolveAsync(DescriptorReference, ...)`), but the `.fuwen` language compiler hard-codes an `is InMemoryTrustedCatalogue` pattern match. If a host passes a custom `ITrustedCatalogue` implementation (e.g. SQLite-backed, caching, or service-hosted catalogue), the compiler cannot resolve descriptors by `name@version` and falls back to a dummy zero-hash descriptor, with all callable output types defaulting to `PrimitiveType(Json)`.

Recommendation: extend `ITrustedCatalogue` with descriptor discovery/lookup capabilities (e.g. `TryGetDescriptor(DescriptorKind kind, string name, string version, out DescriptorReference descriptor)` or an indexing interface) so that `FuwenSource` relies strictly on interface abstractions rather than a concrete test implementation.

### R18 — Performance: Condition evaluation relies on full canonical JSON serialization

Status: resolved (2026-09-17). `Equal`/`NotEqual` now use a `ScalarEqual` fast path (ordinal strings, decimal numbers, booleans, null) and only fall back to canonical JSON bytes for complex values.

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs` (before fix):
```csharp
var leftJson = RuntimeValueWire.ToJson(left);
...
return CanonicalJson.Canonicalize(leftJson).AsSpan().SequenceEqual(CanonicalJson.Canonicalize(RuntimeValueWire.ToJson(right!)));
```

Every condition evaluation (`==`, `!=`, `<`, `<=`, `>`, `>=`) serializes `RuntimeValue` instances to JSON, converts them into canonical byte arrays via `CanonicalJson.Canonicalize`, and performs byte span comparisons. In high-frequency loop iterations (e.g. `repeat max 1000` checking `iter == 3`), serializing and canonicalizing JSON on every step allocates transient buffers and burns CPU cycles unnecessarily.

Recommendation: add fast-path scalar comparisons for primitive types (`long`, `double`, `bool`, `string`) when comparing runtime values, falling back to canonical JSON bytes only for complex objects or detached JSON payloads.

### R19 — Architecture: Fragile string-based `IrVersion` checking across subsystems

Status: resolved (2026-09-17). New `Penghou.Fuwen.IrVersions` helper (ordinals V1–V7 plus `SupportsExecutionOrder`, `SupportsTypedContextRequirements`, `SupportsFanOut`, `SupportsConditionalMerge`, `SupportsRepeat`, `SupportsInteractionGates`) centralizes gating; `WorkflowPlanValidator`, `WorkflowCompiler` (merge/repeat/context/wait gates), and `FuwenZhinuWorkflowFactory` (adapter IR range) now use it.

Evidence: version checks across Core, Compiler, Zhinu, and Baize used scattered string equality checks (before fix):
`!string.Equals(plan.IrVersion, FuwenContracts.IrVersionV5, StringComparison.Ordinal) && !string.Equals(plan.IrVersion, FuwenContracts.IrVersionV6, StringComparison.Ordinal)`

Each new IR feature requires manually updating dozens of string comparisons across 4 separate projects. Overlooking even one location was the exact cause of R13 (v7 merge rejection) and R14 (v6/v7 context rejection).

Recommendation: model IR versions with an ordinal enum (`IrVersion.V7`) or feature capability flags (`plan.SupportsFeature(IrFeatures.ConditionalMerge)`), allowing clean range comparisons (`plan.IrVersion >= IrVersion.V5`) rather than hardcoded combinatorial string checks.

### R20 — Usability: DSL mandates 64-character hex digests in source text

Status: open.

Evidence: `src/Penghou.Fuwen.Compiler/FuwenSource.cs`, lines 1153–1178.

When authoring `.fuwen` files without an in-memory catalogue that pre-indexes digests, authors are forced to write:
`activity step = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: s;) -> string;`
Requiring raw 64-character SHA-256 digests in authored source text is extremely brittle, unreadable, and hostile to human editing or code reviews.

Recommendation: allow human-authored `.fuwen` files to reference logical names (`sample.echo@1`), and provide a lockfile/manifest tool (`fuwen.lock` or catalogue manifest) that pins and verifies the content digests at compilation time.

### R21 — Usability: Sparse authoring documentation and lack of end-to-end samples

Status: open.

Evidence: `docs/fuwen-authoring.md` is only 21 lines long, and the repository contains no `samples/` directory.

External consumers attempting to integrate Fuwen (such as Guyabano, Qingniao, or Marang developers) have no comprehensive grammar specification, syntax guide, or runnable sample demonstrating how to take a `.fuwen` file, compile it against a catalogue, obtain an admission receipt, and execute it with Baize and Zhinu.

Recommendation: expand `docs/fuwen-authoring.md` into a complete syntax and keyword reference, and provide a `samples/` directory with a self-contained, end-to-end runnable workflow sample.

### R22 — Usability / Host Integration: Heavy host setup ceremony

Status: open.

Evidence: executing a single Fuwen workflow currently requires manually instantiating and wiring ~8 low-level abstractions:
`ITrustedCatalogue`, `CapabilityGrantPolicy`, `WorkflowCompiler`, `WorkflowAdmissionService`, `FuwenZhinuExecutionPorts`, `FuwenZhinuProviderRuntimeIdentity`, `FuwenZhinuWorkflowFactory`, `WorkflowRegistry`, and `WorkflowEngine`.

Recommendation: provide a fluent host builder (`FuwenHostBuilder`) and `Microsoft.Extensions.DependencyInjection` extensions (e.g. `services.AddFuwenZhinu(...)`) to simplify host adoption in Guyabano, Qingniao, and ASP.NET Core services.

### R23 — Usability: Lack of formatted diagnostic reporting for CLI/terminal

Status: open.

Evidence: `CompilerDiagnostic` produces structured codes, severity, message, and `SourceSpan`, but the compiler provides no standard ANSI or source-excerpt formatter (like Rust or Roslyn-style caret underlines: `^^^`). Diagnosing syntax or type errors in authored `.fuwen` files from logs or terminal outputs requires manual offset calculation.

Recommendation: add a `DiagnosticFormatter` that renders source lines with context, line/column numbers, and caret indicators for human readability.

### R24 — P2: Object literals cannot satisfy named object schemas in bindings

Status: open (found 2026-09-17 during Guyabano real-planning slice 2).

Evidence: `src/Penghou.Fuwen.Compiler/WorkflowCompiler.cs`, `LiteralMatches` — the `NamedTypeReference` branch only accepts enum schemas; object literals against `ObjectSchemaDefinition` fall through to `_ => false`. A repeat loop carrying a typed envelope state (`{ok, domain, error}`) therefore cannot seed its initial state from an inline literal and must use a field-wise `ObjectBinding` instead. The same wall blocks DSL-authored object literals against named schemas.

Recommendation: extend `LiteralMatches` to validate object literals field-wise against `ObjectSchemaDefinition` (required-field presence, recursive field matching, unknown-field rejection), mirroring the `ObjectBinding` branch of `ValidateBinding`. Add compiler fixtures for valid/invalid object literals.

### R25 — P3: Callable contracts have no optional parameters

Status: resolved (2026-09-17). `ValidateCallableNode` skips the missing-argument error for `OptionalType`-typed parameters; hosts observe only supplied arguments. Backwards compatible: previously admitted plans passing explicit `null` literals keep admitting (null still matches `OptionalType`), and omission vs. explicit null produce distinct but deterministic fingerprints. Repair guidance text updated accordingly. Regression tests cover omitted-optional, explicit-null, and omitted-required cases.

Evidence: `src/Penghou.Fuwen.Compiler/WorkflowCompiler.cs`, `ValidateCallableNode` (before fix): every declared signature parameter had to be supplied at every call site, even one typed `OptionalType`.

### R26 — P1: LLM inference and context cannot execute inside fan-out bodies in the Zhinu runtime

Status: resolved (2026-09-17). `ContextNode` and `InferenceNode` are now allowed in fan-out bodies across the DSL parser, the factory executable subset, and the sequential interpreter (`ExecuteFanOutContextAsync` / `ExecuteFanOutInferenceAsync`). The enclosing item step is the durable boundary, so no per-node iteration steps are needed; snapshots live in item-scoped state for same-item inference requirements. Durable + DSL regression tests included.

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuWorkflowFactory.cs`, `ValidateExecutableSubset`, and `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, `ExecuteFanOutRegionAsync`: fan-out bodies accept only activity nodes and control-only conditionals, and the `.fuwen` parser (`FuwenSource.cs`, `ParseFanOutBody`) rejects `context`/`infer`. Per-context staged work (e.g. contract/component design per bounded context) therefore cannot run inside fan-out bodies although repeat bodies support inference since R16.

Recommendation: allow `ContextNode` and `InferenceNode` in fan-out bodies across the DSL parser, the factory executable subset, and the sequential interpreter (per-item step is the durable boundary, so no per-node iteration steps are needed; item-scoped snapshots serve same-item inference requirements). Add durable tests proving fan-out bodies containing context and inference calls.

### R27 — P2: Repeat initial state cannot consume parent-region outputs

Status: resolved (2026-09-17). The initial state is evaluated once before the first iteration in the repeat's own (parent) region, so the compiler now validates it against the repeat's location region instead of a bogus self-path region; loop-state bindings stay body-only, and continue/break remain body-scoped. The DSL parser already exposed outer names at the seed position, so no parser change was needed. Regression tests cover outer-node seeds (DSL, compiler rejection of loop-state seeds, durable execution observing the seed).

Evidence: `WorkflowCompiler.cs` validates `RepeatNode.InitialState` with a consumer whose region is the repeat's structural path (never equal to any real region), `WorkflowPlanValidator` enforces the same boundary structurally, and `FuwenSource.cs` hides outer names inside repeat bodies. Net effect: a repeat loop can only seed from workflow input or literals — a loop over stage N cannot start from stage N-1's output, so retry loops cannot be chained (topology retry cannot consume the domain artifact).

Recommendation: decide whether this closedness is intentional (document it as an IR contract with a dedicated diagnostic) or a defect (validate `InitialState` in the repeat's parent region like every other root-region binding). Either way the DSL, structural validator, and compiler must agree; today all three agree on closed, so runners work around it with input-seeded loops plus strict single attempts downstream.

### R28 — P1: Durable step evidence is bound to a single plan fingerprint and run

Status: resolved (2026-09-18). `FuwenZhinuExecutionPorts.PriorExecutionFingerprints` declares host-trusted prior plans; `ReadEnvelope` then accepts fork-copied evidence when the fingerprint is listed, the structural path matches, the request fingerprint matches, and the runtime path names the same node. Default (empty) preserves strict single-plan checks; claim-time contract checks are untouched. Verified by `tests/Penghou.Fuwen.Zhinu.Tests/FuwenZhinuMutationTests.cs` (cross-version fork reuses declared prior evidence; undeclared prior evidence still fails closed).

Evidence: `src/Penghou.Fuwen.Zhinu/FuwenZhinuSequentialInterpreter.cs`, `ReadEnvelope`: persisted results are rejected unless the invocation fingerprint equals the current plan's fingerprint and the runtime path equals the current run's path. After a Zhinu fork migrates a run to a new plan version, every copied step is rejected even though the store copied it legitimately and claim-time contract checks (key, implementation, input) still apply.

Recommendation: accept host-declared prior execution fingerprints plus run-prefix-independent runtime paths when structural path and request fingerprint match exactly. Keep the default (no priors) strictly single-plan.

### R29 — P1: Inference nodes do not describe their own contract (architectural pivot)

Status: in progress (2026-09-19). Inference nodes reference host-registered prompt templates and profiles by digest, so the workflow carries no instructions and no tool surface. Generated workflows describe the graph without the work; supervisor-authored workflows cannot be understood, compared, or executed without the authoring host's hidden configuration. Recorded as ADR 0005 (`docs/decisions/0005-inference-contracts-are-self-describing.md`).

Phase A steps 1–2 done (2026-09-19): canonical `PromptDefinition` with typed parameters, message roles, `{{ name }}` placeholder validation, and `sha256:prompt-definition/v1` semantic digests; top-level `prompt` declarations with triple-quoted raw text; `WorkflowPlan.Prompts` carriage with null-omitted canonical JSON (v3–v7 fingerprints byte-identical); IR v8 gating end to end (validator, fingerprint v8, `BuildV8`, source `promptSeen` selection, `SupportsWorkflowPrompts`); prompt digests in plan comparison; `WorkflowPlanSnapshot` deep-clones prompts. Verified by `tests/Penghou.Fuwen.Compiler.Tests/FuwenSourcePromptTests.cs`.

Phase A steps 3–4 done (2026-09-19): registered prompt aliases (`prompt foo(...) uses registered "..."`) with catalogue resolution and digest-pinned sources; `InferenceNode.PromptName` + `PromptBindings` with exactly-one-source enforcement (template XOR prompt reference, no descriptor args alongside prompt bindings); structural validation (resolution, coverage with R25-style optional omission, uniqueness) in `WorkflowPlanValidator` plus exact type checking in `WorkflowBindingValidator`; prompt-style profiles skip callable arg/output contracts but keep existence, kind, and effect checks; bindings share the node's region scope like `Arguments`. `PromptTemplate` is now nullable (positional order preserved, no call-site churn); snapshot, payload bounds, catalogue closure, usage accounting, normalization (bindings sorted), and comparison all handle the nullable template and new fields. Verified by 7 additional prompt tests (20 total).

Execution wiring done (2026-09-19): prompt-style inference executes end to end on the sequential adapter. `PromptRenderer` evaluates bindings and renders deterministically (strings raw, everything else canonical JSON; true single-pass substitution; omitted optionals render empty); `InferenceExecutionRequest` carries the definition plus rendered messages with exactly-one-source enforcement; request identity uses a dedicated prompt record (definition digest + binding values + context) so legacy fingerprints are untouched and reuse stays input-sensitive. `BaizeInferenceExecutor` matches prompt bindings by semantic digest, sends rendered messages, and records prompt/rendered digests in evidence; routed and generation executors fail closed with explicit messages. The factory and interpreter accept v8. Verified by renderer/request unit tests, a Zhinu end-to-end execution test, and Baize prompt tests. Full suite green.

Phase B tools done (2026-09-19): `toolset` declarations expand to exact tool lists on inference nodes (`tools` clause accepts a toolset name, an inline list, or `none`; omission means none). Tools resolve through the trusted catalogue and admit only read-only idempotent retry-safe descriptors; destructive, non-retry-safe, duplicate, non-Tool, unregistered, and pre-v8 tools all fail. Tool lists participate in fingerprints (order-normalized), comparison, snapshots, bounds, and usage. Requests and evidence carry admitted tools; `BaizeInferenceExecutor` bounds the model call to the declared set (unbound declarations fail closed) while legacy template requests keep binding-driven tools. Verified by parser/validator/admission tests, fingerprint tests, Baize tool-bounding tests, and a Zhinu tools-carrying execution test. Full suite green (168/195/38/49). No package published yet.

Evidence: `InferenceNode` carries `ProfileDescriptor` + `TemplateDescriptor` only; `FuwenSource.cs` parses no prompt text; execution identity covers descriptor digests but not prompt semantics; there is no tool concept anywhere in the language.

Recommendation: implement in phases under a new IR version (v3–v7 keep current semantics). Phase A (prompts): canonical `PromptDefinition` with typed parameters and message roles, top-level workflow-owned `prompt` declarations, typed bindings with compile-time validation, parent-region binding evaluation, and prompt semantics in execution fingerprints and plan comparison. Phase B (tools): `DescriptorKind.Tool` with side-effect/idempotency metadata, `tools none` default, inline lists and `toolset` declarations, exact admission, and tool semantics in fingerprints. Restrict Phase B initially to none/read-only/idempotent. Guyabano generation guidance and Marang authoring requirements follow Fuwen, not interleaved.

## Suggested implementation order

1. **Immediate Correctness Fixes** (done 2026-09-17):
    - Fix `WorkflowPlanValidator` to permit IR v7 on conditional merge (R13).
    - Fix `WorkflowPlanValidator` typed context check for IR v6 and v7 (R14).
    - Update `PlanRevisionComparer` to inspect `conditional.Merge` semantics and dependencies (R15).
2. **Unblock Guyabano Repair Loops** (done 2026-09-17):
    - Implement repeat inference/context execution in `FuwenZhinuSequentialInterpreter.cs` (R16).
3. **OOP & Abstraction Hardening** (done 2026-09-17):
    - Eliminate concrete `InMemoryTrustedCatalogue` cast in `FuwenSource.cs` (R17).
    - Add scalar fast-path for condition evaluation (R18).
    - Replace string-based IR checks with ordinal/feature checks (R19).
4. **Developer Experience & Architecture** (still open):
    - Refactor `FuwenZhinuSequentialInterpreter.cs` into cohesive collaborators (R07).
    - Relax 64-character hex digests in authored source via lockfile/manifest (R20).
    - Expand `docs/fuwen-authoring.md` and provide runnable samples (R21).
    - Add fluent host configuration / DI extensions (R22) and CLI diagnostic formatter (R23).
