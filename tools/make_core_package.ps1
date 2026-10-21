<#
.SYNOPSIS
    Build the small "core" release asset: Restoria.exe + Restoria.exe.config, no AI payload.

.DESCRIPTION
    The AI runtime is optional by design: without an `ai\` directory next to the exe,
    the program's child-process probe fails, the AI features switch themselves off, and
    every other feature keeps working. That is what makes a ~1 MB download possible.

    This script temporarily moves `ai\` out of the way, runs build.bat, zips the exe and
    its .config, and restores `ai\` afterwards even if the build fails.

    The PDB is deliberately not included; ship it separately if you want crash reports
    from core users to resolve to line numbers.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's folder.

.PARAMETER Tag
    Release tag to upload to, e.g. 1.5.8. Required with -Upload.

.PARAMETER Upload
    Run `gh release upload` after building. Requires the GitHub CLI and auth.

.PARAMETER MaxMB
    Fail if the resulting zip is larger than this. Default 3.

.EXAMPLE
    .\tools\make_core_package.ps1

.EXAMPLE
    .\tools\make_core_package.ps1 -Tag 1.5.8 -Upload
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Tag,
    [switch]$Upload,
    [double]$MaxMB = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if ($Upload -and -not $Tag) { throw "-Upload requires -Tag (for example -Tag 1.5.8)." }

$ai     = Join-Path $RepoRoot 'ai'
$aiOff  = Join-Path $RepoRoot 'ai.__hidden_for_core_build'
$dist   = Join-Path $RepoRoot 'dist'
$exe    = Join-Path $dist 'Restoria.exe'
$cfg    = Join-Path $dist 'Restoria.exe.config'
$zip    = Join-Path $dist 'Restoria-core.zip'

if (-not (Test-Path (Join-Path $RepoRoot 'build.bat'))) {
    throw "build.bat not found under $RepoRoot - is this the repository root?"
}

Step "Repository: $RepoRoot"

# ---- hide ai\ -----------------------------------------------------------
$movedAi = $false
if (Test-Path -LiteralPath $ai) {
    if (Test-Path -LiteralPath $aiOff) {
        throw "$aiOff already exists. Remove it first (it is a leftover from an interrupted run)."
    }
    Step "Moving ai\ out of the way (that is what makes this build small)"
    Move-Item -LiteralPath $ai -Destination $aiOff
    $movedAi = $true
} else {
    Step "No ai\ directory present - building without it anyway"
}

try {
    # ---- build ----------------------------------------------------------
    Step "Building"
    $env:PICTOOL_NO_PAUSE = '1'
    Push-Location $RepoRoot
    try {
        & cmd.exe /c build.bat 2>&1 | ForEach-Object { Write-Host $_ }
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($code -ne 0) { throw "build.bat failed with exit code $code" }

    if (-not (Test-Path -LiteralPath $exe)) { throw "Build produced no $exe" }
    if (-not (Test-Path -LiteralPath $cfg)) { throw "Build produced no $cfg" }

    # ---- zip ------------------------------------------------------------
    Step "Packing dist\Restoria-core.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $exe, $cfg -DestinationPath $zip -CompressionLevel Optimal

    $mb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
    Write-Host ("    {0}  ({1} MB)" -f (Split-Path $zip -Leaf), $mb) -ForegroundColor Green
    if ($mb -gt $MaxMB) {
        throw "Core package is $mb MB, above the $MaxMB MB budget. Check that ai\ really was excluded."
    }
}
finally {
    # ---- always restore ai\ --------------------------------------------
    if ($movedAi) {
        Step "Restoring ai\"
        Move-Item -LiteralPath $aiOff -Destination $ai
    }
}

# ---- upload -------------------------------------------------------------
if ($Upload) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw "GitHub CLI (gh) not found. Install it, or upload $zip by hand." }

    Step "Uploading to release $Tag"
    & gh release upload $Tag $zip --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with exit code $LASTEXITCODE" }
    Write-Host "    https://github.com/MC5lan/restoria/releases/tag/$Tag" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  1. Make sure the README download table lists Restoria-core.zip first."
Write-Host "  2. Verify by hand: unzip to an empty folder, delete nothing, run Restoria.exe."
Write-Host "     The AI buttons should be disabled with a reason, everything else should work."
