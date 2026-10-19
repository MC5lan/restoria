# probe_layoutcheck.ps1 - does --layout-check actually return a non-zero exit code?
#
# The whole point of the flag is that a CI step can branch on it. A gate that
# cannot fail is decoration, so this measures the exit code rather than
# assuming it: run against a deliberately stale baseline (where line 2 must
# differ) and against the regenerated one (where it must not).
param(
    [string]$Exe = 'D:\lama-oss\dist\Restoria.exe',
    [string]$Root = 'D:\lama-oss'
)

$ErrorActionPreference = 'Stop'
$out = New-Object System.Collections.Generic.List[string]

function Run-LayoutCheck([string]$exe, [string]$dir) {
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = $exe
    $p.StartInfo.Arguments = '--layout-check'
    $p.StartInfo.WorkingDirectory = $dir
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.RedirectStandardOutput = $true
    $p.StartInfo.RedirectStandardError = $true
    $p.StartInfo.CreateNoWindow = $true
    [void]$p.Start()
    # Reading both streams before WaitForExit avoids the classic deadlock when
    # a child fills a pipe buffer.
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(180000)) { throw 'layout-check timed out' }
    return @{
        Code   = $p.ExitCode
        Stdout = $so.Result.Trim()
        Stderr = $se.Result.Trim()
    }
}

$out.Add('=== 1. against the current baseline ===')
$r1 = Run-LayoutCheck $Exe $Root
$out.Add('  exit code : ' + $r1.Code)
$out.Add('  stdout    : ' + $r1.Stdout)
$out.Add('  stderr    : ' + (($r1.Stderr -split "`n" | Select-Object -First 4) -join ' / '))

$line2 = (Get-Content (Join-Path $Root 'tools\golden-layout.txt') -Encoding UTF8)[1]
$out.Add('')
$out.Add('  baseline line 2 : ' + $line2)

$out.Add('')
$out.Add('=== 2. perturb the baseline by one character, then re-check ===')
$goldPath = Join-Path $Root 'tools\golden-layout.txt'
$orig = [System.IO.File]::ReadAllText($goldPath, [System.Text.Encoding]::UTF8)
$perturbed = $orig.Replace('Form client size = 1080x680', 'Form client size = 1080x681')
if ($perturbed -eq $orig) { $out.Add('  (could not perturb: anchor text not found)') }
[System.IO.File]::WriteAllText($goldPath, $perturbed, (New-Object System.Text.UTF8Encoding($true)))
$r2 = Run-LayoutCheck $Exe $Root
$out.Add('  exit code : ' + $r2.Code + '   (expected non-zero)')
$out.Add('  stderr    : ' + (($r2.Stderr -split "`n" | Select-Object -First 3) -join ' / '))
[System.IO.File]::WriteAllText($goldPath, $orig, (New-Object System.Text.UTF8Encoding($true)))
$out.Add('  baseline restored')

$ok = ($r1.Code -eq 0) -and ($r2.Code -ne 0)
$out.Add('')
$out.Add('VERDICT: ' + $(if ($ok) { 'PASS - passes when clean, fails on drift (clean=' + $r1.Code + ', drifted=' + $r2.Code + ')' }
                          else { 'FAIL - does not discriminate (clean=' + $r1.Code + ', drifted=' + $r2.Code + ')' }))

[System.IO.File]::WriteAllText('D:\lama\_lc2.txt', ($out -join "`r`n"), [System.Text.Encoding]::ASCII)
Write-Host ($out -join "`n")