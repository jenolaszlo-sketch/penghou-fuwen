[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDirectory,
    [Parameter(Mandatory = $false)][string] $PinPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$defaultPinPath = Join-Path $repository 'docs/qualification/activity-groups/zhinu-candidate.json'
$defaultPinSha256 = (Get-FileHash -LiteralPath $defaultPinPath -Algorithm SHA256).Hash.ToLowerInvariant()
$selectedPinPath = if ($PSBoundParameters.ContainsKey('PinPath')) {
    (Resolve-Path -LiteralPath $PinPath).Path
} else {
    $defaultPinPath
}
$defaultPin = Get-Content -LiteralPath $defaultPinPath -Raw | ConvertFrom-Json
$pin = Get-Content -LiteralPath $selectedPinPath -Raw | ConvertFrom-Json
$source = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path

function Assert-PinMatchesDefaultContract {
    param(
        [Parameter(Mandatory)] $CandidatePin,
        [Parameter(Mandatory)] $DefaultPin,
        [Parameter(Mandatory)][bool] $ExplicitPin,
        [Parameter(Mandatory)][string] $DefaultPinSha256
    )

    if ($CandidatePin.Version -ne $DefaultPin.Version -or $CandidatePin.SourceCommit -ne $DefaultPin.SourceCommit) {
        throw 'Candidate pin Version and SourceCommit must match the tracked Zhinu candidate contract.'
    }
    $expectedPackages = @($DefaultPin.Packages)
    $actualPackages = @($CandidatePin.Packages)
    if ($expectedPackages.Count -ne 7 -or $actualPackages.Count -ne 7) {
        throw 'Expected exactly the seven-package Zhinu closure.'
    }
    $expectedById = @{}
    foreach ($expected in $expectedPackages) {
        if ([string]::IsNullOrWhiteSpace($expected.Id) -or $expected.File -ne "$($expected.Id).$($DefaultPin.Version).nupkg" -or
            $expected.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $expectedById.ContainsKey($expected.Id)) {
            throw 'The tracked Zhinu package contract is invalid.'
        }
        $expectedById[$expected.Id] = $expected
    }

    $seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $hasDifferentDigest = $false
    foreach ($package in $actualPackages) {
        if ([string]::IsNullOrWhiteSpace($package.Id) -or -not $seenIds.Add($package.Id) -or
            -not $expectedById.ContainsKey($package.Id)) {
            throw 'Candidate pin package IDs must exactly match the tracked seven-package closure.'
        }
        $expected = $expectedById[$package.Id]
        if ($package.File -ne $expected.File -or $package.File -ne "$($package.Id).$($CandidatePin.Version).nupkg" -or
            $package.Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Candidate package filename or SHA-256 is invalid: $($package.Id)"
        }
        if ($package.Sha256.ToLowerInvariant() -ne $expected.Sha256.ToLowerInvariant()) {
            $hasDifferentDigest = $true
        }
    }
    if ($seenIds.Count -ne $expectedById.Count) {
        throw 'Candidate pin is missing one or more packages from the tracked closure.'
    }
    if ($hasDifferentDigest -and -not $ExplicitPin) {
        throw 'Package hashes differ from the tracked archive pin; select a separate source-rebuild manifest with -PinPath.'
    }
    if ($hasDifferentDigest) {
        $provenance = $CandidatePin.PSObject.Properties['Provenance']
        if ($null -eq $provenance -or $provenance.Value.Kind -ne 'source-rebuild' -or
            $provenance.Value.OriginalArchivePinSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $provenance.Value.OriginalArchivePinSha256.ToLowerInvariant() -ne $DefaultPinSha256) {
            throw 'Changed package hashes require source-rebuild provenance in the explicitly selected manifest.'
        }
    }
}

Assert-PinMatchesDefaultContract -CandidatePin $pin -DefaultPin $defaultPin `
    -ExplicitPin ($PSBoundParameters.ContainsKey('PinPath')) -DefaultPinSha256 $defaultPinSha256

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $pin.Packages) {
    $path = Join-Path $source $package.File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Candidate package is missing: $($package.File)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.Sha256.ToLowerInvariant()) {
        throw "Candidate hash mismatch: $($package.File)"
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw 'Expected one package manifest.' }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $package.Id -or $metadata.version -ne $pin.Version -or
            $metadata.repository.commit -ne $pin.SourceCommit) {
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
Write-Output "Prepared exact unpublished Zhinu $($pin.Version) from $($pin.SourceCommit) using pin '$selectedPinPath'."
