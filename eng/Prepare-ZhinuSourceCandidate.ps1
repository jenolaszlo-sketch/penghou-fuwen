[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [Parameter(Mandatory = $false)][string] $ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$defaultPinPath = Join-Path $repository 'docs/qualification/activity-groups/zhinu-candidate.json'
$pin = Get-Content -LiteralPath $defaultPinPath -Raw | ConvertFrom-Json
$defaultPinSha256 = (Get-FileHash -LiteralPath $defaultPinPath -Algorithm SHA256).Hash.ToLowerInvariant()
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$sourceGitRoot = (& git -C $source rev-parse --show-toplevel 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "SourceDirectory is not inside a Git worktree: $SourceDirectory`n$sourceGitRoot" }
$sourceGitRoot = [System.IO.Path]::GetFullPath($sourceGitRoot).TrimEnd([char[]]@('/', '\'))
$source = [System.IO.Path]::GetFullPath($source).TrimEnd([char[]]@('/', '\'))
$pathComparison = if ([OperatingSystem]::IsWindows()) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not [string]::Equals($sourceGitRoot, $source, $pathComparison)) {
    throw 'SourceDirectory must name the root of the Zhinu Git checkout.'
}

$sourceCommit = (& git -C $source rev-parse --verify HEAD 2>&1 | Out-String).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0) { throw "Could not read the Zhinu source HEAD: $sourceCommit" }
if ($sourceCommit -ne $pin.SourceCommit.ToLowerInvariant()) {
    throw "Zhinu source HEAD '$sourceCommit' does not match tracked pin commit '$($pin.SourceCommit)'."
}
$dirtyStatus = @(& git -C $source status --porcelain=v1 --untracked-files=all 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Could not verify Zhinu source worktree status: $($dirtyStatus -join [Environment]::NewLine)" }
if ($dirtyStatus.Count -gt 0) {
    throw "Zhinu source worktree must be clean (tracked or untracked changes found):`n$($dirtyStatus -join [Environment]::NewLine)"
}

$propsPath = Join-Path $source 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { throw 'Zhinu source is missing Directory.Build.props.' }
[xml] $props = Get-Content -LiteralPath $propsPath -Raw
$versions = @($props.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='Version']") |
    ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ -ne '' } | Select-Object -Unique)
if ($versions.Count -ne 1 -or $versions[0] -ne $pin.Version) {
    throw "Zhinu Directory.Build.props version must match tracked pin version '$($pin.Version)'."
}

$solution = Join-Path $source 'Penghou.Zhinu.slnx'
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) { throw 'Zhinu source is missing Penghou.Zhinu.slnx.' }
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $repository 'artifacts/zhinu-source-candidate' }
$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$runId = [Guid]::NewGuid().ToString('N')
$packagesDirectory = $artifactRoot
New-Item -ItemType Directory -Path $packagesDirectory -Force | Out-Null

$expectedPackages = @($pin.Packages)
if ($expectedPackages.Count -ne 7) { throw 'Tracked Zhinu package pin must contain exactly seven packages.' }
# Build only the shipped closure. Packing the full solution restores unrelated
# benchmarks, samples and test dependencies on every clean CI worker.
foreach ($expected in $expectedPackages) {
    if ($expected.Id -notmatch '^Penghou\.Zhinu(?:\.[A-Za-z]+)*$') { throw 'Invalid pinned native package ID.' }
    $project = Join-Path $source "src/$($expected.Id)/$($expected.Id).csproj"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing native package project: $project" }
    $packArguments = @(
        'pack', $project,
        '--configuration', 'Release',
        '--output', $packagesDirectory,
        "-p:Version=$($pin.Version)",
        "-p:PackageVersion=$($pin.Version)",
        "-p:RepositoryCommit=$sourceCommit"
    )
    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) { throw "Zhinu source pack failed for $($expected.Id) with exit code $LASTEXITCODE." }
}

$actualNupkgs = @(Get-ChildItem -LiteralPath $packagesDirectory -File -Filter '*.nupkg')
if ($actualNupkgs.Count -ne 7) {
    throw "Source pack produced $($actualNupkgs.Count) .nupkg files; expected exactly seven."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$rebuiltPackages = [System.Collections.Generic.List[object]]::new()
foreach ($expected in $expectedPackages) {
    $file = "$($expected.Id).$($pin.Version).nupkg"
    $path = Join-Path $packagesDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Source pack omitted expected package '$file'." }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entries.Count -ne 1) { throw "Expected exactly one nuspec in '$file'." }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $expected.Id -or $metadata.version -ne $pin.Version -or
            $metadata.repository.commit -ne $sourceCommit) {
            throw "Source package identity/version/repository commit mismatch: $file"
        }
    } finally { $archive.Dispose() }
    $rebuiltPackages.Add([ordered]@{
        Id = $expected.Id
        File = $file
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}
$actualIds = @($rebuiltPackages | ForEach-Object Id | Sort-Object -Unique)
$expectedIds = @($expectedPackages | ForEach-Object Id | Sort-Object -Unique)
if (($actualIds -join "`n") -ne ($expectedIds -join "`n")) { throw 'Source pack package IDs do not match the tracked seven-package closure.' }

$manifestPath = Join-Path $packagesDirectory "zhinu-source-candidate-$runId.json"
$manifest = [ordered]@{
    Version = $pin.Version
    SourceCommit = $sourceCommit
    Publication = 'Unpublished source-rebuilt local candidate'
    Provenance = [ordered]@{
        Kind = 'source-rebuild'
        OriginalArchivePinSha256 = $defaultPinSha256
        RunId = $runId
    }
    Packages = @($rebuiltPackages)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

$prepareScript = Join-Path $PSScriptRoot 'Prepare-ZhinuCandidate.ps1'
& $prepareScript -ArtifactsDirectory $packagesDirectory -PinPath $manifestPath
if ($LASTEXITCODE -ne 0) { throw "Candidate adoption helper failed with exit code $LASTEXITCODE." }
Write-Output "Prepared source-rebuilt Zhinu $($pin.Version) from $sourceCommit."
Write-Output "Run packages: $packagesDirectory"
Write-Output "Run manifest: $manifestPath"
