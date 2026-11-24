# bump_version.ps1 — 版本号唯一来源同步器
#
# 版本号散落在程序集、清单、安装器、EULA 和手册里，手工替换已多次出错
# （1.5.8 的发行包里 exe 还显示 1.5.7，直到 1.6.0 才修）。
# 本脚本从 src\Core\Version.cs 读出当前版本，把下列文件里的旧版本号
# 全部换成新版本号，并在 CHANGELOG.md 顶部插入一条待填写的发行说明。
#
# 用法：
#   powershell -File tools\bump_version.ps1 1.6.4
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
# 用 $MyInvocation 而不是 $PSScriptRoot：后者在 -File 相对路径调用时可能为空。
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号格式应为 x.y.z，例如 1.6.4' }

$versionFile = Join-Path $root 'src\Core\Version.cs'
$text = [IO.File]::ReadAllText($versionFile, [Text.Encoding]::UTF8)
$m = [regex]::Match($text, 'public const string Version = "([0-9.]+)"')
if (-not $m.Success) { throw '无法从 Version.cs 读出当前版本号' }
$old = $m.Groups[1].Value
if ($old -eq $Version) { throw ('当前已经是 ' + $Version + '，无需重复升版') }

$targets = @(
    'src/Core/Version.cs',
    'assets/app.manifest', 'assets/setup.manifest', 'assets/uninst.manifest',
    'tools/install/AssemblySetup.cs', 'tools/install/AssemblyUninst.cs',
    'tools/install/SetupInfo.cs', 'tools/install/EulaText.cs',
    'docs/使用手册.md'
)
foreach ($rel in $targets) {
    $p = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $p)) { throw ('找不到 ' + $rel) }
    $bytes = [IO.File]::ReadAllBytes($p)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $t = [Text.Encoding]::UTF8.GetString($bytes)
    if ($t.Contains($old)) {
        [IO.File]::WriteAllText($p, ($t.Replace($old, $Version)), (New-Object Text.UTF8Encoding($bom)))
        Write-Output ('  ' + $rel)
    }
}

# CHANGELOG 顶部插入待填写条目
$cl = Join-Path $root 'CHANGELOG.md'
$clText = [IO.File]::ReadAllText($cl, [Text.Encoding]::UTF8)
$stub = '## ' + $Version + ' — ' + (Get-Date -Format 'yyyy-MM-dd') + "`r`n`r`n- （待补充本次改动）`r`n`r`n"
$idx = $clText.IndexOf('## ')
if ($idx -lt 0) { throw 'CHANGELOG.md 里找不到版本小节' }
$clText = $clText.Substring(0, $idx) + $stub + $clText.Substring($idx)
[IO.File]::WriteAllText($cl, $clText, (New-Object Text.UTF8Encoding($true)))

Write-Output ('版本已同步: ' + $old + ' -> ' + $Version)
Write-Output '记得填写 CHANGELOG.md 里新插入的条目。'
