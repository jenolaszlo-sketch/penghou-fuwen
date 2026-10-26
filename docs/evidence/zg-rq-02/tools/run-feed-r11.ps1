#Requires -Version 7.0
# Targeted R11 fault fixture for the native feed: modify the core package's
# neutral-contract dependency pin, update the rebuild-manifest hash, and prove
# the production verifier refuses at the dependency-pin check.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceFeed = 'C:\Users\Laszlos\source\repos\zg-rq-02-fuwen\artifacts\zhinu-candidate'
# Select the rebuild manifest whose seven package hashes match the current feed.
$sourceManifest = $null
foreach ($candidate in (Get-ChildItem 'C:\Users\Laszlos\source\repos\zg-rq-02-fuwen' -Recurse -File -Filter 'zhinu-source-candidate-*.json' -ErrorAction SilentlyContinue)) {
    try { $m = Get-Content $candidate.FullName -Raw | ConvertFrom-Json } catch { continue }
    if (@($m.Packages).Count -ne 7) { continue }
    $ok = $true
    foreach ($p in $m.Packages) {
        $file = Join-Path $sourceFeed $p.File
        if (-not (Test-Path $file)) { $ok = $false; break }
        if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p.Sha256) { $ok = $false; break }
    }
    if ($ok) { $sourceManifest = $candidate.FullName; break }
}
if (-not $sourceManifest) { throw 'No rebuild manifest matches the current staged feed.' }
$pinPath = 'C:\Users\Laszlos\source\repos\zg-rq-02-fuwen\docs\qualification\activity-groups\zhinu-candidate.json'
$verifier = 'C:\Users\Laszlos\source\repos\zg-rq-02-fuwen\eng\Verify-NativeFeed.ps1'
$root = 'C:\Windows\TEMP\opencode\zgrq02\feed-r11'
$feed = Join-Path $root 'R11'
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Path $feed -Force | Out-Null
Copy-Item -Path (Join-Path $sourceFeed '*.nupkg') -Destination $feed

$core = Join-Path $feed 'Penghou.Zhinu.0.2.0-preview.10.nupkg'
$zip = [System.IO.Compression.ZipFile]::Open($core, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $updated = $text -replace '(<dependency id="Penghou\.Workflow\.Abstractions" version=")\[0\.1\.0-preview\.2\](")', '${1}[1.0.0]${2}'
    if ($updated -eq $text) { throw 'Dependency pin replacement did not apply.' }
    $stream = $entry.Open()
    try {
        $stream.SetLength(0)
        $writer = [System.IO.StreamWriter]::new($stream)
        try { $writer.Write($updated) } finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}
finally { $zip.Dispose() }

$newHash = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
foreach ($p in $manifest.Packages) {
    if ($p.Id -eq 'Penghou.Zhinu') { $p.Sha256 = $newHash }
}
$modifiedManifest = Join-Path $root 'rebuild-manifest-r11.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $modifiedManifest -Encoding utf8

$detail = ''
$exitCode = 0
try {
    $output = & "$env:LOCALAPPDATA\Microsoft\WindowsApps\pwsh.exe" -NoProfile -File $verifier `
        -FeedDirectory $feed `
        -PinPath $pinPath `
        -RebuildManifestPath $modifiedManifest `
        -Version '0.2.0-preview.10' `
        -ResolvedSourceCommit 'bf33c05cf2390af6924bbe3761e09f9e377c7a97' 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $detail = $output.Trim()
}
catch {
    $exitCode = 1
    $detail = $_.Exception.Message
}
$observed = if ($exitCode -eq 0) { 'ACCEPTED' } else { 'REFUSED' }
$result = [ordered]@{
    executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    scenario = 'R11-native-dependency-pin'
    expected = 'REFUSED: native core declares a dependency version other than [0.1.0-preview.2]'
    observed = $observed
    exitCode = $exitCode
    detail = $detail
    fixture = [ordered]@{
        feed = $feed
        modifiedPackage = 'Penghou.Zhinu.0.2.0-preview.10.nupkg'
        modifiedDependency = 'Penghou.Workflow.Abstractions [0.1.0-preview.2] -> [1.0.0]'
        rebuildManifestHashUpdated = $true
        originalRebuildManifest = $sourceManifest
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath 'C:\Users\Laszlos\source\repos\zg-rq-02-fuwen\docs\evidence\zg-rq-02\local\feed-r11.json' -Encoding utf8
Write-Output ($result | ConvertTo-Json -Depth 6)
