# verify_parallel.ps1
# Purpose: guard the P0-1 fix -- page-to-page parallel archiving must be
#          byte-for-byte deterministic. Runs the through (thorough) pipeline at
#          several --parallel levels and MD5-compares every output file against
#          the single-threaded (--parallel=1) baseline.
#
# Why this exists: a past review found that the "remove-specks undo insurance"
#   path reads per-page stats; in parallel those stats were shared static state
#   and got overwritten across pages. The old P2 regression only covered the
#   NORMAL grade, while the undo insurance only runs in the THOROUGH grade --
#   that intersection was the blind spot. This script closes it.
#
# Usage:
#   powershell -File tools\verify_parallel.ps1
#   powershell -File tools\verify_parallel.ps1 -In "C:\path\to\pages" -Parallels 1,2,4
#
# Exit code: 0 = all levels byte-identical to baseline; 1 = mismatch/error.
#
# NOTE: keep this file pure ASCII (PS 5.1 reads .ps1 as the OEM codepage;
#       non-ASCII would be garbled). Chinese output is intentionally avoided.

param(
    [string]$In  = "D:\lama\_parallel\in",
    [string]$Work = "D:\lama\_parallel",
    [int[]]$Parallels = @(1, 2, 4),
    [string]$Exe = "D:\lama\dist\PicTool.exe",
    [switch]$Normal   # use normal grade instead of --thorough
)

$ErrorActionPreference = "Stop"

# Log file so the result is readable even when the host does not echo stdout.
if (-not (Test-Path -LiteralPath $Work)) { New-Item -ItemType Directory -Path $Work | Out-Null }
$script:Log = Join-Path $Work "verify_parallel.log"
"verify_parallel started " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") | Set-Content -LiteralPath $script:Log -Encoding ASCII
function Say([string]$msg) {
    Write-Host $msg
    Add-Content -LiteralPath $script:Log -Value $msg -Encoding ASCII
}
function Fail([string]$msg) { Say ("[FAIL] " + $msg); exit 1 }

if (-not (Test-Path -LiteralPath $Exe)) { Fail "exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $In))  { Fail "input dir not found: $In" }

$files = Get-ChildItem -LiteralPath $In -File | Where-Object {
    $_.Extension -match '^\.(jpg|jpeg|png|bmp|tif|tiff)$'
}
if ($files.Count -eq 0) { Fail "no image files in $In" }

$grade = if ($Normal) { "normal" } else { "thorough" }
Say ("verify_parallel: {0} pages, grade={1}, levels={2}" -f $files.Count, $grade, ($Parallels -join ","))
Say ("exe: {0}" -f (Get-Item -LiteralPath $Exe).VersionInfo.FileVersion)
Say ""

$results = @()   # per level: name, dir, seconds, count
foreach ($p in $Parallels) {
    $outDir = Join-Path $Work ("p" + $p)
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir | Out-Null

    $argList = @("--archive", $In, $outDir, "--parallel=$p")
    if (-not $Normal) { $argList += "--thorough" }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # Start-Process -Wait is REQUIRED: '&' does NOT wait for a GUI-subsystem
    # (WinExe) app, so the process would be killed when the shell returns.
    $proc = Start-Process -FilePath $Exe -ArgumentList $argList -Wait -PassThru
    $sw.Stop()

    $cnt = (Get-ChildItem -LiteralPath $outDir -File).Count
    $results += [pscustomobject]@{ Para = $p; Dir = $outDir; Sec = [math]::Round($sw.Elapsed.TotalSeconds, 1); Count = $cnt }
    Say ("  parallel={0}  exit={1}  files={2}  {3}s" -f $p, $proc.ExitCode, $cnt, [math]::Round($sw.Elapsed.TotalSeconds, 1))

    # keep the pipeline report of each run for inspection
    $rep = Join-Path $env:TEMP "PicTool-archive.txt"
    if (Test-Path -LiteralPath $rep) { Copy-Item -LiteralPath $rep (Join-Path $outDir "_report.txt") -Force }
}

$base = $results[0]
Say ""
Say ("baseline = parallel={0}" -f $base.Para)

$allOk = $true
foreach ($r in $results) {
    if ($r.Para -eq $base.Para) { continue }
    $diff = 0; $miss = 0
    foreach ($f in (Get-ChildItem -LiteralPath $base.Dir -File | Where-Object { $_.Name -ne "_report.txt" })) {
        $g = Join-Path $r.Dir $f.Name
        if (-not (Test-Path -LiteralPath $g)) { $miss++; continue }
        $h1 = (Get-FileHash -LiteralPath $f.FullName -Algorithm MD5).Hash
        $h2 = (Get-FileHash -LiteralPath $g -Algorithm MD5).Hash
        if ($h1 -ne $h2) { $diff++ }
    }
    $ok = ($diff -eq 0 -and $miss -eq 0)
    if (-not $ok) { $allOk = $false }
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Say ("  [{0}] parallel={1} vs {2}: diff={3} miss={4}" -f $tag, $r.Para, $base.Para, $diff, $miss)
}

Say ""
if ($allOk) {
    Say "RESULT: PASS - all parallel levels byte-identical to single-thread baseline"
    exit 0
} else {
    Say "RESULT: FAIL - parallel output differs from baseline"
    exit 1
}
