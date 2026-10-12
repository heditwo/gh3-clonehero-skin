# Copies each stage-events.json of this GH3 direction archive into the song folder whose chart has the SHA-256 of
# gh3-direction-charts.sha256. It looks in the songs folder and in all its subfolders. It never replaces a file.
# Usage: install-direction.bat ["<songs folder>"], or powershell -File install-direction.ps1 [-SongsDir <folder>] [-DryRun | -Yes]
# It shows the list first, then asks before it copies. -DryRun shows the list only; -Yes copies without the question.
# Windows PowerShell 5.1 or later.
param(
    [Parameter(Position = 0)][string]$SongsDir,
    [switch]$DryRun,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$sidecar = 'stage-events.json'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$index = Join-Path $here 'gh3-direction-charts.sha256'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $index -PathType Leaf)) { Fail "$index is missing. Run the script from the extracted archive." }
if (-not $SongsDir) { $SongsDir = Read-Host 'Clone Hero songs folder' }
$SongsDir = $SongsDir.Trim().Trim('"')
if (-not $SongsDir -or -not (Test-Path -LiteralPath $SongsDir -PathType Container)) { Fail "The folder '$SongsDir' does not exist." }

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Windows PowerShell 5.1 reads files without a byte order mark as ANSI; the folder names are UTF-8.
$entries = @(Get-Content -LiteralPath $index -Encoding UTF8 | Where-Object { $_ -match '^[0-9a-f]{64}  .+/' } | ForEach-Object {
        $entry = $_.Substring(66)
        $slash = $entry.LastIndexOf('/')
        [pscustomobject]@{ Hash = $_.Substring(0, 64); Folder = $entry.Substring(0, $slash); Chart = $entry.Substring($slash + 1) }
    })
if ($entries.Count -eq 0) { Fail "$index has no entries." }
$names = @($entries | ForEach-Object { $_.Chart } | Sort-Object -Unique)

$charts = @{}
$checked = 0
Get-ChildItem -LiteralPath $SongsDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $names -contains $_.Name } | ForEach-Object {
        $hash = Get-Sha256 $_.FullName
        if (-not $charts.ContainsKey($hash)) { $charts[$hash] = New-Object System.Collections.ArrayList }
        [void]$charts[$hash].Add($_.DirectoryName)
        $checked++
    }
Write-Host "Checked $checked charts in $SongsDir"
Write-Host ''

$plan = New-Object System.Collections.ArrayList
$present = 0; $kept = 0; $missing = 0
foreach ($entry in $entries) {
    $source = Join-Path (Join-Path $here $entry.Folder) $sidecar
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { Fail "$($entry.Folder)/$sidecar is missing in the archive." }
    if (-not $charts.ContainsKey($entry.Hash)) {
        $missing++
        Write-Host "Not found:       $($entry.Folder) (no chart with its SHA-256)"
        continue
    }
    foreach ($folder in $charts[$entry.Hash]) {
        $target = Join-Path $folder $sidecar
        if (Test-Path -LiteralPath $target) {
            if ((Get-Sha256 $source) -eq (Get-Sha256 $target)) {
                $present++
                Write-Host "Already there:   $folder"
            } else {
                $kept++
                Write-Host "Kept (another $sidecar): $folder"
            }
        } else {
            Write-Host "Copy:            $($entry.Folder) -> $folder"
            [void]$plan.Add([pscustomobject]@{ Source = $source; Target = $target })
        }
    }
}
Write-Host ''
Write-Host "$($plan.Count) to copy, $present already there, $kept kept (another file), $missing songs not found."

if ($plan.Count -eq 0) { exit 0 }
if ($DryRun) { Write-Host 'Dry run: no file was copied.'; exit 0 }
if (-not $Yes) {
    $answer = Read-Host "Copy $($plan.Count) files? Type y and push Enter"
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'No file was copied.'; exit 0 }
}

$copied = 0
foreach ($item in $plan) {
    # Another program can make the file after the plan; do not replace it.
    if (Test-Path -LiteralPath $item.Target) {
        Write-Host "Kept (made since the check): $($item.Target)"
        continue
    }
    [System.IO.File]::Copy($item.Source, $item.Target, $false)
    $copied++
}
Write-Host "Copied $copied files. In Clone Hero, scan the songs again."
