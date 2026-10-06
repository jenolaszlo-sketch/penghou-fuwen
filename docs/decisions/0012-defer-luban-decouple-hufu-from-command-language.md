# ADR 0012: Defer Luban and decouple Hufu from the command language

## Status

Proposed, 2026-10-06.

Scope: Penghou.Luban, Hufu, execution abstractions, Gagamba integration.
Decision type: architectural simplification. Priority: high.

## Context

Luban was introduced to solve an important problem:

> Agents should not receive unrestricted shell access when a much smaller
> executable vocabulary would be sufficient.

That problem remains valid.

However, implementing Luban as a useful shell-like language now implies
substantial additional work:

- parser design,
- expression parsing,
- pipelines,
- object/value semantics,
- variables,
- quoting,
- command discovery,
- parameter binding,
- error handling,
- diagnostics,
- Git commands,
- HTTP commands,
- filesystem commands,
- diff and merge commands,
- testing,
- fuzzing,
- compatibility rules,
- documentation,
- model prompting,
- language evolution,
- runtime maintenance.

Adopting PowerShell-derived syntax reduces some grammar work but does not
eliminate the larger language-runtime problem. Once pipelines and structured
values are required, Luban begins to resemble a language implementation rather
than a small execution DSL.

The essential authority and containment properties can already be implemented
through Hufu, typed execution requests, resource brokers, Gagamba,
workflow-level execution intent, explicit activity authority, and auditing.

Luban is therefore currently high effort, useful but non-essential, and not on
the critical path. The architecture must not depend on it.

## Decision

Development of **Penghou.Luban** is deferred (`Status: Deferred`).

Luban will not be part of the required execution path for the current Penghou
architecture. The project MAY remain in the repository; existing design notes
and experiments SHOULD be retained. New feature development SHOULD stop unless
required to preserve compatibility with existing work. No new dependencies
SHOULD be introduced from core Penghou components to Luban.

Luban SHOULD NOT block Hufu, Fuwen, Zhinu, Gagamba, Baize authority
integration, semantic execution adapters, workflow execution, sandboxing,
authority debugging, or audit work.

Hufu MUST be updated so that:

1. Hufu does not depend on Luban syntax, commands, cmdlets, or language
   semantics.
2. Hufu authorizes neutral execution requests and resolved resource operations.
3. Typed semantic operations can be authorized directly.
4. Native process execution can be authorized independently and delegated to
   Gagamba.
5. A future Luban implementation MAY act as a frontend over these same
   execution abstractions without requiring changes to Hufu.

Luban remains a valid future project, but it is no longer a dependency for
delivering the Penghou authority and execution model.

## Revised architectural principle

The previous architecture risked making Luban the central route for safe agent
execution:

```text
Agent
  |
Luban
  |
Hufu
  |
Execution
```

The new model is:

```text
Agent-authored workflow
        |
        v
Execution request / semantic operation
        |
        v
Hufu
        |
        +---------------------+
        |                     |
        v                     v
Semantic broker        Native execution
        |                     |
        v                     v
Controlled effect           Gagamba
```

Luban MAY later become another producer of execution requests:

```text
Luban
  |
  v
Execution request
  |
  v
Hufu
```

It MUST NOT become a required layer between Hufu and execution.

## Consequences

### Hufu becomes language-neutral

Hufu authorizes **meaning**, not syntax. Hufu MUST NOT know about frontend
vocabulary such as `Get-Content`, `Set-Content`, `Invoke-WebRequest`,
`Get-GitDiff`, or `Merge-File`. It reasons about neutral operations such as
`filesystem.read`, `filesystem.write`, `filesystem.enumerate`,
`http.request`, `git.status`, `git.diff`, `git.stage`, `git.commit`, and
`process.execute`. Exact authority vocabulary MAY differ, but it MUST describe
resources and effects rather than a command language.

### Neutral execution model

A neutral execution abstraction SHOULD be introduced or strengthened, e.g.
`IExecutionRequest` with request types such as `FileReadRequest`,
`FileWriteRequest`, `FileEnumerateRequest`, `HttpRequestExecution`,
`GitStatusRequest`, `GitDiffRequest`, `GitStageRequest`, `GitCommitRequest`,
and `ProcessExecutionRequest`. Hufu evaluates these independently of how they
were produced (Fuwen workflow activities, Zhinu, Baize, application code,
semantic adapters, future Luban syntax, approved tooling).

### Semantic operations preferred where valuable

Where an effect can be represented precisely, prefer a typed semantic
operation (e.g. `FileReadRequest{Path=...}` over
`ProcessExecutionRequest{Executable=cat,...}`; `GitDiffRequest` over
`ProcessExecutionRequest{Executable=git,...}`). Benefits: precise authority,
stronger auditing, easier preflight/policy evaluation, deterministic behavior,
less ambient capability, better VFS/WhatIf integration, easier testing.

### Native execution remains first-class

Not every tool can or should be a semantic operation (`dotnet test`,
`npm test`, compilers, project-specific tools, ...). These use a neutral
native execution request (`Executable`, `Arguments`, `WorkingDirectory`,
`Environment`, `ExpectedCapabilities`, `ExecutionIntent`) flowing
`workflow/activity -> Hufu admission -> Gagamba -> native process tree`.
Luban is not required.

### Hufu / Gagamba responsibilities stay explicit

