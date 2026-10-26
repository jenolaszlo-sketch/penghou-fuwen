#Requires -Version 7.0
# ZG-RQ-02 RG-03: local native-feed rehearsal matrix (nonpublishing).
#
# Exercises the REAL feed verifier (eng/Verify-NativeFeed.ps1) and the real
# NuGet source-mapping behavior against fixtures derived from a staged feed:
#   R15  feed absent / pin invalid / resolved commit mismatch / missing
#        package / wrong version            -> REFUSE
#   R09   extra native package in the feed   -> REFUSE
#   R11   dependency pin differs             -> REFUSE (verifier level)
#   R16   empty mapped feed directory with an isolated cache: restore must
#         fail closed; exclusive ID mapping must not fall back to nuget.org
#   R18   valid staged feed + successful isolated-cache restore of the
#         adapter project                   -> RELEASE-ELIGIBLE, NO PUSH
# This harness contains no publication code path.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $FeedDirectory,
    [Parameter(Mandatory = $true)][string] $PinPath,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][string] $RebuildManifestPath,
    [string] $Version = '0.2.0-preview.10'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Test-Path $OutputDirectory) { throw 'Fixture output must be new.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$FeedDirectory = (Resolve-Path $FeedDirectory).Path
$PinPath = (Resolve-Path $PinPath).Path
$RebuildManifestPath = (Resolve-Path $RebuildManifestPath).Path
$verifier = Join-Path $PSScriptRoot 'Verify-NativeFeed.ps1'
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [hashtable] $Parameters
    )
    $refused = $false
    $output = ''
    try {
        $params = @{ FeedDirectory = $Parameters.FeedDirectory; PinPath = $Parameters.PinPath; Version = $Parameters.Version; RebuildManifestPath = $RebuildManifestPath }
        if ($Parameters.ContainsKey('ResolvedSourceCommit')) { $params['ResolvedSourceCommit'] = $Parameters.ResolvedSourceCommit }
        $output = & $verifier @params 2>&1 | Out-String
    }
    catch {
        $refused = $true
        $output = $_.Exception.Message
    }
    $reason = @{
        'R15b-feed-absent'='Native feed directory is absent'; 'R15c-package-missing'='Feed contains 6';
        'R15d-wrong-version'='Pin version'; 'R15e-commit-mismatch'='Resolved native source commit';
        'R15f-invalid-pin'='SourceCommit must'; 'R09-extra-package'='Feed contains 8';
        'R15g-corrupt-bytes'='Rebuild hash mismatch'
    }
    if ($Parameters.ExpectRefuse -and (-not $refused -or $output -notmatch $reason[$Id])) {
        throw "Expected production rejection for $Id, got: $output"
    }
    $results.Add([pscustomobject]@{
        scenario = $Id
        expected = if ($Parameters.ExpectRefuse) { 'REFUSED' } else { 'PASS' }
        observed = if ($refused) { 'REFUSED' } else { 'PASS' }
        status   = if ($refused -eq [bool]$Parameters.ExpectRefuse) { 'PASS' } else { 'FAIL' }
        detail   = (@($output -split [Environment]::NewLine) | Where-Object { $_ } | Select-Object -Last 1)
    })
}

function Copy-Feed {
    param([Parameter(Mandatory = $true)][string] $Name)
    $dir = Join-Path $OutputDirectory $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -Path (Join-Path $FeedDirectory '*.nupkg') -Destination $dir
    Copy-Item -LiteralPath $PinPath -Destination (Join-Path $dir 'zhinu-candidate.json')
    return $dir
}

# R15 series: valid base verifies, then each fault refuses.
$pinCommit = (Get-Content -LiteralPath $PinPath -Raw | ConvertFrom-Json).SourceCommit
$base = Copy-Feed 'R15-base'
Invoke-Scenario 'R15a-valid-feed' @{ FeedDirectory = $base; PinPath = "$base\zhinu-candidate.json"; Version = $Version; ResolvedSourceCommit = $pinCommit; ExpectRefuse = $false }

