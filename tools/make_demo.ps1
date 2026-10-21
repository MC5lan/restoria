<#
.SYNOPSIS
    Turn a screen recording into the README demo: a silent MP4 (best) and/or a GIF (fallback).

.DESCRIPTION
    GitHub renders an uploaded MP4 as an autoplaying, muted <video> when you drag it into
    the README editor on github.com - it looks far better than a GIF and is what visitors
    see first. The GIF exists for mirrors, chat clients and places that refuse video.

    Recommended recording: 1280x720, 30 fps, cursor visible, notifications off, no dead
    time hunting for menus. Ten seconds is enough.

.PARAMETER Input
    The raw recording (.mp4/.mov/.mkv).

.PARAMETER OutDir
    Where to write the results. Defaults to docs\images.

.PARAMETER Mode
    mp4, gif, or both. Default both.

.PARAMETER Width
    GIF width in pixels. Default 960. GitHub displays at ~880, so wider just costs bytes.

.PARAMETER Fps
    GIF frame rate. Default 12. Drop to 10 if the file is over budget.

.PARAMETER Start
    Optional trim start, in seconds.

.PARAMETER Duration
    Optional trim length, in seconds.

.EXAMPLE
    .\tools\make_demo.ps1 -Input .\rec\demo.mov -Start 1.5 -Duration 10

.EXAMPLE
    .\tools\make_demo.ps1 -Input .\rec\demo.mov -Mode gif -Fps 10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Input,
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\images'),
    [ValidateSet('mp4', 'gif', 'both')]
    [string]$Mode = 'both',
    [int]$Width = 960,
    [int]$Fps = 12,
    [double]$Start = -1,
    [double]$Duration = -1,
    [double]$MaxGifMB = 3,
    [double]$MaxMp4MB = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not (Test-Path -LiteralPath $Input)) { throw "Input not found: $Input" }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    throw "ffmpeg not found on PATH. Install it (winget install Gyan.FFmpeg) and reopen the shell."
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

# Optional trim, applied before decoding for speed.
$trim = @()
if ($Start -ge 0) { $trim += @('-ss', "$Start") }
if ($Duration -gt 0) { $trim += @('-t', "$Duration") }

$mp4Out = Join-Path $OutDir 'demo.mp4'
$gifOut = Join-Path $OutDir 'demo.gif'

if ($Mode -eq 'mp4' -or $Mode -eq 'both') {
    Step "Encoding MP4 (1280 wide, 30 fps, silent)"
    $ffArgs = @('-y') + $trim + @(
        '-i', $Input,
        '-vf', 'scale=1280:-2:flags=lanczos,fps=30',
        '-an',
        '-c:v', 'libx264', '-crf', '24', '-preset', 'slow',
        '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
        $mp4Out
    )
    & $ffmpeg @ffArgs
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on the MP4 pass (exit $LASTEXITCODE)" }

    $mb = [math]::Round((Get-Item -LiteralPath $mp4Out).Length / 1MB, 2)
    $color = if ($mb -le $MaxMp4MB) { 'Green' } else { 'Yellow' }
    Write-Host ("    {0}  ({1} MB)" -f (Split-Path $mp4Out -Leaf), $mb) -ForegroundColor $color
    if ($mb -gt $MaxMp4MB) {
        Write-Warning "MP4 is over $MaxMp4MB MB. Shorten the clip or raise -crf (26-28 is still fine)."
    }
}

if ($Mode -eq 'gif' -or $Mode -eq 'both') {
    Step "Encoding GIF ($Width wide, $Fps fps, 160-colour palette)"
    $vf = "fps=$Fps,scale=${Width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=160:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3"
    $ffArgs = @('-y') + $trim + @(
        '-i', $Input,
        '-vf', $vf,
        '-loop', '0',
        $gifOut
    )
    & $ffmpeg @ffArgs
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on the GIF pass (exit $LASTEXITCODE)" }

    $mb = [math]::Round((Get-Item -LiteralPath $gifOut).Length / 1MB, 2)
    $color = if ($mb -le $MaxGifMB) { 'Green' } else { 'Red' }
    Write-Host ("    {0}  ({1} MB)" -f (Split-Path $gifOut -Leaf), $mb) -ForegroundColor $color
    if ($mb -gt $MaxGifMB) {
        Write-Warning "GIF is over $MaxGifMB MB. Retry with -Fps 10 -Width 840, or shorten the clip."
    }
}

Write-Host ""
Write-Host "Done. Next:" -ForegroundColor Green
Write-Host "  1. Watch the result once, end to end. The first frame must show a dirty page."
Write-Host "  2. On github.com, edit README.md and drag demo.mp4 into the editor; GitHub hosts it."
Write-Host "  3. Keep demo.gif only if you need a fallback for mirrors and chat clients."


