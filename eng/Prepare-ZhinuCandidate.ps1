[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArtifactsDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$pin = Get-Content -LiteralPath (Join-Path $repository 'docs/qualification/activity-groups/zhinu-candidate.json') -Raw | ConvertFrom-Json
$source = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
if (@($pin.Packages).Count -ne 7) { throw 'Expected the seven-package Zhinu closure.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $pin.Packages) {
    if ($package.File -ne "$($package.Id).$($pin.Version).nupkg") { throw 'Invalid candidate package filename.' }
    $path = Join-Path $source $package.File
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.Sha256) {
        throw "Candidate hash mismatch: $($package.File)"
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw 'Expected one package manifest.' }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $package.Id -or $metadata.version -ne $pin.Version -or $metadata.repository.commit -ne $pin.SourceCommit) {
            throw "Candidate identity/source mismatch: $($package.File)"
        }
    } finally { $archive.Dispose() }
}
# No partial adoption: validate the complete closure before writing any feed entry.
$destination = Join-Path $repository 'artifacts/zhinu-candidate'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($package in $pin.Packages) {
    Copy-Item -LiteralPath (Join-Path $source $package.File) -Destination (Join-Path $destination $package.File) -Force
}
Write-Output "Prepared exact unpublished Zhinu $($pin.Version) from $($pin.SourceCommit)."
