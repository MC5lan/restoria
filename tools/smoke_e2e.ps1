# smoke_e2e.ps1 — 端到端冒烟（Inno Setup 版）：安装 → 核对 → 卸载 → 确认清干净
#
# 为什么要有它：安装器最容易出问题的地方不是界面，而是"装出来的东西对不对、
# 卸载能不能清干净"。旧版自研安装器有私有载荷索引要验，换成 Inno Setup 后
# 仍然需要真跑一遍：静默安装、关键文件、快捷方式、注册表卸载入口、静默卸载。
#
# 用法：
#   powershell -File tools\smoke_e2e.ps1
#   powershell -File tools\smoke_e2e.ps1 -Dir D:\tmp\e2e
#
# 说明：
#   · 全程静默（/VERYSILENT），不弹任何窗口
#   · 装到临时位置而不是 Program Files，装完就地卸载
#   · 只动自己新建的那个临时目录；不动系统里已有的正式安装
#   · 退出码：0 = 全过，1 = 有失败项（供 CI 阻断）
param(
    [string]$Dir = ''
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$out = New-Object System.Collections.ArrayList
$fail = 0

function W([string]$s) { [void]$out.Add($s) }
function Chk([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { W ('  [PASS] ' + $name.PadRight(34) + $detail) }
    else { W ('  [FAIL] ' + $name.PadRight(34) + $detail); $script:fail++ }
}
function Skip([string]$name, [string]$why) {
    W ('  [SKIP] ' + $name.PadRight(34) + $why)
}

$AppName = 'Restoria'
$MainExe = 'Restoria.exe'
$UninstExe = 'unins000.exe'
$AppId = '{8F3C2A41-6D5B-4E7A-9C21-3B8E5A7D1F40}_is1'

if ([string]::IsNullOrEmpty($Dir)) { $Dir = Join-Path $env:TEMP 'restoria-e2e' }
$pkg = Join-Path $root '发布包\Restoria-setup.exe'

W '============================================================'
W (' 端到端冒烟    ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
W (' 安装包: ' + $pkg)
W (' 装载点: ' + $Dir)
W '============================================================'

if (-not (Test-Path -LiteralPath $pkg)) {
    W '  [FAIL] 找不到安装包，先跑 tools\make_package.ps1'
    $text = ($out -join "`r`n") + "`r`n"
    Write-Output $text
    exit 2
}

# ---------- 0. 清场 ----------
W ''
W '[0] 清场'
if (Test-Path -LiteralPath $Dir) { Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue }
Chk '临时装载点已清空' (-not (Test-Path -LiteralPath $Dir)) $Dir

# ---------- 1. 安装 ----------
W ''
W '[1] 静默安装 (/VERYSILENT)'
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $pkg -ArgumentList @('/VERYSILENT', '/NORESTART', '/SP-', ("/DIR=""$Dir""")) -PassThru -Wait
$sw.Stop()
Chk '安装程序退出码 0' ($p.ExitCode -eq 0) ('exit=' + $p.ExitCode + '，耗时 ' + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + ' 秒')

# ---------- 2. 核对装了些什么 ----------
W ''
W '[2] 安装结果'
$required = @(
    $MainExe, 'Restoria.exe.config', $UninstExe,
    'ai\onnxruntime.dll', 'ai\migan.onnx', 'ai\win7\onnxruntime.dll',
    'THIRD_PARTY_NOTICES'
)
foreach ($f in $required) {
    $fp = Join-Path $Dir $f
    $e = Test-Path -LiteralPath $fp
    $d = '缺失'
    if ($e) { $d = [string](Get-Item -LiteralPath $fp).Length + ' 字节' }
    Chk ('  ' + $f) $e $d
}
# 旧安装器的两个文件不应被 Inno Setup 装进来
foreach ($f in @('Uninstall.exe', 'archivesuite.install')) {
    Chk ('已排除旧件 ' + $f) (-not (Test-Path -LiteralPath (Join-Path $Dir $f))) ''
}

# 签名：三态（不签名是项目的既定状态，不算失败）
$sigMain = Get-AuthenticodeSignature -LiteralPath (Join-Path $Dir $MainExe)
if ($sigMain.Status -eq 'Valid') { Chk '主程序签名' $true ('Valid ' + [string]$sigMain.SignerCertificate.Subject) }
elseif ($sigMain.Status -eq 'NotSigned') { Skip '主程序签名' '未签名（已知状态）' }
else { Chk '主程序签名' $false ([string]$sigMain.Status + ' ' + [string]$sigMain.StatusMessage) }

# 卸载登记：32 位 Inno Setup 安装器写在 HKLM 的 Registry32 视图
$regFound = $false
$regDetail = ''
foreach ($rootKey in @('LocalMachine', 'CurrentUser')) {
    foreach ($view in @('Registry32', 'Registry64')) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($rootKey, $view)
            $k = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppId)
            if ($k) {
                $regFound = $true
                $us = [string]$k.GetValue('UninstallString')
                $regDetail = $rootKey + '/' + $view + ' :: ' + [string]$k.GetValue('DisplayName') + ' :: ' + $us
                Chk '卸载串指向 unins000.exe' ($us -like ('*' + $UninstExe + '*')) $us
                $k.Close()
            }
            $base.Close()
        } catch { }
    }
}
Chk '存在卸载登记' $regFound $regDetail