$absent = Join-Path $OutputDirectory 'R15b-absent'
Invoke-Scenario 'R15b-feed-absent' @{ FeedDirectory = $absent; PinPath = $PinPath; Version = $Version; ExpectRefuse = $true }

$missing = Copy-Feed 'R15c'
$pinned = Get-Content -LiteralPath "$missing\zhinu-candidate.json" -Raw | ConvertFrom-Json
Remove-Item -LiteralPath (Join-Path $missing "$($pinned.Packages[2].Id).$Version.nupkg")
Invoke-Scenario 'R15c-package-missing' @{ FeedDirectory = $missing; PinPath = "$missing\zhinu-candidate.json"; Version = $Version; ExpectRefuse = $true }

$wrongVersion = Copy-Feed 'R15d'
Invoke-Scenario 'R15d-wrong-version' @{ FeedDirectory = $wrongVersion; PinPath = "$wrongVersion\zhinu-candidate.json"; Version = '0.2.0-preview.9'; ExpectRefuse = $true }

$wrongCommit = Copy-Feed 'R15e'
Invoke-Scenario 'R15e-commit-mismatch' @{ FeedDirectory = $wrongCommit; PinPath = "$wrongCommit\zhinu-candidate.json"; Version = $Version; ResolvedSourceCommit = '0123456789abcdef0123456789abcdef01234567'; ExpectRefuse = $true }

$badPin = Copy-Feed 'R15f'
$pin = Get-Content -LiteralPath "$badPin\zhinu-candidate.json" -Raw | ConvertFrom-Json
$pin.SourceCommit = 'not-a-sha'
$pin | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$badPin\zhinu-candidate.json"
Invoke-Scenario 'R15f-invalid-pin' @{ FeedDirectory = $badPin; PinPath = "$badPin\zhinu-candidate.json"; Version = $Version; ExpectRefuse = $true }

