[CmdletBinding()]
param(
    [string] $BaselinePath,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $BaselinePath) {
    $BaselinePath = Join-Path $repository 'docs/qualification/activity-groups/zhinu-public-baseline.json'
}
$BaselinePath = (Resolve-Path -LiteralPath $BaselinePath).Path
$verifier = Join-Path $PSScriptRoot 'Verify-PublicZhinuBaseline.ps1'
$temp = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path ([IO.Path]::GetTempPath()) "zhinu-public-rejection-$([Guid]::NewGuid().ToString('N'))"
}
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    $baseline.Packages[0].Sha256 = ('0' * 64)
    $mutated = Join-Path $temp 'mutated-baseline.json'
    $baseline | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $mutated -Encoding utf8

    $failed = $false
    $log = ''
    try {
        $log = (& $verifier -BaselinePath $mutated -OutputDirectory (Join-Path $temp 'packages') -ReceiptPath (Join-Path $temp 'receipt.json') *>&1 | Out-String)
    }
    catch {
        $failed = $true
        $log = "$($_ | Out-String)`n$log"
    }
    if (-not $failed) { throw 'Mutated public baseline was not rejected.' }
    if ($log -notmatch 'SHA-256 mismatch') { throw "Mutated baseline failed for an unexpected reason: $log" }

    $receipt = [ordered]@{
        SchemaVersion = 1
        Status = 'passed'
        Control = 'mutated-public-package-hash'
        Rejected = $true
        Reason = 'SHA-256 mismatch'
        MutatedPackage = $baseline.Packages[0].Id
        BaselineSource = $BaselinePath
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $temp 'rejection.json') -Encoding utf8
    $receipt | ConvertTo-Json -Depth 5
}
finally {
    if (Test-Path -LiteralPath $temp) {
        try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Could not remove rejection-control temp directory '$temp': $($_.Exception.Message)" }
    }
}