$desk = [Environment]::GetFolderPath('CommonDesktopDirectory')
Chk '桌面快捷方式' (Test-Path -LiteralPath (Join-Path $desk ($AppName + '.lnk'))) (Join-Path $desk ($AppName + '.lnk'))
$sm = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) ('Programs\' + $AppName)
Chk '开始菜单文件夹' (Test-Path -LiteralPath $sm) $sm
if (Test-Path -LiteralPath $sm) {
    Chk '开始菜单-主程序' (Test-Path -LiteralPath (Join-Path $sm ($AppName + '.lnk'))) ''
    Chk '开始菜单-卸载' (Test-Path -LiteralPath (Join-Path $sm ('卸载 ' + $AppName + '.lnk'))) ''
}

# ---------- 3. 卸载 ----------
W ''
W '[3] 静默卸载 (/VERYSILENT)'
$un = Join-Path $Dir $UninstExe
if (Test-Path -LiteralPath $un) {
    $q = Start-Process -FilePath $un -ArgumentList '/VERYSILENT', '/NORESTART' -PassThru -Wait
    W ('        卸载器退出码 ' + $q.ExitCode)
    # Inno Setup 会保留程序运行时生成的用户文件（如 keys.ini），
    # 所以判据用"关键文件都没了"而不是"目录不存在"。
    $left = @()
    foreach ($f in $required) { if (Test-Path -LiteralPath (Join-Path $Dir $f)) { $left += $f } }
    Chk '安装文件已清除' ($left.Count -eq 0) ('残留 ' + $left.Count + ' 项' + $(if ($left.Count) { '：' + ($left -join ', ') } else { '' }))
    Chk '桌面快捷方式已删除' (-not (Test-Path -LiteralPath (Join-Path $desk ($AppName + '.lnk')))) ''
    Chk '开始菜单文件夹已删除' (-not (Test-Path -LiteralPath $sm)) ''

    $regLeft = $false
    foreach ($rootKey in @('LocalMachine', 'CurrentUser')) {
        foreach ($view in @('Registry32', 'Registry64')) {
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($rootKey, $view)
                if ($base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppId)) { $regLeft = $true }
                $base.Close()
            } catch { }
        }
    }
    Chk '卸载登记已删除' (-not $regLeft) $AppId
} else {
    Chk '卸载器存在' $false $un
}

# ---------- 汇总 ----------
W ''
W '============================================================'
if ($fail -eq 0) { W ' 结论: 端到端冒烟全部通过' } else { W (' 结论: ' + $fail + ' 项失败，请看上面 [FAIL] 行') }
W '============================================================'

$text = ($out -join "`r`n") + "`r`n"
Write-Output $text
try { [IO.File]::WriteAllText((Join-Path $root '发布包\端到端冒烟报告.txt'), $text, (New-Object System.Text.UTF8Encoding($true))) } catch { }
if ($fail -gt 0) { exit 1 }
exit 0
