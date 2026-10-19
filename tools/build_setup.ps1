# build_setup.ps1 - build the installer and the uninstaller
#
#   dist\setup-boot.exe  <- installer  (guide layer + payload reader + wizard + EULA)
#                           packing script appends the payload, see tools\pack_payload.ps1
#   dist\uninst.exe      <- uninstaller (shipped inside the payload, installed as Uninstall.exe)
#
# Both exes are tiny standalone .NET 4.x WinForms programs built with the in-box csc.exe.
# No third-party toolchain. Sources are shared where it matters (Theme / SetupInfo / FlatButton)
# so the two programs can never drift apart visually.
#
# Usage: powershell -File tools\build_setup.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
Set-Location $root

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (need .NET Framework 4.x)' }
$fx = Split-Path -Parent $csc

if (-not (Test-Path 'dist')) { New-Item -ItemType Directory -Path 'dist' -Force | Out-Null }

# ---------- version consistency: one product version, four declarations ----------
# NOTE: keep this inline (no helper function). A PS helper taking two positional
# strings trips the host parser here; inlining is boring but reliable.
$rawApp = Get-Content 'src\Core\Version.cs' -Raw -Encoding UTF8
$rawSetup = Get-Content 'tools\install\AssemblySetup.cs' -Raw -Encoding UTF8
$rawUninst = Get-Content 'tools\install\AssemblyUninst.cs' -Raw -Encoding UTF8
$rawInfo = Get-Content 'tools\install\SetupInfo.cs' -Raw -Encoding UTF8

$mApp = [regex]::Match($rawApp, 'public const string Version\s*=\s*"([0-9.]+)"')
$mSetup = [regex]::Match($rawSetup, 'AssemblyVersion\("([0-9.]+)"\)')
$mUninst = [regex]::Match($rawUninst, 'AssemblyVersion\("([0-9.]+)"\)')
$mInfo = [regex]::Match($rawInfo, 'public const string Version\s*=\s*"([0-9.]+)"')

if (-not $mApp.Success) { throw 'cannot read version from src\Core\Version.cs' }
if (-not $mSetup.Success) { throw 'cannot read version from tools\install\AssemblySetup.cs' }
if (-not $mUninst.Success) { throw 'cannot read version from tools\install\AssemblyUninst.cs' }
if (-not $mInfo.Success) { throw 'cannot read version from tools\install\SetupInfo.cs' }

$vApp = $mApp.Groups[1].Value
# main app version is 3-part (x.y.z); assembly version is always 4-part -> append one zero
if ($vApp.Split('.').Count -eq 3) { $vApp4 = $vApp + '.0' }
elseif ($vApp.Split('.').Count -eq 4) { $vApp4 = $vApp }
else { throw ('unexpected version format in src\Core\Version.cs: ' + $vApp) }
$bad = @()
if ($mSetup.Groups[1].Value -ne $vApp4) { $bad += ('AssemblySetup.cs = ' + $mSetup.Groups[1].Value) }
if ($mUninst.Groups[1].Value -ne $vApp4) { $bad += ('AssemblyUninst.cs = ' + $mUninst.Groups[1].Value) }
if ($mInfo.Groups[1].Value -ne $vApp) { $bad += ('SetupInfo.cs = ' + $mInfo.Groups[1].Value) }
if ($bad.Count -gt 0) { throw ('version mismatch (main app ' + $vApp + '): ' + ($bad -join ', ')) }
Write-Host ('  version ok: ' + $vApp)

$common = @(
    'src\Ui\Theme.cs',
    'tools\install\SetupInfo.cs',
    'tools\install\FlatButton.cs'
)
$setupSrc = $common + @(
    'tools\install\AssemblySetup.cs',
    'tools\install\Payload.cs',
    'tools\install\SetupBoot.cs',
    'tools\install\EulaText.cs',
    'tools\install\EulaForm.cs',
    'tools\install\SetupGui.cs'
)
$uninstSrc = $common + @(
    'tools\install\AssemblyUninst.cs',
    'tools\install\UninstallGui.cs'
)

foreach ($s in ($setupSrc + $uninstSrc)) {
    if (-not (Test-Path $s)) { throw ('source file not found: ' + $s) }
}

function Compile($outExe, $manifest, $srcFiles) {
    $a = @(
        '/noconfig', '/nostdlib+', '/nologo', '/target:winexe', '/platform:anycpu',
        '/optimize+', '/codepage:65001', '/langversion:5'
    )
    if (Test-Path 'assets\app.ico') { $a += '/win32icon:assets\app.ico' }
    if (Test-Path $manifest) { $a += ('/win32manifest:' + $manifest) }
    $a += ('/r:' + $fx + '\mscorlib.dll')
    $a += ('/r:' + $fx + '\System.dll')
    $a += ('/r:' + $fx + '\System.Core.dll')
    $a += ('/r:' + $fx + '\System.Drawing.dll')
    $a += ('/r:' + $fx + '\System.Windows.Forms.dll')
    $a += ('/out:' + $outExe)
    $a += $srcFiles

    & $csc @a
    if ($LASTEXITCODE -ne 0) { throw ('compile failed: ' + $outExe + ' (exit ' + $LASTEXITCODE + ')') }
    if (-not (Test-Path $outExe)) { throw ('compile failed, no output: ' + $outExe) }
    $kb = [Math]::Round((Get-Item $outExe).Length / 1KB, 1)
    Write-Host ('  OK -> ' + $outExe + '  (' + $kb + ' KB)')
}

Write-Host '=== build installer (setup-boot.exe) ==='
Compile 'dist\setup-boot.exe' 'assets\setup.manifest' $setupSrc

Write-Host '=== build uninstaller (uninst.exe) ==='
Compile 'dist\uninst.exe' 'assets\uninst.manifest' $uninstSrc
