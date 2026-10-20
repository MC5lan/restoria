$ErrorActionPreference = 'Stop'
# 从脚本位置推导仓库根，不写死路径 —— 这个脚本要跟仓库一起走，
# 别人 clone 到别的目录时硬编码会直接失效。
# 用 MyInvocation 而不是 $PSScriptRoot：后者在某些调用方式下为空，
# 空值会让 Join-Path 直接抛 ParameterArgumentValidationError。
$scriptPath = $MyInvocation.MyCommand.Path
if (-not $scriptPath) { $scriptPath = $PSCommandPath }
if (-not $scriptPath) { throw 'cannot determine script path' }
$root = Split-Path -Parent (Split-Path -Parent $scriptPath)
$exe = Join-Path $root 'dist\Restoria.exe'
if (-not (Test-Path $exe)) { throw ("not built: " + $exe + " - run build.bat first") }
$lines = New-Object System.Collections.Generic.List[string]

function Run-LayoutCheck([string]$face) {
    # EnvironmentVariables on ProcessStartInfo is null under PS 5.1; setting the
    # variable in this process and letting the child inherit it works reliably.
    if ($face) { $env:PICTOOL_UI_FACE = $face } else { Remove-Item Env:\PICTOOL_UI_FACE -ErrorAction SilentlyContinue }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = $exe
    $p.StartInfo.Arguments = '--layout-check'
    $p.StartInfo.WorkingDirectory = $root
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.RedirectStandardOutput = $true
    $p.StartInfo.RedirectStandardError = $true
    $p.StartInfo.CreateNoWindow = $true
    [void]$p.Start()
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    [void]$p.WaitForExit(180000)
    Remove-Item Env:\PICTOOL_UI_FACE -ErrorAction SilentlyContinue
    $snap = ''
    $rep = Join-Path $env:TEMP 'PicTool-layout.txt'
    if (Test-Path $rep) { $snap = (Get-FileHash $rep -Algorithm SHA256).Hash.Substring(0, 16) }
    $font = ''
    $diag = Join-Path $env:TEMP 'PicTool-layout-diag.txt'
    if (Test-Path $diag) {
        $first = (Get-Content $diag -Encoding UTF8 | Select-Object -First 1)
        if ($first) { $font = $first }
    }
    return @{ Code = $p.ExitCode; Hash = $snap; Diag = $font }
}

$lines.Add('=== default face (whatever this machine has) ===')
$r0 = Run-LayoutCheck $null
$lines.Add('  exit=' + $r0.Code + '  snapshot=' + $r0.Hash)
$lines.Add('  ' + $r0.Diag)

$lines.Add('')
$lines.Add('=== forced to Segoe UI (simulates a runner without YaHei) ===')
$r1 = Run-LayoutCheck 'Segoe UI'
$lines.Add('  exit=' + $r1.Code + '  snapshot=' + $r1.Hash)
$lines.Add('  ' + $r1.Diag)

$lines.Add('')
$lines.Add('=== forced to Microsoft YaHei ===')
$r2 = Run-LayoutCheck 'Microsoft YaHei'
$lines.Add('  exit=' + $r2.Code + '  snapshot=' + $r2.Hash)
$lines.Add('  ' + $r2.Diag)

$lines.Add('')
$same = ($r0.Hash -eq $r1.Hash) -and ($r0.Hash -eq $r2.Hash)
$lines.Add('all exit 0: ' + (($r0.Code -eq 0) -and ($r1.Code -eq 0) -and ($r2.Code -eq 0)))
$lines.Add('all snapshots identical: ' + $same)
$lines.Add('')
if (($r0.Code -eq 0) -and ($r1.Code -eq 0) -and ($r2.Code -eq 0) -and $same) {
    $lines.Add('VERDICT: PASS - the snapshot is font-independent.')
} else {
    $lines.Add('VERDICT: FAIL - font still affects the snapshot.')
}

[System.IO.File]::WriteAllText((Join-Path $root '_fonttest.txt'), ($lines -join "`r`n"), [System.Text.Encoding]::ASCII)
Write-Host ($lines -join "`n")
