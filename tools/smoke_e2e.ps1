# smoke_e2e.ps1 - 端到端冒烟：一条命令走完"安装 → 核对 → 卸载 → 确认清干净"。
#
# 为什么要有它：单文件安装包最容易出问题的地方不是界面，而是**包能不能被自己认出来**。
# 打包时校验通过、签名后却认不出载荷（索引被签名数据挤走），这个坑靠肉眼看不出来
# —— 只能真跑一遍。所以这个脚本每次改完打包链路都应该跑一次。
#
# 用法：
#   powershell -File tools\smoke_e2e.ps1
#   powershell -File tools\smoke_e2e.ps1 -Dir D:\tmp\e2e      # 指定装载位置
#
# 说明：
#   · 全程用 --auto --exit（跳过许可协议页、装完自动退出），不弹任何窗口
#   · 装到临时位置而不是 Program Files，装完就地卸载，机器上不留痕迹
#   · 只动自己新建的那个临时目录；不动系统里已有的正式安装
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

$AppName = 'Restoria'
$MainExe = 'Restoria.exe'
$UninstExe = 'Uninstall.exe'
$AppId = 'PicToolArchiveSuite'

if ([string]::IsNullOrEmpty($Dir)) { $Dir = Join-Path $env:TEMP 'pictool-e2e-inst' }
$pkg = Join-Path $root '发布包\Restoria-安装.exe'

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
$tp = Join-Path $env:TEMP 'PicToolUninstall'
if (Test-Path -LiteralPath $tp) { Remove-Item -LiteralPath $tp -Recurse -Force -ErrorAction SilentlyContinue }

# ---------- 1. 安装 ----------
W ''
W '[1] 跑单文件安装程序 (--auto --exit)'
$log = Join-Path $env:TEMP 'PicTool-install.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue }
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $pkg -ArgumentList @('--dir', $Dir, '--auto', '--exit') -PassThru -Wait
$sw.Stop()
Chk '安装程序退出码 0' ($p.ExitCode -eq 0) ('exit=' + $p.ExitCode + '，耗时 ' + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + ' 秒')

# ---------- 2. 核对装了些什么 ----------
W ''
W '[2] 安装结果'
foreach ($f in @($MainExe, $UninstExe, 'archivesuite.install', 'ai\onnxruntime.dll', 'ai\migan.onnx')) {
    $fp = Join-Path $Dir $f
    $e = Test-Path -LiteralPath $fp
    $d = '缺失'
    if ($e) { $d = [string](Get-Item -LiteralPath $fp).Length + ' 字节' }
    Chk ('  ' + $f) $e $d
}
$sigMain = Get-AuthenticodeSignature -LiteralPath (Join-Path $Dir $MainExe)
Chk '主程序签名 Valid' ($sigMain.Status -eq 'Valid') ([string]$sigMain.Status)
$sigUn = Get-AuthenticodeSignature -LiteralPath (Join-Path $Dir $UninstExe)
Chk '卸载程序签名 Valid' ($sigUn.Status -eq 'Valid') ([string]$sigUn.Status)

if (Test-Path -LiteralPath $log) {
    # 必须显式 -Encoding UTF8：PS 5.1 的 Get-Content 默认按系统 ANSI 码页读，
    # 中文会变乱码，'*安装完成*' 这种匹配就会假失败。
    $tail = @(Get-Content -LiteralPath $log -Encoding UTF8 -Tail 40)
    $joined = $tail -join "`n"
    Chk '日志里有"安装完成"' ($joined -like '*安装完成*') ('日志 ' + $log + '（读回 ' + $tail.Count + ' 行）')
    $aiLine = @($tail | Where-Object { $_ -like '*AI 可用*' })
    if ($aiLine.Count -gt 0) { W ('        ' + ($aiLine[0].Trim())) }
} else {
    Chk '安装日志已生成' $false $log
}

# 载荷解压出来的临时目录应当已经收掉（判据同样用"递归一个都不剩"，父目录空壳不算通过）
$bootTmp = Join-Path $env:TEMP 'PicToolSetup'
$bLeft = 0
if (Test-Path -LiteralPath $bootTmp) { $bLeft = @(Get-ChildItem -LiteralPath $bootTmp -Recurse -Force -ErrorAction SilentlyContinue).Count }
Chk '解压临时目录已回收' ((-not (Test-Path -LiteralPath $bootTmp)) -and $bLeft -le 0) ($bootTmp + ' 剩 ' + $bLeft + ' 项')

# ---------- 3. 卸载 ----------
W ''
W '[3] 跑卸载程序 (--silent)'
$un = Join-Path $Dir $UninstExe
if (Test-Path -LiteralPath $un) {
    $q = Start-Process -FilePath $un -ArgumentList '--silent' -PassThru -Wait
    W ('        启动器退出码 ' + $q.ExitCode)
    $t = 0
    while ($t -lt 30 -and (Test-Path -LiteralPath $Dir)) { Start-Sleep -Seconds 1; $t++ }
    Chk '安装目录已删除' (-not (Test-Path -LiteralPath $Dir)) ('等了 ' + $t + ' 秒')

    $desk = [Environment]::GetFolderPath('CommonDesktopDirectory')
    Chk '桌面快捷方式已删除' (-not (Test-Path -LiteralPath (Join-Path $desk ($AppName + '.lnk')))) ''
    $sm = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) ('Programs\' + $AppName)
    Chk '开始菜单文件夹已删除' (-not (Test-Path -LiteralPath $sm)) ''

    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $k = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppId)
    Chk '卸载登记已删除' ($k -eq $null) ('HKLM\...\Uninstall\' + $AppId)
    $base.Close()

    # 自清理是"延迟执行的 cmd"：写完脚本、ping 三下再删，所以要等一下再看。
    # 判据用"递归数到一个都不剩"，而不是"父目录是否存在" —— 父目录是个空壳，
    # 存在与否不代表清理成功。
    $purgeTmp = Join-Path $env:TEMP 'PicToolUninstall'
    $left = -1
    for ($k = 0; $k -lt 15; $k++) {
        $left = 0
        if (Test-Path -LiteralPath $purgeTmp) {
            $left = @(Get-ChildItem -LiteralPath $purgeTmp -Recurse -Force -ErrorAction SilentlyContinue).Count
        }
        if ($left -eq 0 -and -not (Test-Path -LiteralPath $purgeTmp)) { break }
        Start-Sleep -Seconds 1
    }
    Chk '卸载临时目录已自清' ((-not (Test-Path -LiteralPath $purgeTmp)) -and $left -le 0) ($purgeTmp + ' 剩 ' + $left + ' 项')
} else {
    Chk '卸载程序存在' $false $un
}

# ---------- 汇总 ----------
W ''
W '============================================================'
if ($fail -eq 0) { W ' 结论: 端到端冒烟全部通过' } else { W (' 结论: ' + $fail + ' 项失败') }
W '============================================================'

$text = ($out -join "`r`n") + "`r`n"
Write-Output $text
try { [IO.File]::WriteAllText((Join-Path $root '发布包\端到端冒烟报告.txt'), $text, (New-Object System.Text.UTF8Encoding($true))) } catch { }
