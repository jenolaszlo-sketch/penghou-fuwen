[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.+-]*$')][string] $PackageVersion,
    [Parameter(Mandatory)][string] $ZhinuPinPath,
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [Parameter(Mandatory)][string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$pin = Get-Content -LiteralPath $ZhinuPinPath -Raw | ConvertFrom-Json
$originalPin = Get-Content -LiteralPath (Join-Path $repository 'docs/qualification/activity-groups/zhinu-candidate.json') -Raw | ConvertFrom-Json
if ($pin.Version -ne $originalPin.Version -or $pin.SourceCommit -ne $originalPin.SourceCommit) {
    throw 'Native candidate source/version does not match the tracked contract.'
}
$sourceCommit = (& git -C $repository rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the Fuwen source commit.' }
$fuwenIds = @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Baize', 'Penghou.Fuwen.Zhinu')
$nativeById = @{}
foreach ($package in $pin.Packages) {
    if ($nativeById.ContainsKey($package.Id)) { throw 'Duplicate native package in manifest.' }
    $nativeById[$package.Id] = $package
}
if ($nativeById.Count -ne 7) { throw 'Expected the seven-package native closure.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives = @{}
foreach ($id in @($fuwenIds) + @($nativeById.Keys)) {
    $isFuwen = $id -in $fuwenIds
    $version = if ($isFuwen) { $PackageVersion } else { $pin.Version }
    $expectedCommit = if ($isFuwen) { $sourceCommit } else { $pin.SourceCommit }
    $file = "$id.$version.nupkg"
    $archivePath = Join-Path $artifacts $file
    $digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $isFuwen -and ($nativeById[$id].File -ne $file -or $nativeById[$id].Sha256 -ne $digest)) {
        throw "Native archive differs from the selected manifest: $file"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $file" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec.package.metadata.id -ne $id -or $nuspec.package.metadata.version -ne $version -or
            $nuspec.package.metadata.repository.commit -ne $expectedCommit) {
            throw "Package identity or source mismatch: $file"
        }
    } finally { $zip.Dispose() }
    $archives[$id] = [ordered]@{ Id = $id; Version = $version; File = $file; Sha256 = $digest; SourceCommit = $expectedCommit }
}

$consumers = @()
foreach ($name in @('Penghou.Fuwen.MarangConsumer.Tests', 'Penghou.Fuwen.ReviewConsumer.Tests')) {
    $assetsPath = Join-Path $repository "tests/$name/obj/project.assets.json"
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $resolved = @()
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'project') { throw "Consumer has a source project reference: $($library.Name)" }
        $identity = $library.Name.Split('/')
        $id = $identity[0]
        if ($id -notlike 'Penghou.Fuwen*' -and $id -notlike 'Penghou.Zhinu*') { continue }
        if (-not $archives.ContainsKey($id) -or $identity.Count -ne 2 -or $identity[1] -ne $archives[$id].Version) {
            throw "Unexpected resolved candidate in ${name}: $($library.Name)"
        }
        $cacheArchive = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder ($library.Value.path + '/' + $archives[$id].File.ToLowerInvariant())
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $cacheArchive = $candidate; break }
        }
        if ($null -eq $cacheArchive -or
            (Get-FileHash -LiteralPath $cacheArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archives[$id].Sha256) {
            throw "Cached archive does not match the supplied candidate: $($library.Name)"
        }
        $resolved += $archives[$id]
    }
    $required = @('Penghou.Fuwen', 'Penghou.Fuwen.Compiler', 'Penghou.Fuwen.Zhinu', 'Penghou.Zhinu', 'Penghou.Zhinu.Sqlite')
    if ($name -eq 'Penghou.Fuwen.ReviewConsumer.Tests') { $required += 'Penghou.Fuwen.Baize' }
    foreach ($id in $required) {
        if ($id -notin $resolved.Id) { throw "Consumer $name did not resolve required candidate $id" }
    }
    $consumers += [ordered]@{
        Project = $name
        ProjectReferences = $false
        AssetsSha256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ResolvedCandidates = @($resolved | Sort-Object Id)
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
    SchemaVersion = 1; Status = 'passed'; OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    SourceCommit = $sourceCommit; PackageVersion = $PackageVersion
    ZhinuSourceCommit = $pin.SourceCommit; ZhinuPackageVersion = $pin.Version
    NativeManifestSha256 = (Get-FileHash -LiteralPath $ZhinuPinPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Packages = @($archives.Values | Sort-Object Id); Consumers = $consumers; Tests = $receipts
}
$output = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output "Verified both isolated package consumers on .NET 8/10; receipt: $output"
