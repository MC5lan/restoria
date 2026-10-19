# verify_release.ps1 - 发行版客观验收：一条命令跑完，逐项给 PASS / FAIL。
#
# 用法：
#   powershell -File tools\verify_release.ps1                 # 核对默认安装位置 %ProgramFiles%\Restoria
#   powershell -File tools\verify_release.ps1 -Dir D:\x\inst  # 核对指定目录（自动化测试用）
#   powershell -File tools\verify_release.ps1 -RunSelftest     # 顺带跑主程序 --selftest
#
# 输出：屏幕逐行打印，同时落盘 发布包\验收报告.txt
# 设计原则：只读。不修改、不删除任何东西，可反复跑。

param(
    [string]$Dir = '',
    [switch]$RunSelftest
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$out = New-Object System.Collections.ArrayList
$fail = 0

function W([string]$s) { [void]$out.Add($s) }
function Chk([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { W ('  [PASS] ' + $name.PadRight(32) + $detail) }
    else { W ('  [FAIL] ' + $name.PadRight(32) + $detail); $script:fail++ }
}

# 产品常量（与 tools\install\SetupInfo.cs 保持一致）
$AppName = 'Restoria'
$AppId = 'PicToolArchiveSuite'
$MainExe = 'Restoria.exe'
$UninstExe = 'Uninstall.exe'
$Marker = 'archivesuite.install'
$StartMenuFolder = 'Restoria'
$DesktopLnk = 'Restoria.lnk'
$UninstLnk = 'Uninstall Restoria.lnk'

# 读 .lnk 的目标：优先 COM；COM 被限制时退化为在字节流里找绝对路径字符串。
function LnkTarget([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return '(不存在)' }
    try {
        $ws = New-Object -ComObject WScript.Shell
        return $ws.CreateShortcut($p).TargetPath
    } catch { }
    try {
        $b = [IO.File]::ReadAllBytes($p)
        $txt = [Text.Encoding]::Unicode.GetString($b) + '|' + [Text.Encoding]::ASCII.GetString($b)
        $m = [regex]::Matches($txt, '[A-Za-z]:\\[^\x00\"<>|]{2,200}\.exe')
        if ($m.Count -gt 0) { return $m[$m.Count - 1].Value }
    } catch { }
    return '(无法解析，但文件存在)'
}

W '============================================================'
W (' Restoria 1.5.6 发行验收    ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
W (' 机器: ' + $env:COMPUTERNAME + '   用户: ' + $env:USERNAME)
W '============================================================'

# ---------- 1. 安装包本体 ----------
W ''
W '[1] 安装包'
$pkg = Join-Path $root '发布包\Restoria-安装.exe'
$pkgOk = Test-Path -LiteralPath $pkg
Chk '安装包存在' $pkgOk $pkg
if ($pkgOk) {
    $mb = [Math]::Round((Get-Item -LiteralPath $pkg).Length / 1MB, 1)
    Chk '体积合理(单文件,非WinRAR外壳)' ($mb -gt 50 -and $mb -lt 400) ('体积 ' + $mb + ' MB')
    # 载荷尾部索引 + 容器头：不能只看"最后 32 字节"——签名会在其后追加证书表。
    # 所以从末尾往回搜 PTBOOT01，再顺着 offset 去读容器头 PTPK0001 与条目数。
    try {
        $fs = [IO.File]::OpenRead($pkg)
        $scanLen = [int][Math]::Min(262144, $fs.Length)
        $buf = New-Object byte[] $scanLen
        $fs.Seek(-$scanLen, 'End') | Out-Null
        $fs.Read($buf, 0, $scanLen) | Out-Null
        $magic = [Text.Encoding]::ASCII.GetBytes('PTBOOT01')
        $pOff = -1
        for ($i = $scanLen - 32; $i -ge 0; $i--) {
            $hit = $true
            for ($k = 0; $k -lt 8; $k++) { if ($buf[$i + $k] -ne $magic[$k]) { $hit = $false; break } }
            if ($hit) { $pOff = $i; break }
        }
        $okFooter = ($pOff -ge 0)
        Chk '载荷尾部索引 PTBOOT01' $okFooter ('距文件末尾 ' + ($scanLen - $pOff) + ' 字节')
        if ($okFooter) {
            $dataOff = [BitConverter]::ToInt64($buf, $pOff + 8)
            $dataLen = [BitConverter]::ToInt64($buf, $pOff + 16)
            W ('        payloadOffset=' + $dataOff + '   payloadLength=' + $dataLen)
            $fs.Seek($dataOff, 'Begin') | Out-Null
            $head = New-Object byte[] 12
            $fs.Read($head, 0, 12) | Out-Null
            $pm = [Text.Encoding]::ASCII.GetString($head, 0, 8)
            $cnt = [BitConverter]::ToInt32($head, 8)
            Chk '载荷容器头 PTPK0001' ($pm -eq 'PTPK0001') ('magic=' + $pm + '   条目数=' + $cnt)
            $stageDir = Join-Path $root '发布包\Restoria-安装'
            if (Test-Path -LiteralPath $stageDir) {
                # 载荷会跳过几个文件（pack_payload.ps1 的 $skip：旧链路脚本 + setup-boot.exe 自己），
                # 所以条目数允许比目录文件数少 1。
                $files = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File)
                $ok = ($cnt -ge ($files.Count - 1) -and $cnt -le $files.Count)
                Chk '条目数与安装目录文件数匹配' $ok ($cnt.ToString() + ' 条 vs 目录 ' + $files.Count + ' 个文件')
            }
        }
        $fs.Close()
    } catch { Chk '载荷尾部索引 PTBOOT01' $false ('读取失败: ' + $_.Exception.Message) }

    $sig = Get-AuthenticodeSignature -LiteralPath $pkg
    Chk '代码签名 Valid' ($sig.Status -eq 'Valid') ([string]$sig.Status + '  ' + [string]$sig.StatusMessage)
    $vi = (Get-Item -LiteralPath $pkg).VersionInfo
    Chk '文件属性-产品名' ($vi.ProductName -like '*安装程序*') ('ProductName=' + $vi.ProductName)
    # 版本从 SetupInfo.cs 动态读（别再写死）
    $expVer = '0.0.0'
    try {
        $m = (Select-String -LiteralPath (Join-Path $PSScriptRoot 'install\SetupInfo.cs') -Pattern 'Version\s*=\s*"([0-9.]+)"' | Select-Object -First 1)
        if ($m) { $expVer = $m.Matches[0].Groups[1].Value }
    } catch { }
    Chk '文件属性-版本' ($vi.FileVersion -like ($expVer + '*')) ('FileVersion=' + $vi.FileVersion + '，期望 ' + $expVer + '*')
}

# ---------- 2. 安装目录 ----------
W ''
W '[2] 安装目录'
if ([string]::IsNullOrEmpty($Dir)) {
    $pf = [Environment]::GetFolderPath('ProgramFiles')
    if ([string]::IsNullOrEmpty($pf)) { $pf = [Environment]::GetFolderPath('ProgramFilesX86') }
    $Dir = Join-Path $pf $AppName
}
W ('      ' + $Dir)
$dirOk = Test-Path -LiteralPath $Dir
Chk '目录存在' $dirOk $Dir
if ($dirOk) {
    foreach ($f in @($MainExe, $UninstExe, $Marker, 'ai\onnxruntime.dll', 'ai\migan.onnx', 'ai\win7\onnxruntime.dll')) {
        $p = Join-Path $Dir $f
        $exists = Test-Path -LiteralPath $p
        $detail = '缺失'
        if ($exists) { $detail = [string](Get-Item -LiteralPath $p).Length + ' 字节' }
        Chk ('  ' + $f) $exists $detail
    }
    $viExe = (Get-Item -LiteralPath (Join-Path $Dir $MainExe)).VersionInfo
    Chk '主程序-产品名' ($viExe.ProductName -eq $AppName) ('ProductName=' + $viExe.ProductName)
    Chk '主程序-公司' (-not [string]::IsNullOrEmpty($viExe.CompanyName)) ('CompanyName=' + $viExe.CompanyName)
    Chk '主程序-版本' ($viExe.FileVersion -like ($expVer + '*')) ('FileVersion=' + $viExe.FileVersion + '，期望 ' + $expVer + '*')
    $sigExe = Get-AuthenticodeSignature -LiteralPath (Join-Path $Dir $MainExe)
    W ('      主程序签名: ' + [string]$sigExe.Status)
    $total = (Get-ChildItem -LiteralPath $Dir -Recurse -File | Measure-Object -Property Length -Sum)
    W ('      安装体积: ' + $total.Count + ' 个文件, ' + [Math]::Round($total.Sum / 1MB, 1) + ' MB')
}

# ---------- 3. 快捷方式 ----------
W ''
W '[3] 快捷方式'
$commonDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$commonStart = [Environment]::GetFolderPath('CommonStartMenu')
$userDesktop = [Environment]::GetFolderPath('Desktop')

$l1 = Join-Path $commonDesktop $DesktopLnk
Chk '所有用户桌面-主程序' (Test-Path -LiteralPath $l1) ((LnkTarget $l1))
$l2 = Join-Path $userDesktop $DesktopLnk
if ($l2 -ne $l1) { W ('      当前用户桌面: ' + (Test-Path -LiteralPath $l2) + '  ' + (LnkTarget $l2)) }

# 开始菜单的程序文件夹是 CommonStartMenu\Programs，装出来的文件夹在 Programs 里面
$smFolder = Join-Path (Join-Path $commonStart 'Programs') $StartMenuFolder
Chk '所有用户开始菜单-文件夹' (Test-Path -LiteralPath $smFolder) $smFolder
if (Test-Path -LiteralPath $smFolder) {
    Get-ChildItem -LiteralPath $smFolder -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
        W ('        ' + $_.Name + '  ->  ' + (LnkTarget $_.FullName))
    }
    Chk '开始菜单-主程序' (Test-Path -LiteralPath (Join-Path $smFolder $DesktopLnk)) $DesktopLnk
    Chk '开始菜单-卸载' (Test-Path -LiteralPath (Join-Path $smFolder $UninstLnk)) $UninstLnk
}

# ---------- 4. 控制面板卸载入口 ----------
W ''
W '[4] 控制面板「程序和功能」'
$found = $false
foreach ($view in @('Registry64', 'Registry32')) {
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
        $k = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $AppId)
        if ($k -ne $null) {
            $found = $true
            W ('      [' + $view + ']')
            foreach ($n in @('DisplayName', 'DisplayVersion', 'Publisher', 'InstallLocation', 'DisplayIcon', 'UninstallString', 'QuietUninstallString', 'EstimatedSize', 'NoModify', 'NoRepair')) {
                W ('        ' + $n.PadRight(20) + ' = ' + [string]$k.GetValue($n))
            }
            $us = [string]$k.GetValue('UninstallString')
            Chk ('  ' + $view + ' 卸载串指向Uninstall.exe') ($us -like ('*' + $UninstExe + '*')) $us
            $k.Close()
        } else {
            W ('      [' + $view + '] 无此键')
        }
        $base.Close()
    } catch {
        W ('      [' + $view + '] 读取异常: ' + $_.Exception.Message)
    }
}
Chk '存在卸载登记' $found ('HKLM\...\Uninstall\' + $AppId)

# ---------- 5. 主程序自检 ----------
if ($RunSelftest -and $dirOk) {
    W ''
    W '[5] 主程序自检 (--selftest)'
    $exe = Join-Path $Dir $MainExe
    $t = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru -Wait -WindowStyle Hidden
    Chk '自检退出码为 0' ($t.ExitCode -eq 0) ('ExitCode=' + $t.ExitCode)
}

# ---------- 汇总 ----------
W ''
W '============================================================'
if ($fail -eq 0) { W ' 结论: 全部通过 (0 项失败)' } else { W (' 结论: ' + $fail + ' 项失败，请看上面 [FAIL] 行') }
W '============================================================'

$text = ($out -join "`r`n") + "`r`n"
Write-Output $text
$rep = Join-Path $root '发布包\验收报告.txt'
try { [IO.File]::WriteAllText($rep, $text, (New-Object System.Text.UTF8Encoding($true))) } catch { }
