[CmdletBinding()]
param(
    [string] $BaselinePath,
    [string] $OutputDirectory,
    [string] $ReceiptPath,
    [int] $DownloadAttempts = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $BaselinePath) {
    $BaselinePath = Join-Path $repository 'docs/qualification/activity-groups/zhinu-public-baseline.json'
}
$BaselinePath = (Resolve-Path -LiteralPath $BaselinePath).Path
$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
if ($baseline.Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid public Zhinu version.' }
if ($baseline.SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid public Zhinu source commit.' }
if ($baseline.Repository -cne 'jenolaszlo-sketch/penghou-zhinu') { throw 'Unexpected public Zhinu repository.' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) "zhinu-public-$([Guid]::NewGuid().ToString('N'))"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $ReceiptPath) {
    $ReceiptPath = Join-Path $repository 'artifacts/zhinu-public-baseline.json'
}
$receiptPath = [IO.Path]::GetFullPath($ReceiptPath)
New-Item -ItemType Directory -Path (Split-Path $receiptPath -Parent) -Force | Out-Null

$packages = @($baseline.Packages)
if ($packages.Count -ne 7) { throw "Expected seven public Zhinu packages; found $($packages.Count)." }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($package in $packages) {
    if (-not $seen.Add($package.Id)) { throw "Duplicate public package: $($package.Id)" }
    if ($package.File -cne "$($package.Id).$($baseline.Version).nupkg") { throw "Noncanonical package filename: $($package.File)" }
    if ($package.Sha256 -notmatch '^[0-9a-f]{64}$' -or $package.SnupkgSha256 -notmatch '^[0-9a-f]{64}$') { throw "Invalid hash contract: $($package.Id)" }
}
if ($seen.Count -ne 7) { throw 'Public package closure is incomplete.' }

function Get-Package {
    param([string] $Uri, [string] $Path)
    for ($attempt = 1; $attempt -le $DownloadAttempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing
            if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) { throw 'Empty download.' }
            return
        }
        catch {
            if ($attempt -eq $DownloadAttempts) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

function Get-Nuspec {
    param([string] $Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $Path" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml] $document = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return $document
    }
    finally { $archive.Dispose() }
}

$source = $baseline.PackageSource
$receiptPackages = @()
foreach ($package in $packages) {
    $low = $package.Id.ToLowerInvariant()
    $nupkg = Join-Path $output $package.File
    $nupkgUrl = "https://api.nuget.org/v3-flatcontainer/$low/$($baseline.Version)/$low.$($baseline.Version).nupkg"
    Get-Package -Uri $nupkgUrl -Path $nupkg
    $nupkgHash = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($nupkgHash -ne $package.Sha256) { throw "SHA-256 mismatch for public package $($package.Id)." }

    $snupkg = Join-Path $output "$($package.Id).$($baseline.Version).snupkg"
    $snupkgUrl = "https://www.nuget.org/api/v2/symbolpackage/$($package.Id)/$($baseline.Version)"
    Get-Package -Uri $snupkgUrl -Path $snupkg
    $snupkgHash = (Get-FileHash -LiteralPath $snupkg -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($snupkgHash -ne $package.SnupkgSha256) { throw "Symbol SHA-256 mismatch for public package $($package.Id)." }

    $document = Get-Nuspec $nupkg
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw "Missing nuspec metadata: $($package.Id)" }
    if ($metadata.SelectSingleNode("*[local-name()='id']").InnerText -cne $package.Id -or
        $metadata.SelectSingleNode("*[local-name()='version']").InnerText -cne $baseline.Version -or
        $metadata.SelectSingleNode("*[local-name()='repository']").GetAttribute('commit') -cne $baseline.SourceCommit) {
        throw "Public package identity/source mismatch: $($package.Id)"
    }

    & dotnet nuget verify $nupkg --all | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Public package signature verification failed: $($package.Id)" }

    $receiptPackages += [ordered]@{
        Id = $package.Id
        Version = $baseline.Version
        Nupkg = $package.File
        NupkgSha256 = $nupkgHash
        Snupkg = [IO.Path]::GetFileName($snupkg)
        SnupkgSha256 = $snupkgHash
        Source = $source
    }
}

$receipt = [ordered]@{
    SchemaVersion = 1
    Status = 'passed'
    Repository = $baseline.Repository
    SourceCommit = $baseline.SourceCommit
    Version = $baseline.Version
    BaselineSha256 = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash.ToLowerInvariant()
    PackageSource = $source
    Packages = $receiptPackages
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output "Verified seven signed public Zhinu packages for $($baseline.Version) from $source; receipt: $receiptPath"
