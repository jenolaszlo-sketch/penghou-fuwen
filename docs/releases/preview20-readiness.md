# Fuwen preview.20 release preparation

Date: 2026-10-20. Status: **qualified preview candidate; publication gates open**.
This packet prepares a publication decision. It does not authorize a tag,
workflow dispatch, NuGet push, merge or downstream dependency update.

## Frozen candidate and release notes

Fuwen `0.1.0-preview.20` runtime/package source is
`eca1213796d9fa4ad08423e2691cb409c892f63b`. Its four original packages and four
symbol archives remain unchanged. Native dependency is exact Zhinu
`0.2.0-preview.10`, source `bf33c05cf2390af6924bbe3761e09f9e377c7a97`, schema 10,
with seven original packages and seven symbol archives. The accompanying
[manifest](preview20-readiness.json) records all 22 archive hashes and their
managed payloads, source commits, dependency metadata and public-feed observation.
These are candidate upload bytes, not a claim about future indexed archive bytes.

Release notes: preview.20 fixes cross-run dynamic provider operation-key
collisions, duplicate ungrouped repeat names sharing native state, ungrouped
fan-out mapper round trips and missing provider-path byte-budget admission.
New dynamic definitions use `fuwen-ir/v6-run-scoped-dynamics`; existing v1-v5
definitions retain their original invocation identities. New independent runs
sharing an idempotent provider should compile/admit v6. Never rekey an in-flight
effect. Duplicate ungrouped repeat names and oversized identities now fail
registration before writes. See [migration notes](../consumer-migration.md),
[v6 contract](../run-scoped-dynamic-invocations.md) and
[release changelog](../../CHANGELOG.md#010-preview20).

The [sealed review](../qualification/activity-groups/integration-second-pass.md)
qualifies 1,770 regression executions and 32 package-only consumer executions
per OS on Windows/Linux .NET 8/10. Both consumers use the same Ubuntu archives.
Native Q33's 1,963 executions per OS remain a separate earlier qualification.
Original candidate archives, CI rebuilds and eventual public packages must keep
separate provenance. [REL-SQLITE-01](../reliability/windows-sqlite-process-loss.md)
tracks the unexplained Windows I/O incident; the unchanged-source passing rerun
does not close it.

## Publication gates found in this preparation

| Gate | Current evidence | Required next action |
| --- | --- | --- |
| Candidate runtime and package qualification | Passed; sealed review and original archive hashes retained | Preserve this checkpoint |
| Public native dependency | All seven native preview.10 package URLs return HTTP 404 at the recorded observation | Publish the separately approved native set first and wait for indexing |
| Public Fuwen preview.20 | All four package URLs return HTTP 404; proposed version tags are absent | Approve publication only after native/public-feed gates are satisfied |
| Fuwen tag workflow restore | Root `NuGet.config` maps exact native IDs to `artifacts/zhinu-candidate`; the publish workflow never prepares that directory | Fix and validate the release feed path before creating a tag |
| Release archive identity | Both tag workflows pack again; native workflow also derives its package version from the selected tag | Choose and qualify the actual upload set; do not claim it has the frozen archive hashes |
| Indexed public archives | Not yet available | Download, verify and qualify the indexed set with fresh caches |
| Downstream adoption | Candidate consumer tests passed; no preview.20 public-package adoption is claimed | Fuwen indexing, then Guihua, then application qualification |

Package source mapping uses the most specific matching pattern. Consequently,
the native exact-ID mappings cannot fall back to the public `*` mapping merely
because native packages become public. See
[NuGet package source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping).
The consumer projects likewise have local Fuwen/native mappings; their normal
restore commands are candidate-feed proofs, not public-only adoption proofs.

NuGet.org automatically repository-signs uploaded packages. That changes the
submitted archive; full-file candidate hashes must remain distinct from public
download hashes. Compare managed payloads and nuspec identity/source/dependencies,
verify the public package signature, and record the indexed archives separately.
See [NuGet signed packages](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference)
and [repository signing](https://devblogs.microsoft.com/dotnet/introducing-repository-signatures/).
Do not overwrite the original native pin with new public hashes: preserve the
candidate record and add an explicit public release pin plus qualification.

## Ordered release procedure

1. Keep the original candidate artifacts and sealed evidence. Resolve the
   release-workflow feed gate and decide whether publication promotes these
   upload bytes or produces a newly qualified archive set. A tag of a later
   documentation/harness commit creates different provenance when repacked.
2. Separately approve the exact native preview.10 release ref and upload set.
   Verify all seven packages/symbols and the supported schema 5-9 to 10 boundary;
   schema 4 remains unsupported. The current native tag workflow packs and
   publishes; its execution alone is not the Q33 qualification gate.
3. After native indexing, download all seven public packages into a fresh D:
   cache/feed. Record signature checks, archive hashes, managed payload hashes,
   source and dependency closure. Qualify those bytes before selecting them as
   Fuwen's public native dependency set.
4. Validate the Fuwen release ref/version and actual release archives, including
   package-only consumers on both frameworks/OSes. Recheck release notes, the
   versioned identity contract and migration boundaries. Get the separate Fuwen
   publication decision only after that result is reviewable.
5. After Fuwen indexing, run both package consumers using an explicit
   public-only NuGet configuration and a new cache on D:. Clear inherited local
   source mappings. Record the actual resolved archive graph; local-feed passes
   cannot substitute for this check. Verify symbols/indexing as a separate gate.
6. Update and qualify Guihua against the indexed versions, then applications.
   Preserve focused restart, uncertain-effect identity and provider admission
   behavior in those adoption probes. Do not broaden the DSL during this work.

The neutral `Penghou.Workflow.Abstractions` preview.2 package already returns
HTTP 200. It precedes the chain where required. The release order is therefore
**native Zhinu → Fuwen → Guihua → applications**, with public indexing and
qualification at each dependency boundary.

## Preview versus formal RC

Preview.20 is a qualified candidate for the bounded integration profile. It is
not a declaration that Fuwen has passed the repository's formal
[RC graduation policy](../graduation-requirements.md). That policy still requires
API freeze, live-provider conformance, deferred-item rulings, recorded released-
package pilot evidence, human security review and a scoped release definition.
The integration evidence does not establish those broader gates. This packet
does not change them or move API baselines to Shipped.

The older Guyabano migration and non-code vertical have separate historical
records; they do not prove preview.20 adoption. The local package-consumer and
binary probes establish the stated candidate boundaries only. Keep the generic
[release checklist](../release-checklist.md) as the governing checklist and record
fresh public-package adoption before claiming that gate complete.
