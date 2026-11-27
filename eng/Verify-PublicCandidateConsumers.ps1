[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.+-]*$')][string] $PackageVersion,
    [string] $BaselinePath,
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [Parameter(Mandatory)][string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $BaselinePath) {
    $BaselinePath = Join-Path $repository 'docs/qualification/activity-groups/zhinu-public-baseline.json'
}
$BaselinePath = (Resolve-Path -LiteralPath $BaselinePath).Path
$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
$baselineById = @{}
foreach ($package in $baseline.Packages) {
    if ($baselineById.ContainsKey($package.Id)) { throw "Duplicate public baseline package: $($package.Id)" }
    $baselineById[$package.Id] = $package
}
if ($baselineById.Count -ne 7) { throw 'Expected seven public Zhinu packages.' }
$sourceCommit = (& git -C $repository rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the Fuwen source commit.' }
$fuwenIds = @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Baize', 'Penghou.Fuwen.Zhinu')
$fuwen = @{}
$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
foreach ($id in $fuwenIds) {
    $file = "$id.$PackageVersion.nupkg"
    $path = Join-Path $artifacts $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Fuwen candidate artifact is missing: $file" }
    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entries = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $file" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ($metadata.id -ne $id -or $metadata.version -ne $PackageVersion -or
            $metadata.repository.commit -ne $sourceCommit) {
            throw "Fuwen package identity/source mismatch: $file"
        }
    } finally { $zip.Dispose() }
    $fuwen[$id] = [ordered]@{ Id = $id; Version = $PackageVersion; File = $file; Sha256 = $digest; SourceCommit = $sourceCommit }
}

function Get-CachedArchive {
    param($Assets, $Library, [string] $FileName)
    foreach ($folder in $Assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder ($Library.path + '/' + $FileName.ToLowerInvariant())
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

$consumers = @()
foreach ($name in @('Penghou.Fuwen.MarangConsumer.Tests', 'Penghou.Fuwen.ReviewConsumer.Tests')) {
    $assetsPath = Join-Path $repository "tests/$name/obj/project.assets.json"
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $resolved = @()
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'project') { throw "Consumer has a source project reference: $($library.Name)" }
        $identity = $library.Name.Split('/')
        if ($identity.Count -ne 2) { continue }
        $id = $identity[0]
        $version = $identity[1]
        $expected = $null
        $expectedHash = $null
        $expectedFile = $null
        if ($id -like 'Penghou.Fuwen*') {
            if (-not $fuwen.ContainsKey($id) -or $version -ne $PackageVersion) { throw "Unexpected Fuwen candidate: $($library.Name)" }
            $expected = $fuwen[$id]
            $expectedHash = $expected.Sha256
            $expectedFile = $expected.File
        }
        elseif ($id -like 'Penghou.Zhinu*') {
            if (-not $baselineById.ContainsKey($id) -or $version -ne $baseline.Version) { throw "Unexpected public Zhinu dependency: $($library.Name)" }
            $expected = [ordered]@{ Id = $id; Version = $version; File = $baselineById[$id].File; Sha256 = $baselineById[$id].Sha256; SourceCommit = $baseline.SourceCommit }
            $expectedHash = $expected.Sha256
            $expectedFile = $expected.File
        }
        else { continue }

        $cacheArchive = Get-CachedArchive -Assets $assets -Library $library.Value -FileName $expectedFile
        if ($null -eq $cacheArchive) { throw "Resolved package archive was not found in the consumer cache: $($library.Name)" }
        if ((Get-FileHash -LiteralPath $cacheArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
            throw "Cached archive does not match the public/candidate contract: $($library.Name)"
        }
        if ($id -like 'Penghou.Zhinu*') {
            $metadataPath = Join-Path (Split-Path $cacheArchive -Parent) '.nupkg.metadata'
            if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw "Missing cache metadata: $($library.Name)" }
            $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
            if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') {
                throw "Public Zhinu dependency was not resolved from nuget.org: $($library.Name) source=$($metadata.source)"
            }
        }
        $resolved += $expected
    }
    $required = @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Zhinu', 'Penghou.Zhinu', 'Penghou.Zhinu.Sqlite')
    if ($name -eq 'Penghou.Fuwen.ReviewConsumer.Tests') { $required += 'Penghou.Fuwen.Baize' }
    foreach ($id in $required) {
        if ($id -notin $resolved.Id) { throw "Consumer $name did not resolve required package $id" }
    }
    $consumers += [ordered]@{
        Project = $name
        ProjectReferences = $false
        AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ResolvedPackages = @($resolved | Sort-Object Id)
    }
}

$receipts = @()
$identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -File -Filter '*.trx') {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
    $counter = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $definition = $trx.SelectSingleNode("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")
    if ($null -eq $counter -or $null -eq $definition -or [int]$counter.total -lt 1 -or
        [int]$counter.failed -ne 0 -or [int]$counter.notExecuted -ne 0 -or [int]$counter.passed -ne [int]$counter.total) {
        throw "Consumer test receipt did not pass completely: $($file.FullName)"
    }
    $storage = $definition.storage.Replace('\', '/').ToLowerInvariant()
    $framework = if ($storage.Contains('/net8.0/')) { 'net8.0' } elseif ($storage.Contains('/net10.0/')) { 'net10.0' } else { throw 'Missing receipt framework.' }
    $assembly = $storage.Split('/')[-1]
    if ($assembly -notin @('penghou.fuwen.marangconsumer.tests.dll', 'penghou.fuwen.reviewconsumer.tests.dll') -or
        -not $identities.Add("$assembly/$framework")) { throw 'Unexpected or duplicate consumer test receipt.' }
    $receipts += [ordered]@{
        File = $file.Name; Assembly = $assembly; Framework = $framework
        Passed = [int]$counter.passed; Failed = [int]$counter.failed; Skipped = [int]$counter.notExecuted
        Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
if ($receipts.Count -ne 4) { throw 'Expected both consumer projects on both target frameworks.' }

$result = [ordered]@{
    SchemaVersion = 1
    Status = 'passed'
    OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    SourceCommit = $sourceCommit
    PackageVersion = $PackageVersion
    ZhinuSourceCommit = $baseline.SourceCommit
    ZhinuPackageVersion = $baseline.Version
    PublicBaselineSha256 = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Packages = @($fuwen.Values | Sort-Object Id) + @($baseline.Packages | ForEach-Object { [ordered]@{ Id = $_.Id; Version = $baseline.Version; File = $_.File; Sha256 = $_.Sha256; SourceCommit = $baseline.SourceCommit } })
    Consumers = $consumers
    Tests = $receipts
}
$output = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output "Verified both isolated public-dependency consumers on .NET 8/10; receipt: $output"
