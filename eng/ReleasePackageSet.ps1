#Requires -Version 7.0
# This manifest is trusted only inside the same successful qualification run.
param([ValidateSet('Create','Verify')][string]$Mode,
      [string]$Directory, [string]$Version, [string]$Commit = $env:GITHUB_SHA,
      [string]$Repository = $env:GITHUB_REPOSITORY, [string]$Run = $env:GITHUB_RUN_ID)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ids = @('Penghou.Fuwen','Penghou.Fuwen.Compiler','Penghou.Fuwen.Zhinu','Penghou.Fuwen.Baize')
$manifestPath = Join-Path $Directory 'release-manifest.json'
if ($Commit -notmatch '^[a-f0-9]{40}$' -or $Run -notmatch '^\d+$' -or -not $Repository -or -not $Version) { throw 'Invalid binding.' }
$files = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object Extension -In '.nupkg','.snupkg')
if ($files.Count -ne 8) { throw 'Expected exactly four package/symbol pairs.' }
$entries = @()
foreach ($id in $ids) {
    foreach ($extension in @('nupkg','snupkg')) {
        $name = "$id.$Version.$extension"
        $path = Join-Path $Directory $name
        $zip = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $specs = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
            if ($specs.Count -ne 1) { throw 'Expected one nuspec.' }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml]$doc = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $meta = $doc.package.metadata
            if ($meta.id -cne $id -or $meta.version -cne $Version -or $meta.repository.commit -cne $Commit) { throw "Package identity/source mismatch: $name" }
        } finally { $zip.Dispose() }
        $entries += [ordered]@{ file=$name; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}
$expected = [ordered]@{ schema=1; repository=$Repository; commit=$Commit; run=$Run; version=$Version; files=$entries }
if ($Mode -eq 'Create') {
    if (Test-Path $manifestPath) { throw 'Manifest already exists.' }
    $expected | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
} else {
    $actual = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($field in @('schema','repository','commit','run','version')) {
        if ($actual[$field] -cne $expected[$field]) { throw "Manifest binding mismatch: $field" }
    }
    if (@($actual.files).Count -ne 8) { throw 'Manifest file count mismatch.' }
    foreach ($entry in $entries) {
        $match = @($actual.files | Where-Object { $_.file -ceq $entry.file -and $_.sha256 -ceq $entry.sha256 })
        if ($match.Count -ne 1) { throw "Manifest file/hash mismatch: $($entry.file)" }
    }
}
Write-Output "$Mode verified exact Fuwen package set: $Commit / $Run / $Version"
