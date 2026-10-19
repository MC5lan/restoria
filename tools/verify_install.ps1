# verify_install.ps1 — 实地核对一键安装的结果（用脚本文件避免命令行编码问题）
$ErrorActionPreference = 'Continue'
$out = New-Object System.Collections.ArrayList
function W([string]$s) { [void]$out.Add($s) }

W ('用户目录      : ' + $env:USERPROFILE)
W ('目录存在      : ' + (Test-Path -LiteralPath $env:USERPROFILE))

$target = Join-Path $env:USERPROFILE 'Restoria'
W ('安装目录      : ' + $target)
W ('安装目录存在  : ' + (Test-Path -LiteralPath $target))

if (Test-Path -LiteralPath $target) {
    W '--- 安装目录内容 ---'
    Get-ChildItem -LiteralPath $target | ForEach-Object {
        if ($_.PSIsContainer) {
            $n = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File | Measure-Object -Property Length -Sum)
            W ('  [DIR ] ' + $_.Name + '  文件 ' + $n.Count + ' 个，' + [Math]::Round($n.Sum / 1MB, 1) + ' MB')
        } else {
            W ('  [FILE] ' + $_.Name + '  ' + $_.Length + ' 字节')
        }
    }
    foreach ($f in @('Restoria.exe', 'PicTool.exe.config', 'ai\onnxruntime.dll', 'ai\migan.onnx')) {
        $p = Join-Path $target $f
        if (Test-Path -LiteralPath $p) {
            W ('  ' + $f.PadRight(22) + ' : OK  ' + (Get-Item -LiteralPath $p).Length + ' 字节')
        } else {
            W ('  ' + $f.PadRight(22) + ' : 缺失')
        }
    }
}

W '--- 桌面快捷方式 ---'
$desks = @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:USERPROFILE 'Desktop'))
foreach ($d in ($desks | Select-Object -Unique)) {
    W ('桌面目录: ' + $d)
    if (Test-Path -LiteralPath $d) {
        Get-ChildItem -LiteralPath $d -Filter '*.lnk' | ForEach-Object {
            $ws = New-Object -ComObject WScript.Shell
            $sc = $ws.CreateShortcut($_.FullName)
            W ('  ' + $_.Name + '  ->  ' + $sc.TargetPath + '   存在=' + (Test-Path -LiteralPath $sc.TargetPath))
        }
    }
}

W '--- 安装日志尾部 ---'
$log = Join-Path $env:TEMP 'PicTool-install.log'
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 12 | ForEach-Object { W ('  ' + $_) } }
else { W '  (没有日志)' }

$out -join "`r`n" | Out-File -FilePath 'D:\lama\_verify.txt' -Encoding UTF8
