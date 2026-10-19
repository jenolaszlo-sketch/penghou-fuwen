[CmdletBinding()]
param([Parameter(Mandatory)][string] $ArtifactsDirectory, [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $CandidatePinPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$fixture = Join-Path $output ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'eng'), (Join-Path $fixture 'docs/qualification/activity-groups') -Force | Out-Null
$pinPath = Join-Path $fixture 'docs/qualification/activity-groups/zhinu-candidate.json'
Copy-Item -LiteralPath (Join-Path $repository 'docs/qualification/activity-groups/zhinu-candidate.json') -Destination $pinPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Prepare-ZhinuCandidate.ps1') -Destination (Join-Path $fixture 'eng/Prepare-ZhinuCandidate.ps1')
$helper = Join-Path $fixture 'eng/Prepare-ZhinuCandidate.ps1'
$pinText = if ($CandidatePinPath) { Get-Content -LiteralPath $CandidatePinPath -Raw } else { Get-Content -LiteralPath $pinPath -Raw }
$pinDigest = (Get-FileHash -LiteralPath $pinPath -Algorithm SHA256).Hash
$feed = Join-Path $fixture 'artifacts/zhinu-candidate'
if ($CandidatePinPath) { & $helper -ArtifactsDirectory $ArtifactsDirectory -PinPath $CandidatePinPath }
else { & $helper -ArtifactsDirectory $ArtifactsDirectory }
$before = @{}
foreach ($file in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg') {
    $before[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
if ($before.Count -ne 7) { throw 'Positive adoption did not produce the seven-package closure.' }
$cases = @()
foreach ($kind in @('source', 'version', 'missing-package', 'duplicate-package', 'changed-hash-without-provenance', 'last-package-hash')) {
    $pin = $pinText | ConvertFrom-Json
    switch ($kind) {
        'source' { $pin.SourceCommit = '0000000000000000000000000000000000000000' }
        'version' { $pin.Version = '0.0.0-invalid' }
        'missing-package' { $pin.Packages = @($pin.Packages | Select-Object -First 6) }
        'duplicate-package' { $pin.Packages[6] = $pin.Packages[0] }
        'changed-hash-without-provenance' {
            $pin.Packages[6].Sha256 = '0' * 64
            $pin.PSObject.Properties.Remove('Provenance')
        }
        'last-package-hash' {
            $pin.Packages[6].Sha256 = '0' * 64
            $pin | Add-Member -Force -NotePropertyName Provenance -NotePropertyValue @{ Kind = 'source-rebuild'; OriginalArchivePinSha256 = $pinDigest }
        }
    }
    $selectedPin = Join-Path $fixture "$kind.json"
    $pin | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $selectedPin -Encoding utf8
    $rejected = $false
    try { & $helper -ArtifactsDirectory $ArtifactsDirectory -PinPath $selectedPin }
    catch { $rejected = $true; $reason = $_.Exception.Message }
    if (-not $rejected) { throw "Invalid preparation case was accepted: $kind" }
    $files = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg')
    if ($files.Count -ne 7) { throw "Feed was partially altered after rejecting $kind" }
    foreach ($file in $files) {
        if ($before[$file.Name] -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "Feed changed after rejecting $kind"
        }
    }
    $cases += [ordered]@{ Case = $kind; Rejected = $true; FeedUnchanged = $true; Reason = $reason }
}
if ((Get-FileHash -LiteralPath $pinPath -Algorithm SHA256).Hash -ne $pinDigest) { throw 'Tracked archive pin was overwritten.' }
[ordered]@{ Status = 'passed'; OriginalPinUnchanged = $true; AdoptedPackages = 7; RejectionCases = $cases } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture 'qualification.json') -Encoding utf8
Write-Output "Candidate preparation checks passed: $(Join-Path $fixture 'qualification.json')"
