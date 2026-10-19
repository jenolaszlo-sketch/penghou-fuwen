# Activity groups: first Fuwen integration profile

This records the bounded ZG-6.1 implementation of FM-0/1/2, not completion of the
full FM/ZG milestone. The subsequent [repeat profile](activity-groups-repeat-profile.md)
adds single-level grouped repetition. Packages are unpublished candidates. Historical macro-free
plans keep their existing canonical bytes, fingerprints, step keys and adapter
behavior. Expanded scopes require `fuwen-ir/v4-group-expansion`.

## Authoring and scope

```fuwen
macro Review(value: string) {
  activity checked = activity "sample.echo@1#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" (value: value;) -> string;
  wait approved signal approve type string;
  yield approved;
}
workflow demo(input: string) -> string {
  group stage {
    use review = Review(value: input);
    yield review;
  }
  return stage;
}
```

Macro declarations precede the workflow. Invocation names are structural identities;
`demo/$group/stage/$group/review/checked` is an ordinary expanded activity path.
Names are local to a scope. Macros accept named runtime bindings for string,
integer, number, boolean and JSON parameters. No text is reparsed as executable
source. Macro bodies see declared parameters and their own locals; nested calls
receive only their explicit arguments. A single `yield` exports a direct node
output; other locals remain private. Logical groups can capture enclosing bindings.
Direct/indirect recursion, alias overwrites and unsupported parameter types fail
compilation. Source, parse work, nesting, generated nodes/groups and canonical
bytes retain hard ceilings and host compilation budgets.

Roles/profiles, compile-time topology parameters, imports, arbitrary AST
substitution and a dedicated expansion CLI are outside this first profile.
`FuwenSourceCompilationResult.Plan`, `SourceMap` and `GroupedDefinition` provide
programmatic expansion inspection. Macro names have no runtime dispatch meaning.

## Immutable admission and recovery

The compiler produces ordinary executable IR and an immutable
`WorkflowGroupManifest`. `GroupedWorkflowDefinitionDocument` canonically pins
both components. Changes to source references or presentation affect the bundle
pin without changing the ordinary executable fingerprint. Invocation arguments
remain ordinary IR bindings, not copied into group provenance.

Use `WorkflowAdmissionService.AdmitGroupedAsync` and
`FuwenZhinuWorkflowFactory.CreateGroupedAsync`. The opaque in-process admission
result binds the exact bundle to normal catalogue, effect and capability checks.
Plain `CreateAsync` rejects v4 plans missing their companion. The adapter stores,
reloads and checks both immutable components before returning a registration.
`DirectoryWorkflowDefinitionStore` provides content-addressed files with staged,
atomic same-directory publication and verified reads. Its directory is owned by
the host. Atomic publication covers process loss, not a new power-loss/fsync promise.

On restart, load the exact bundle by fingerprint, re-admit its pinned expanded IR
against the host catalogue/policy and re-register the same workflow generation.
This path needs no source compiler, macro definition text or macro library.
Zhinu's workflow fingerprint pins the bundle; provider envelopes continue to use
the ordinary execution fingerprint. A changed bundle cannot replace a running
generation merely because its ordinary execution plan is identical.

Definition/invocation source spans are advisory references into the authored
revision, retained in the Fuwen bundle. The source compiler creates them from
lexer coordinates. The standalone bundle verifier checks their shape, not access
to the original source revision. Zhinu queries expose generic macro identity and
invocation provenance; exact source locations are looked up in the pinned Fuwen
bundle. No raw source or unrestricted argument payload is copied into Zhinu.

## Runtime support and limits

| Shape | First grouped profile |
| --- | --- |
| Repeated static invocations, nested macros/logical groups | Supported |
| Activity, context, one-call inference | Native Work membership |
| Conditional decisions/merge, fallback, checkpoint, return | Native Control membership |
| Signal wait | Native Work membership using `WaitForGroupedSignalAsync` |
| Ordinary ungrouped workflows | Existing adapter behavior |
| Repeat, fan-out, coordinated inference in a grouped plan | Rejected before definition writes/provider calls |
| Macro children in independent child workflows/forks | No new DSL support |

Each step belongs directly to the innermost occurrence; ancestors are explicit
native occurrences. No synthetic persisted root is added. Membership does not
grant authority, order work or alter retries. Group state remains mechanical:
a provider's durable failure envelope may coexist with a Completed native step
and Failed run. The adapter does not manufacture a group failure by interpreting
domain outcomes. Current rows do not prove future expansion closure or an exact
total; existing Unknown/Partial coverage rules remain in effect.

The historical fan-out item boundary remains unchanged. Independently durable
macro children inside repeat/fan-out require versioned scoped lowering and
separate qualification. Rejection is an interim limitation: Q45 and full ZG-6,
FM-2 and ZG-7 remain open. Windows tests do not establish Linux CI qualification
or permission to publish packages.

## Candidate package reproduction

Restore uses the exact seven unpublished Zhinu packages recorded in
[`qualification/activity-groups/zhinu-candidate.json`](qualification/activity-groups/zhinu-candidate.json).
Run `eng/Prepare-ZhinuCandidate.ps1 -ArtifactsDirectory <qualified-package-directory>`
first. It checks all package hashes, versions and source commits before copying
the closure to the local feed. `NuGet.config` maps those seven package IDs to
that feed and uses a repository-local cache, preventing accidental adoption of
different bytes with the same version from a global cache. Candidate packages
and caches are ignored by Git. Other dependencies continue to use NuGet.org.
