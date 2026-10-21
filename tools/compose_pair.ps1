<#
.SYNOPSIS
    Build the README hero image: one real page, before and after, side by side.

.DESCRIPTION
    Takes the original scan and the file Restoria produced for it, crops the same
    *relative* region out of both, scales them to the same width, and writes a single
    PNG with a 2px divider. Relative crops are used because the pipeline may rotate or
    resize the page, so "the same pixel rectangle" would not line up.

    Output is kept under -MaxKB by stepping the width down until it fits, because a
    README image that takes ten seconds to load costs more than it earns.

.EXAMPLE
    .\tools\compose_pair.ps1 -Before page_001.tif -After out\page_001.png

.EXAMPLE
    # aim at the seal in the lower right
    .\tools\compose_pair.ps1 -Before page_001.tif -After out\page_001.png `
        -Rect "0.55,0.60,0.40,0.30" -Width 560 -Out docs\images\real-page.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After,
    [string]$Out = "docs\images\real-page.png",
    [string]$Rect = "0.30,0.22,0.40,0.34",
    [int]$Width = 600,
    [int]$MaxKB = 400,
    [switch]$Labels
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

foreach ($p in @($Before, $After)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Not found: $p" }
}

$frac = $Rect -split ',' | ForEach-Object { [double]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture) }
if ($frac.Count -ne 4) { throw "-Rect needs four comma-separated fractions: x,y,w,h" }
$fx, $fy, $fw, $fh = $frac

function Get-Crop([string]$path) {
    $img = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $path).Path)
    try {
        $x = [int]($img.Width * $fx)
        $y = [int]($img.Height * $fy)
        $w = [int]($img.Width * $fw)
        $h = [int]($img.Height * $fh)
        if ($w -lt 8 -or $h -lt 8) { throw "Crop is too small for $path ($w x $h)" }
        if ($x + $w -gt $img.Width) { $w = $img.Width - $x }
        if ($y + $h -gt $img.Height) { $h = $img.Height - $y }

        $bmp = New-Object System.Drawing.Bitmap($img)
        try {
            $rect = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
            return $bmp.Clone($rect, $bmp.PixelFormat)
        } finally { $bmp.Dispose() }
    } finally { $img.Dispose() }
}

function New-Panel($src, [int]$targetW) {
    $ratio = $targetW / [double]$src.Width
    $h = [int][math]::Round($src.Height * $ratio)
    $bmp = New-Object System.Drawing.Bitmap($targetW, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($src, 0, 0, $targetW, $h)
    } finally { $g.Dispose() }
    return $bmp
}

function Add-Label($bmp, [string]$text) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $font = New-Object System.Drawing.Font("Segoe UI", [math]::Max(11, $bmp.Width / 45), [System.Drawing.FontStyle]::Bold)
        $size = $g.MeasureString($text, $font)
        $pad = 8
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(190, 0, 0, 0))
        $g.FillRectangle($brush, 0, 0, $size.Width + $pad * 2, $size.Height + $pad)
        $g.DrawString($text, $font, [System.Drawing.Brushes]::White, $pad, $pad / 2)
        $brush.Dispose(); $font.Dispose()
    } finally { $g.Dispose() }
}

$beforeCrop = Get-Crop $Before
$afterCrop = Get-Crop $After

try {
    $w = $Width
    while ($true) {
        $left = New-Panel $beforeCrop $w
        $right = New-Panel $afterCrop $w
        try {
            if ($Labels) { Add-Label $left 'BEFORE'; Add-Label $right 'AFTER' }

            $gap = 2
            $h = [math]::Max($left.Height, $right.Height)
            $canvas = New-Object System.Drawing.Bitmap(($w * 2 + $gap), $h)
            $g = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $g.Clear([System.Drawing.Color]::White)
                $g.DrawImage($left, 0, 0)
                $g.DrawImage($right, ($w + $gap), 0)
                $divider = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(221, 221, 221))
                $g.FillRectangle($divider, $w, 0, $gap, $h)
                $divider.Dispose()
            } finally { $g.Dispose() }

            $dir = Split-Path -Parent $Out
            if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

            $ms = New-Object System.IO.MemoryStream
            $canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $kb = [math]::Round($ms.Length / 1KB, 1)
            if ($kb -le $MaxKB -or $w -le 320) {
                $outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out }
                           else { Join-Path (Get-Location).Path $Out }
                $outDir = Split-Path -Parent $outPath
                if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
                    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
                }
                [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
                $ms.Dispose(); $canvas.Dispose()
                Write-Host ("wrote {0}  {1}x{2}  {3} KB" -f $outPath, ($w * 2 + 2), $h, $kb) -ForegroundColor Green
                if ($kb -gt $MaxKB) { Write-Warning "Still over $MaxKB KB at the minimum width; consider -Rect to a smaller area." }
                break
            }
            $ms.Dispose(); $canvas.Dispose()
            $w = [int]($w * 0.92)
        } finally {
            $left.Dispose(); $right.Dispose()
        }
    }
} finally {
    $beforeCrop.Dispose()
    $afterCrop.Dispose()
}