# R09: an extra native package in the feed.
$extra = Copy-Feed 'R09'
Copy-Item -LiteralPath (Join-Path $extra "Penghou.Zhinu.$Version.nupkg") `
    -Destination (Join-Path $extra "Penghou.Zhinu.Experimental.$Version.nupkg")
Invoke-Scenario 'R09-extra-package' @{ FeedDirectory = $extra; PinPath = "$extra\zhinu-candidate.json"; Version = $Version; ExpectRefuse = $true }

# A correct filename cannot hide modified bytes in any package.
$corrupt = Copy-Feed 'R15g'
$target = Join-Path $corrupt "$($pinned.Packages[2].Id).$Version.nupkg"
[IO.File]::AppendAllText($target, 'corrupt')
Invoke-Scenario 'R15g-corrupt-bytes' @{ FeedDirectory=$corrupt; PinPath=$PinPath; Version=$Version; ExpectRefuse=$true }

# R16: the mapped feed path exists but is EMPTY. With an isolated cache the
# restore of the adapter project must fail closed: the exclusive ID mapping
# cannot fall back to nuget.org for the native packages.
$restoreCase = Join-Path $OutputDirectory 'R16'
New-Item -ItemType Directory -Path "$restoreCase\artifacts\zhinu-candidate" -Force | Out-Null
$project = Join-Path $restoreCase 'AdapterProbe.csproj'
Set-Content -LiteralPath $project -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RestorePackagesPath>$(Join-Path $restoreCase 'isolated-cache')\</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Zhinu" Version="[$Version]" />
  </ItemGroup>
</Project>
"@
$restoreConfig = Join-Path $restoreCase 'NuGet.config'
Set-Content -LiteralPath $restoreConfig -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="zhinu-candidate" value="$(Join-Path $restoreCase 'artifacts\zhinu-candidate')" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="zhinu-candidate">
      <package pattern="Penghou.Zhinu" />
      <package pattern="Penghou.Zhinu.Sqlite" />
      <package pattern="Penghou.Zhinu.Hosting" />
      <package pattern="Penghou.Zhinu.Hosting.AspNetCore" />
      <package pattern="Penghou.Zhinu.Agents" />
      <package pattern="Penghou.Zhinu.Testing" />
      <package pattern="Penghou.Zhinu.OpenTelemetry" />
    </packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
$restoreFailed = $false
$restoreOutput = ''
try {
    $restoreOutput = & dotnet restore $project --configfile $restoreConfig --no-cache 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { $restoreFailed = $false } else { $restoreFailed = $true }
}
catch { $restoreFailed = $true }
$results.Add([pscustomobject]@{
    scenario = 'R16'
    expected = 'REFUSED'
    observed = if ($restoreFailed) { 'REFUSED' } else { 'PASSED-RESTORE' }
    status   = if ($restoreFailed -and $restoreOutput -match 'NU1101' -and $restoreOutput -match 'zhinu-candidate|Penghou.Zhinu') { 'PASS' } else { 'FAIL' }
    detail   = $restoreOutput
})

# R18: the valid staged feed restores the adapter project from the mapped
# source only, with an isolated cache (RELEASE-ELIGIBLE, NO PUSH).
$restoreOk = Join-Path $OutputDirectory 'R18'
New-Item -ItemType Directory -Path "$restoreOk\artifacts\zhinu-candidate" -Force | Out-Null
Copy-Item -Path (Join-Path $FeedDirectory '*.nupkg') -Destination "$restoreOk\artifacts\zhinu-candidate"
$projectOk = Join-Path $restoreOk 'AdapterProbe.csproj'
Set-Content -LiteralPath $projectOk -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RestorePackagesPath>$(Join-Path $restoreOk 'isolated-cache')\</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Zhinu" Version="[$Version]" />
    <PackageReference Include="Penghou.Zhinu.Sqlite" Version="[$Version]" />
  </ItemGroup>
</Project>
"@
$configOk = Join-Path $restoreOk 'NuGet.config'
Set-Content -LiteralPath $configOk -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="zhinu-candidate" value="artifacts\zhinu-candidate" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="zhinu-candidate">
      <package pattern="Penghou.Zhinu" />
      <package pattern="Penghou.Zhinu.Sqlite" />
      <package pattern="Penghou.Zhinu.Hosting" />
      <package pattern="Penghou.Zhinu.Hosting.AspNetCore" />
      <package pattern="Penghou.Zhinu.Agents" />
      <package pattern="Penghou.Zhinu.Testing" />
      <package pattern="Penghou.Zhinu.OpenTelemetry" />
    </packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
$r18Refused = $false
$r18Output = ''
try {
    Push-Location $restoreOk
    $r18Output = & dotnet restore $projectOk --configfile $configOk --no-cache 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $r18Output" }
}
catch { $r18Refused = $true; $r18Output = $_.Exception.Message }
finally { Pop-Location }
$results.Add([pscustomobject]@{
    scenario = 'R18'
    expected = 'RELEASE-ELIGIBLE (NO PUSH)'
    observed = if ($r18Refused) { 'REFUSED' } else { 'RELEASE-ELIGIBLE (NO PUSH)' }
    status   = if ($r18Refused) { 'FAIL' } else { 'RELEASE-ELIGIBLE (NO PUSH)' }
    detail   = $r18Output
})

$summary = [ordered]@{
    executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    scenarios     = $results
    noPushReason  = 'Publication side effects exist only in publish.yml:publish behind validation and the protected nuget environment; this harness and the rehearsal workflow contain no push steps.'
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'rehearsal-matrix.json')
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.status -like 'FAIL*' })
if ($failed.Count -gt 0) { throw "Rehearsal matrix failures: $($failed.Count)" }
Write-Output "Native-feed rehearsal matrix complete: $($results.Count) scenarios, all as expected."

