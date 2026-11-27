# Public Zhinu dependency baseline (canonical)

Canonical Fuwen build, test, pack and release validation consume the **signed
public** Zhinu `0.2.0-preview.10` packages from nuget.org:

- `Penghou.Zhinu`
  `8e735c4d783a8d57c8aa256a5652cb16d6e4063cc4611342281d1372456bac71`
- `Penghou.Zhinu.Sqlite`
  `a92f273306352a61ce2b743977d67c5f840fef48225c397271cb6e6fa30bbd70`

The complete seven-package identity/signature/hash contract is
[`zhinu-public-baseline.json`](zhinu-public-baseline.json).

## Canonical usage

```powershell
./eng/Verify-PublicZhinuBaseline.ps1 -OutputDirectory artifacts/zhinu-public `
  -ReceiptPath artifacts/zhinu-public-baseline.json
dotnet build Penghou.Fuwen.slnx --configuration Release
dotnet test Penghou.Fuwen.slnx --configuration Release --no-build
dotnet pack Penghou.Fuwen.slnx --configuration Release --no-build --output artifacts
```

`NuGet.config` resolves all Zhinu IDs from nuget.org. The consumer proof uses
`eng/NuGet.Consumer.config`, which maps only `Penghou.Fuwen*` to the local
candidate feed and all Zhinu IDs to nuget.org.

## Historical material

`zhinu-candidate.json` and the `Prepare-Zhinu*` / `Verify-NativeFeed` /
`Invoke-ReleaseRehearsal` scripts are retained as historical source-rebuild
evidence. They are **not** part of canonical CI or release validation and must
not be used to construct release artifacts. The canonical release path verifies
the public baseline, builds/tests, packs, and publishes the exact validated
artifacts.

The public-baseline rejection control
(`eng/Test-PublicZhinuBaselineRejection.ps1`) deliberately mutates a package hash
and confirms the verifier refuses it.
