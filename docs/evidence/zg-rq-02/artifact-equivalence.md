# Artifact identity and equivalence

`local/native-equivalence.json` inventories all 14 recovered and newly rebuilt
native package/symbol archives: package IDs/versions, dependencies by TFM, nuspec
metadata, repository commit, member SHA-256s, RID assets and archive hashes.
Both sets are from pinned native source bf33c05, version preview.10.

All decompressed members are byte-identical in this local rebuild, including
DLLs and PDBs. Archive bytes are NOT identical: ZIP member timestamps differ
(for example core entries 08:23:52 versus 09:48:38 UTC on 2026-10-26). This is
exact payload equivalence for those two local runs, not deterministic ZIP output.
There are no in-package RID binaries in this Zhinu closure; transitive native
SQLite assets are resolved from their public dependencies and their runtime DLL
hash/version under stress is recorded separately. Do not claim RID equivalence
to an unbuilt platform or byte identity to a public release.

The native staged feed is a per-run source rebuild, not the frozen original
Windows archive set and not a public-feed substitute. Each transfer must match
its own rebuild manifest and the original contract. Hosted platform tests and
clean-cache restores qualify the staged bytes used in that hosted run.

The intended Fuwen publisher consumes the exact validated Ubuntu artifact;
same-run download and revalidation establish the handoff identity without a
second build. The hosted handoff receipt, artifact IDs and final package member
inventory accompany the completed run. There is no evidence of published NuGet
byte equality because publication is prohibited.