- Hufu answers "May this activity perform this operation?" (read paths, send
  HTTPS to a host, execute `dotnet`, write a scope, create child processes).
- Gagamba answers "How is approved native execution contained and cleaned
  up?" (process-tree ownership, termination, sandbox boundaries, lifecycle,
  resource limits, platform containment, crash/descendant cleanup).

Neither depends on Luban.

### Two execution trust tiers

- Tier 1 (semantic: `filesystem.read/write`, `http.request`, `git.diff`,
  `apply.patch`): precise semantics/authority, narrow implementation,
  strongly auditable, VFS/WhatIf-friendly, preferred where practical.
- Tier 2 (native: `dotnet test`, `npm install`, `git rebase`, ...): opaque or
  partially opaque, broader effect surface, Gagamba containment, stronger
  observation, explicit process authority plus resource authority.

This replaces the assumption that Luban defines the trust boundary.

### Capability, process authority, and intent

- Hufu capabilities describe resources/operations (e.g.
  `filesystem.read:/repo/src/**`, `network.http:api.nuget.org`,
  `process.execute:dotnet`), not command names (`allow Get-Content`).
- Process authority is explicit (executable, resolved path, arguments, working
  directory, environment, requested network/filesystem authority, sandbox
  profile, guarantees, parent activity, workflow identity). Approving
  `process.execute` never implies unrestricted machine access.
- Fuwen execution intent (profile, required/preferred guarantees, isolation,
  network/filesystem/process-tree expectations) stays provider-neutral and
  MUST NOT reference Luban, Gagamba, or Hufu implementation types.

### Adapters, HTTP, Git, diff/merge/patch, VFS, audit, debugger

- Semantic adapters (filesystem, HTTP, Git status/diff, patch, diff/merge)
  only where value justifies cost; native execution otherwise. This prevents
  Luban's scope problem reappearing elsewhere.
- HTTP remains a first-class Hufu authority domain serving Baize, semantic
  activities, and any future Luban `Invoke-WebRequest` through one model.
- Git stays independent of Luban; semantic `GitStatus/Diff/Stage/Commit` are
  services/adapters, with complex operations still allowed via sandboxed
  native Git.
- Diff/merge/patch become reusable capabilities (`IDiffService`,
  `IMergeService`, `IPatchService`, or `Diff/Merge/ApplyPatchRequest`).
- VFS/WhatIf integrates at semantic requests; native execution keeps the
  isolate-observe-derive-authorize-apply flow (never blind rerun).
- Audit records describe the semantic or native operation (activity,
  operation, target, authority, result, sandbox where applicable); a Luban
  source expression is optional provenance only.
- The Hufu authority debugger operates on neutral concepts only
  (`Why denied? What grant suffices? What resources reachable? What
  guarantees missing?`).

### Dependency direction

```text
Fuwen / application / activity
           |
           v
Execution abstractions
           |
           v
Hufu
           |
       +---+---+
       |       |
       v       v
Semantic   Gagamba
brokers    execution
```

Future Luban depends on execution abstractions; `Hufu -> Luban` and
`Execution abstractions -> Luban` MUST NOT exist. Possible layout:
`Penghou.Execution.Abstractions`, `Penghou.Hufu`,
`Penghou.Hufu.Execution`, `Penghou.Gagamba`,
`Penghou.Execution.{FileSystem,Http,Git}`, `Penghou.Luban` (deferred
frontend). Exact names may differ; direction is what matters.

### Migration and tests

1. Inventory every Luban type referenced by Hufu, adapters, workflow
   abstractions, sandbox integration, tests, and policy.
2. Extract neutral execution concepts into an abstraction package.
3. Replace command-oriented authority with semantic resource/effect authority.
4. Normalize native execution independently of Luban.
5. Confirm Gagamba accepts authorized requests without Luban.
6. Preserve useful filesystem/HTTP/Git/diff/merge/patch implementations.
7. Freeze Luban and prevent new core dependencies on it.

Tests must prove Luban is not required: Hufu builds/runs without
`Penghou.Luban`; semantic filesystem, Baize HTTP, and Gagamba native
execution authorize/execute/audit without Luban; denial, revocation, and
restart/recovery behave independently of any Luban runtime.

### What is not removed

Hufu, Gagamba, execution intent, activity-level authority, workflow least
privilege, semantic operations, native containment, HTTP/filesystem/Git
authority, diff/merge/patch, VFS/WhatIf, and auditing all remain. Only the
assumption that these need a dedicated shell language is removed.

### Re-entry criteria

Resume Luban only on concrete pressure, e.g. agents repeatedly needing to
compose semantic operations awkwardly via workflows; humans needing an
interactive authority-controlled shell; repeated native execution safely
replaceable by a constrained language; model-generated command sequences as a
major UX requirement; stable operation APIs allowing a thin frontend; or
evidence of material security/cost/observability/usability gain. If it
returns, it is a frontend (`Luban source -> parser -> semantic operation
requests -> existing abstractions`) introducing no second execution model and
requiring no Hufu syntax changes.

The core baseline becomes:

```text
             Fuwen / Zhinu / Baize / Activities
                         |
                         v
              Execution Abstractions
                         |
                         v
                       Hufu
                    /        \
                   /          \
                  v            v
        Semantic Operations   Gagamba
                               |
                               v
                        Native Execution
```

with Luban deliberately outside the critical path:

```text
Future Luban
     |
     v
Execution Abstractions
```
