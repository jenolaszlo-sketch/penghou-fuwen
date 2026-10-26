#Requires -Version 7.0
# ZG-RQ-02 RG-03: verify the staged native candidate feed used by the Fuwen
# release path. The root NuGet.config maps all seven native package IDs
# exclusively to this local feed; restore cannot fall back to nuget.org for
# those IDs (most-specific-pattern mapping). This verifier proves the staged
# feed is exactly the qualified native candidate:
#   - the pinned source commit is a 40-hex SHA and matches the checkout that
#     produced the feed (when -ResolvedSourceCommit is provided);
#   - the feed contains exactly the seven pinned native package IDs, at the
#     pinned version, each with its symbol archive;
#   - no additional or substituted Penghou packages appear;
#   - the native core package declares the accepted neutral-contract pin
#     'Penghou.Workflow.Abstractions' '[0.1.0-preview.2]'.
#
# Provenance note: staged packages are exact-source rebuilds; their archive
# hashes intentionally differ from the frozen candidate pin hashes. Identity,
# version, dependency and commit binding are the accepted provenance
# contract for a staged feed (release packet: "a source rebuild is a new
# per-run archive set"; pins and exact source are retained separately).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $FeedDirectory,
    [string] $PinPath = 'docs/qualification/activity-groups/zhinu-candidate.json',
    [Parameter(Mandatory = $true)][string] $RebuildManifestPath,
    [string] $Version,
    [string] $ResolvedSourceCommit,
    [string] $ExpectedNeutralDependency = '[0.1.0-preview.2]',
    [string] $NeutralDependencyId = 'Penghou.Workflow.Abstractions'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PinPath -PathType Leaf)) {
    throw "Native candidate pin not found: $PinPath (R15 REFUSE: feed prerequisite missing)."
}
$pin = Get-Content -LiteralPath $PinPath -Raw | ConvertFrom-Json
if (-not $pin.PSObject.Properties['SourceCommit'] -or
    ([string]$pin.SourceCommit) -notmatch '^[0-9a-f]{40}$') {
    throw 'Pin file is invalid: SourceCommit must be a 40-character commit SHA (R15 REFUSE).'
}
if (-not $Version) { $Version = [string]$pin.Version }
if ([string]$pin.Version -ne $Version) {
    throw "Pin version '$($pin.Version)' does not match expected native version '$Version' (R15 REFUSE)."
}
if ($ResolvedSourceCommit -and $ResolvedSourceCommit.ToLowerInvariant() -ne ([string]$pin.SourceCommit).ToLowerInvariant()) {
    throw "Resolved native source commit '$ResolvedSourceCommit' is not the pinned commit '$($pin.SourceCommit)' (R15 REFUSE)."
}
if (-not (Test-Path -LiteralPath $FeedDirectory -PathType Container)) {
    throw "Native feed directory is absent: $FeedDirectory (R15 REFUSE)."
}

$feed = (Resolve-Path -LiteralPath $FeedDirectory).Path
$nupkgs = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File)
$pinnedIds = @($pin.Packages | ForEach-Object { $_.Id })
if ($pinnedIds.Count -ne 7) {
    throw "Pin lists $($pinnedIds.Count) native packages; exactly 7 are required (R15 REFUSE)."
}

# The feed serves NuGet restore; restore consumes only nupkg files, and the
# pin's provenance set lists exactly the seven nupkg archives. Symbol
# archives are never restored from a local feed and are not required here.
if ($nupkgs.Count -ne 7) { throw "Feed contains $($nupkgs.Count) nupkg files; exactly 7 are required (R15 REFUSE)." }

foreach ($id in $pinnedIds) {
    if (-not ($nupkgs | Where-Object { $_.Name -eq "$id.$Version.nupkg" })) {
        throw "Native package missing from feed: $id.$Version.nupkg (R15 REFUSE)." }
}
$expected = $pinnedIds | ForEach-Object { "$_.$Version" }
$unexpected = @($nupkgs | Where-Object { $_.BaseName -notin $expected })
if ($unexpected.Count -gt 0) {
    throw "Unexpected native packages in feed: $($unexpected.Name -join ', ') (R09/R16 REFUSE)."
}

$rebuilt = Get-Content -LiteralPath $RebuildManifestPath -Raw | ConvertFrom-Json
if ($rebuilt.SourceCommit -cne $pin.SourceCommit -or $rebuilt.Version -cne $pin.Version -or
    $rebuilt.Provenance.Kind -cne 'source-rebuild' -or
    $rebuilt.Provenance.OriginalArchivePinSha256 -cne (Get-FileHash -LiteralPath $PinPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'Rebuild manifest does not bind the tracked native contract.'
}
if (@($rebuilt.Packages).Count -ne 7 -or @($rebuilt.Packages.Id | Sort-Object -Unique).Count -ne 7) { throw 'Invalid rebuild closure.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-PackageArchive {
    param([string]$Path, [string]$Id, [string]$Version, [string]$Commit)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $specs = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw "Expected one nuspec: $Path" }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$doc = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $doc.package.metadata
        if ($meta.id -cne $Id -or $meta.version -cne $Version -or $meta.repository.commit -cne $Commit) {
            throw "Archive identity/version/source mismatch: $Path"
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if (-not $seen.Add($entry.FullName) -or $entry.FullName -match '(^/|\\|(^|/)\.\.(/|$))') {
                throw "Unsafe or duplicate archive member: $Path"
            }
        }
    } finally { $zip.Dispose() }
}

foreach ($package in $rebuilt.Packages) {
    if ($package.Id -notin $pinnedIds -or $package.File -cne "$($package.Id).$Version.nupkg") { throw 'Rebuild package identity mismatch.' }
    $file = Join-Path $feed $package.File
    if ($package.Sha256 -cne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Rebuild hash mismatch: $file" }
    Assert-PackageArchive $file $package.Id $Version $pin.SourceCommit
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$coreArchive = [System.IO.Compression.ZipFile]::OpenRead(
    (Join-Path $feed "Penghou.Zhinu.$Version.nupkg"))
try {
    $nuspecEntry = $coreArchive.Entries |
        Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspec = [xml]$reader.ReadToEnd()
    $reader.Close()
}
finally { $coreArchive.Dispose() }
$actualVersions = @()
foreach ($group in $nuspec.package.metadata.dependencies.group) {
    foreach ($dependency in $group.dependency) {
        if ($dependency.id -eq $NeutralDependencyId) { $actualVersions += $dependency.version }
    }
}
$actualVersions = @($actualVersions | Select-Object -Unique)
if ($actualVersions.Count -ne 1 -or $actualVersions[0] -ne $ExpectedNeutralDependency) {
    throw "Native core package declares '$NeutralDependencyId' versions '$($actualVersions -join ', ')'; the accepted pin is '$ExpectedNeutralDependency' (R11 REFUSE)."
}

Write-Output ("NATIVE FEED VERIFIED: $($pinnedIds.Count) packages at $Version, " +
    "source $($pin.SourceCommit), $NeutralDependencyId $ExpectedNeutralDependency.")
