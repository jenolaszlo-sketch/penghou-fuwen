# Penghou.Fuwen

[![CI](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/workflows/ci.yml)
[![NuGet Compiler](https://img.shields.io/nuget/vpre/Penghou.Fuwen.Compiler?label=NuGet%20Compiler)](https://www.nuget.org/packages/Penghou.Fuwen.Compiler)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-fuwen)](LICENSE)

Penghou.Fuwen is a typed workflow language, compiler, and immutable intermediate
representation for portable, artifact-driven AI workflows. It turns bounded,
capability-reviewed source into a canonical executable plan. Penghou.Zhinu
executes that plan durably; Fuwen does not replace the workflow engine.

Fuwen treats code, documents, datasets, research, media, model outputs,
manifests, and validation evidence as typed immutable artifact references.
Workflow source never embeds C#, Python, JavaScript, shell, credentials, or
unrestricted filesystem access.

```text
.fuwen source
    -> bounded parser and semantic compiler
    -> exact catalogue resolution and capability review
    -> immutable canonical WorkflowPlan + execution fingerprint
    -> host admission receipt
    -> durable execution through Penghou.Fuwen.Zhinu
```

## Why Fuwen

Fuwen makes the parts of an AI workflow that affect safety, reproducibility,
and replay visible before execution:

- typed inputs, outputs, bindings, schemas, enums, and artifact references;
- exact version-and-digest pins for activities, contexts, inference profiles,
  prompt templates, and tools;
- workflow-owned prompts with typed parameters and canonical rendering;
- capability, resource-budget, callable-effect, idempotency, and retry checks;
- stable structural node identities, source maps, and execution fingerprints;
- bounded conditionals, keyed fan-out, repeat regions, checkpoints, and waits;
- immutable plan revisions and deterministic change explanations.

The host remains responsible for trusted catalogues, credentials, artifact
storage, authorization, admission policy, and resource limits. Provider output
is untrusted until it passes the declared Fuwen type.

## Packages

All packages target .NET 8 and .NET 10.

| Package | Responsibility |
| --- | --- |
| `Penghou.Fuwen` | Immutable IR, type system, bindings, artifacts, identities, runtime values, execution ports, and evidence contracts |
| `Penghou.Fuwen.Compiler` | Bounded source parsing, formatting, catalogue resolution, validation, compilation, diagnostics, admission, and plan comparison |
| `Penghou.Fuwen.Zhinu` | Admission-bound durable interpretation of Fuwen plans on Penghou.Zhinu |
| `Penghou.Fuwen.Baize` | Exact descriptor-bound structured and media inference through Penghou.Baize |

Install only the layers your host needs:

```bash
dotnet add package Penghou.Fuwen.Compiler --prerelease
dotnet add package Penghou.Fuwen.Zhinu --prerelease
dotnet add package Penghou.Fuwen.Baize --prerelease
```

## Current language and IR

The current source compiler emits the smallest IR version required by the
authored features, up to `fuwen-ir/v8`.

| IR | Added contract |
| --- | --- |
| v2 | Explicit structured execution order |
| v3 | Typed context requirements and runtime snapshot evidence |
| v4 | Bounded keyed fan-out with stable item identities |
| v5 | Value-producing conditional merges |
| v6 | Bounded state-carrying repeat regions |
| v7 | Checkpoint and external wait interaction gates |
| v8 | Workflow-owned prompts and declared inference tools |

The source language supports schemas, enums, capability declarations, typed
workflows, context/activity/inference nodes, restricted bindings, conditionals,
fan-out, repeat, checkpoints, waits, prompt declarations, toolsets, and complete
typed returns.

A minimal IR v8 workflow looks like this:

```fuwen
prompt greet(name: string) {
  system "Answer briefly."
  user "Greet {{ name}}."
}

workflow greeting(input: string) -> string {
  infer answer = infer
    "sample.profile@1#dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    prompt greet(name: input;)
    tools none
    -> string;

  return answer;
}
```

Descriptor pins are resolved against the host's trusted catalogue. Prompt and
tool declarations are part of plan identity: changing their semantic content
changes the execution fingerprint. Inference admits effect-free and read-only
tools, plus retry-safe writes whose callable contract is idempotent.

See the [authoring contract](docs/fuwen-authoring.md) for the complete compact
syntax and catalogue inputs. The [capability matrix](docs/capability-matrix.md)
distinguishes complete, limited, deferred, and deliberately host-owned surfaces.

## Compile, admit, execute

`FuwenSourceCompiler` lexes, parses, binds, resolves exact descriptors, applies
host and caller budgets, and lowers source through the same semantic validator
used for programmatic plans. A successful compilation returns a canonical
`WorkflowPlan`, diagnostics, source map, and resource-usage summary.

Compilation alone does not authorize execution. `WorkflowAdmissionService`
binds the exact execution fingerprint to:

- an immutable trusted-catalogue snapshot;
- resolved callable metadata and capabilities;
- the host policy revision and finite grants;
- the effective compilation limits.

The resulting opaque receipt is process-bound authority. Plan-revision
documents and fingerprints prove identity and lineage; they do not grant
capabilities or activate work.

`Penghou.Fuwen.Zhinu` supports IR v3 through v8. Its sequential interpreter
executes context, inference, activity, conditional, fan-out, repeat, checkpoint,
wait, and return semantics as stable Zhinu work. It verifies admission,
definition storage, runtime identity, and workflow fingerprints before
registration. Durable tests cover crash recovery, selective restart, focused
fan-out recovery, bounded loop replay, interaction gates, cancellation,
definition drift, corrupt evidence, and idempotent artifact publication.

`Penghou.Fuwen.Baize` resolves host-owned logical bindings to exact Baize
endpoints. It records provider/model identity, attempts, usage, duration,
pricing revision, tools, and artifact-publication evidence. Retries occur only
for explicitly classified representation or fallback failures.

## Identity and evolution

Fuwen keeps three concepts separate:

1. the canonical executable-plan fingerprint;
2. the authored source identity and source map;
3. the plan-revision lineage and supporting evidence.

This separation lets a host explain why a revision exists without allowing
prose, timestamps, or source formatting to perturb executable identity.
`WorkflowExplanation` and `PlanRevisionComparer` are deterministic inspection
surfaces; neither can authorize execution or reuse runtime artifacts.

The canonical formatter is idempotent. Comments and whitespace may change
during formatting, while the executable plan remains identical. Stable
diagnostic codes (`FWN-*`) and UTF-8 source spans are intended for bounded
machine-assisted repair.

## Ecosystem boundaries

- **Fuwen** owns source syntax, typing, validation, immutable IR, fingerprints,
  source maps, admission, and diagnostics.
- **Zhinu** owns durable scheduling, retries, fencing, signals, restart,
  cancellation, child workflows, compensation, loops, and persistence.
- **Baize** owns provider-neutral model execution and provider provenance.
- **Nuwa** may repair malformed JSON; final Fuwen type validation still decides
  whether the value is accepted.
- **Cangjie and Hetu** provide memory and code/context knowledge through
  immutable references.
- **Hongxian** owns long-lived session continuity and correlation, not workflow
  execution.
- **Hosts and artifact providers** own bytes, storage, authorization, routing,
  credentials, and budgets.

## Development

```powershell
dotnet build Penghou.Fuwen.slnx --configuration Release
dotnet test Penghou.Fuwen.slnx --configuration Release --no-build
dotnet pack Penghou.Fuwen.slnx --configuration Release --no-build --output artifacts
```

Useful design references:

- [Roadmap and delivery status](docs/roadmap.md)
- [Current capability matrix](docs/capability-matrix.md)
- [Bounded complex-activity direction](docs/complex-activities.md)
- [Complex-inference implementation plan](docs/complex-activities-implementation-plan.md)
- [Execution ports](docs/execution-ports.md)
- [Zhinu adapter boundary](docs/zhinu-adapter.md)
- [Runtime values and evidence](docs/runtime-evidence.md)
- [Keyed fan-out](docs/keyed-fanout.md)
- [Execution-port conformance](docs/execution-conformance.md)
- [Generated-asset publication](docs/generated-asset-publisher.md)
- [Identity and fingerprints](docs/identity-and-fingerprints.md)
- [Threat model](docs/threat-model.md)

The packages are preview software. Source syntax and public APIs may still
change between preview releases; persisted plans remain governed by their
explicit IR, compiler-semantics, canonicalization, and fingerprint versions.

## License

[Apache-2.0](LICENSE)

Copyright (c) 2026 Jenő Konrád László
