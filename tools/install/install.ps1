# install.ps1 — Restoria 一键安装脚本（在目标机上运行）
#
# 做四件事：检查环境 → 复制程序 → 按需安装 VC++ 运行库（AI 模式用）→ 自检并建桌面快捷方式
# 每一步都有明确输出，全部写进日志，失败会给出手动处理办法。可重复运行。

$ErrorActionPreference = 'Continue'
$script:fail = 0
$script:warn = 0
$log = Join-Path $env:TEMP 'PicTool-install.log'

function Log($msg) {
    Write-Host $msg
    try { Add-Content -LiteralPath $log -Value $msg -Encoding UTF8 } catch { }
}
function Step($msg) { Log (''); Log ('=== ' + $msg + ' ===') }
function OK($msg)   { Log ('  [成功] ' + $msg) }
function Warn($msg) { $script:warn++; Log ('  [注意] ' + $msg) }
function Fail($msg) { $script:fail++; Log ('  [失败] ' + $msg) }

try { Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue } catch { }
try { $Host.UI.RawUI.WindowTitle = 'Restoria 安装程序' } catch { }

$quiet = $false
foreach ($a in $args) { if ($a -eq '-quiet') { $quiet = $true } }
if ($quiet) { $script:warn = 0 }

Log '========================================'
Log '   Restoria — 安装程序'
Log ('   时间: ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Log ('   日志: ' + $log)
Log '========================================'

$src = Split-Path -Parent $MyInvocation.MyCommand.Definition
Log ('   安装源: ' + $src)

# ---------- 1. 环境检查 ----------
Step '1/5 检查系统环境'
$os = [System.Environment]::OSVersion.Version
$osName = $os.Major.ToString() + '.' + $os.Minor.ToString() + ' (Build ' + $os.Build.ToString() + ')'
Log ('   操作系统: ' + $osName + '    ' + [System.Environment]::OSVersion.VersionString)
$is64 = [IntPtr]::Size -eq 8
Log ('   进程位数: ' + $(if ($is64) { '64 位' } else { '32 位' }))
if ($os.Major -lt 6) { Warn '系统版本较老（低于 Vista/2008），可能不受支持' }
elseif ($os.Major -eq 6 -and $os.Minor -eq 1) { OK 'Windows 7，目标平台' }
else { OK '系统版本较新，程序同样兼容' }

# 主运行库：.NET Framework 4.8（硬性要求；Win7 自带的 3.5 不够用）
$net4x = $null
try {
    $k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
    if ($k.Release) { $net4x = $k.Release }
} catch { }
if ($net4x -and $net4x -ge 528040) {
    OK ('.NET Framework 4.8 已就绪（Release ' + $net4x + '）')
}
elseif ($net4x) {
    Warn ('.NET Framework 版本偏低（Release ' + $net4x + '），本程序需要 4.8 或更高')
    Log '         处理方法：运行安装包内附带的 .NET Framework 4.8 离线安装程序'
}
else {
    Fail '系统缺少 .NET Framework 4.8，程序无法运行'
    Log '         处理方法：运行安装包内附带的 .NET Framework 4.8 离线安装程序'
    Log '         注意：Win7 已自带的 .NET 3.5 不够用，必须装 4.8'
}

# ---------- 2. 确定安装位置 ----------
Step '2/5 确定安装位置'
$target = Join-Path $env:USERPROFILE 'Restoria'
Log ('   安装到: ' + $target)

# ---------- 3. 复制文件 ----------
Step '3/5 复制程序文件'
if (-not (Test-Path (Join-Path $src 'Restoria.exe'))) {
    Fail ('安装源里找不到 Restoria.exe（' + $src + '）')
} else {
    try {
        if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
        Copy-Item -LiteralPath (Join-Path $src 'Restoria.exe') -Destination $target -Force
        $cfg = Join-Path $src 'Restoria.exe.config'
        if (Test-Path $cfg) { Copy-Item -LiteralPath $cfg -Destination $target -Force }
        OK '程序主体已复制'
    } catch { Fail ('复制失败：' + $_.Exception.Message) }

    # AI 素材（可选）—— **整棵 ai\ 复制**（含 ai\win7\ 兼容层），不再按系统版本挑文件。
    # 为什么不再挑（2026-09-20 用户 Win11 报"错误码 126"的根因）：
    #   为了让 ORT 1.14.1 能在 Win7 跑，onnxruntime.dll 的**导入表**里 4 个 API 集名被改成了
    #   普通文件名 w7shim-*.dll（见 tools\prepare_ai_dir.py）。改名落进导入表后，这 4 个文件
    #   就成了**所有系统上的硬依赖** —— 旧版"只在 Win8 以下复制垫片"于是让 Win10/11 缺文件、
    #   LoadLibrary 直接 126（找不到模块），而提示还写成"缺 VC++ 运行库"，把排查带偏。
    # 现在包里两套并存，程序自己按"谁能加载用谁"依次试（见 src\Ai\AiFill.cs LoadAnyRuntime）：
    #   ai\onnxruntime.dll       原版（Win8/10/11 用，不依赖垫片）
    #   ai\win7\onnxruntime.dll  补丁版 + 同目录垫片（Win7 用）
    $srcAi = Join-Path $src 'ai'
    $dll = Join-Path $srcAi 'onnxruntime.dll'
    $models = @()
    if (Test-Path $srcAi) { $models = @(Get-ChildItem -LiteralPath $srcAi -Filter '*.onnx' -ErrorAction SilentlyContinue) }
    if ((Test-Path $dll) -and ($models.Count -gt 0)) {
        try {
            $dstAi = Join-Path $target 'ai'
            if (-not (Test-Path $dstAi)) { New-Item -ItemType Directory -Path $dstAi -Force | Out-Null }
            # ⚠️ 必须用 Get-ChildItem 展开：-LiteralPath 不认通配符（会一个文件都不拷且不报错）
            Get-ChildItem -LiteralPath $srcAi | Copy-Item -Destination $dstAi -Recurse -Force
            $mb = [Math]::Round(((Get-Item -LiteralPath (Join-Path $dstAi 'onnxruntime.dll')).Length / 1MB), 1)
            OK ('AI 修复素材已复制（运行时 ' + $mb + 'MB，模型 ' + $models.Count + ' 个）')
            if (Test-Path (Join-Path $dstAi 'win7\onnxruntime.dll')) { OK 'Win7 兼容层已一并复制' }
        } catch { Warn ('AI 素材复制失败：' + $_.Exception.Message) }
    } else {
        Warn '未包含 AI 素材，安装后为三模式版本（普通/增强/混合），功能完全正常'
        Log  '         如需 AI 修复：把 onnxruntime.dll 与 migan.onnx 放进安装目录的 ai 文件夹即可'
    }
    # 文档（包里 AI_SETUP.md 放在 docs\ 子目录，README 在根目录，两处都找一下）
    foreach ($doc in @('使用说明.txt', 'AI_SETUP.md')) {
        $p = Join-Path $src $doc
        if (-not (Test-Path -LiteralPath $p)) { $p = Join-Path $src ('docs\' + $doc) }
        if (Test-Path -LiteralPath $p) { try { Copy-Item -LiteralPath $p -Destination $target -Force } catch { } }
    }
}

# ---------- 3.5 就地核对（不许"只报告成功"，要真查文件在不在） ----------
$exeCheck = Join-Path $target 'Restoria.exe'
if (-not (Test-Path -LiteralPath $exeCheck)) {
    Fail '安装目录里没有 Restoria.exe，复制环节没成功'
} else {
    $sz = [Math]::Round((Get-Item -LiteralPath $exeCheck).Length / 1KB, 1)
    if ($sz -lt 40) { Fail ('程序文件只有 ' + $sz + ' KB，明显不完整') }
    else { OK ('已核对：Restoria.exe ' + $sz + ' KB 就位') }
    if ((Test-Path -LiteralPath (Join-Path $src 'ai\migan.onnx')) -and (-not (Test-Path -LiteralPath (Join-Path $target 'ai\migan.onnx')))) {
        Fail 'AI 模型没有复制到安装目录'
    }
}

# ---------- 4. VC++ 运行库（AI 模式需要） ----------
Step '4/5 检查 VC++ 运行库（AI 模式依赖）'
$vcOk = (Test-Path (Join-Path $env:SystemRoot 'System32\vcruntime140.dll')) -and (Test-Path (Join-Path $env:SystemRoot 'System32\msvcp140.dll'))
$needAi = Test-Path (Join-Path $target 'ai\onnxruntime.dll')
if (-not $needAi) {
    Log '   未安装 AI 素材，跳过（不影响使用）'
} elseif ($vcOk) {
    OK 'VC++ 运行库已安装'
} else {
    $rd = Join-Path $src 'vc_redist\vc_redist.x64.exe'
    if (Test-Path $rd) {
        Log '   系统缺少 VC++ 运行库，尝试自动安装（会弹出权限确认，请点"是"）...'
        try {
            $p = Start-Process -FilePath $rd -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -PassThru -Wait
            if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) { OK 'VC++ 运行库安装完成' }
            else { Warn ('自动安装返回代码 ' + $p.ExitCode + '，如 AI 模式报错请手动运行 vc_redist.x64.exe') }
        } catch { Warn ('自动安装未完成：' + $_.Exception.Message + '（可稍后手动运行 vc_redist.x64.exe）') }
    } else {
        Warn '缺少 VC++ 运行库且安装包内没有安装程序，AI 模式可能无法启动'
        Log  '         处理方法：安装 Microsoft Visual C++ 2015-2022 可再发行组件 (x64)'
    }
}

# ---------- 5. 自检 + 快捷方式 ----------
Step '5/5 自检与快捷方式'
$exe = Join-Path $target 'Restoria.exe'
if (Test-Path $exe) {
    # 先报版本：现场反馈问题时，"装的是哪一版"是第一个要确认的信息
    try {
        $v = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
        Log ('   程序版本: ' + $v)
        if ($v -lt '2.0.0.0') { Warn ('装的还是旧版本 ' + $v + '，请更新发布包') }
    } catch { Warn '读不到程序版本' }

    # 扫描就绪诊断：WIA 组件/服务/设备/TWAIN 一次查清，报告落盘可带回来
    $srep = Join-Path $env:TEMP 'PicTool-scan-diag.txt'
    try { Remove-Item -LiteralPath $srep -ErrorAction SilentlyContinue } catch { }
    Log '   正在做扫描就绪诊断（检查 WIA 组件/服务/设备/TWAIN，约几秒）...'
    try {
        $sp = Start-Process -FilePath $exe -ArgumentList '--scan-diag','--silent' -PassThru
        $null = $sp.WaitForExit(60000)
        if (Test-Path -LiteralPath $srep) {
            $stext = (Get-Content -LiteralPath $srep -Encoding UTF8) -join "`n"
            Get-Content -LiteralPath $srep -Encoding UTF8 | ForEach-Object { Log ('   ' + $_) }
            Log '   ----------------------------------------'
            if ($stext -match 'WIA 设备 : [1-9]') { OK '扫描就绪：本机能看到扫描设备' }
            elseif ($stext -match 'WIA 设备 : 0') { Warn '扫描未就绪：本机 WIA 看不到任何扫描设备（详见上面结论）' }
        } else { Warn '扫描诊断没有生成报告（不拦安装，之后可手动运行 --scan-diag）' }
    } catch { Warn ('扫描诊断失败：' + $_.Exception.Message) }

    try {
        # 自检 = 真正把程序跑起来一次：加载运行时与模型，输出张量规格
        $rep = Join-Path $env:TEMP 'PicTool-ai-check.txt'
        try { Remove-Item -LiteralPath $rep -ErrorAction SilentlyContinue } catch { }
        if ($quiet) {
            Log '   正在自检（加载 AI 运行时与模型，老机器可能需要十几秒，请稍等）...'
            $p = Start-Process -FilePath $exe -ArgumentList '--ai-check-silent' -PassThru
        } else {
            Log '   正在自检（会弹出一个报告窗口，看完点"确定"即可）...'
            $p = Start-Process -FilePath $exe -ArgumentList '--ai-check' -PassThru
        }
        $null = $p.WaitForExit(120000)
        if (-not $p.HasExited) { try { $p.Kill() } catch { } }
        if (Test-Path -LiteralPath $rep) {
            $repText = (Get-Content -LiteralPath $rep -Encoding UTF8) -join "`n"
            Log '   ----------------------------------------'
            Get-Content -LiteralPath $rep -Encoding UTF8 | ForEach-Object { Log ('   ' + $_) }
            Log '   ----------------------------------------'
            if ($repText -match 'AI 可用\s*:\s*是') {
                OK '自检通过：AI 修复可用'
            } elseif (Test-Path -LiteralPath (Join-Path $target 'ai\migan.onnx')) {
                Warn 'AI 素材已装好，但自检没能用上（多半是缺 VC++ 运行库，见上一步）'
            } else {
                OK '自检通过：程序可正常运行（三模式版，AI 素材未包含）'
            }
        } else {
            Fail '自检没有生成报告，程序可能无法启动；请把日志发给维护者'
        }
    } catch { Warn ('自检失败：' + $_.Exception.Message) }

    # 桌面快捷方式
    try {
        $ws = New-Object -ComObject WScript.Shell
        $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Restoria.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $target
        # 悬停提示只保留程序名：一串"— 裁剪 / A4 归档 / …"太啰嗦
        $lnk.Description = ''
        $lnk.Save()
        OK '已在桌面创建快捷方式「Restoria」'

        # 改过名字：把旧版（图片工具箱）的安装目录与快捷方式清掉，免得学生机器上留两份
        try {
            $oldDir = Join-Path $env:USERPROFILE '图片工具箱'
            if (Test-Path -LiteralPath $oldDir) {
                Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction Stop
                OK '已清理旧版安装目录（图片工具箱）'
            }
            $oldLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) '图片工具箱.lnk'
            if (Test-Path -LiteralPath $oldLnk) {
                Remove-Item -LiteralPath $oldLnk -Force -ErrorAction Stop
                OK '已清理旧版桌面快捷方式'
            }
        } catch { Warn ('旧版清理失败（不影响使用）：' + $_.Exception.Message) }
    } catch { Warn ('快捷方式创建失败：' + $_.Exception.Message + '（可手动进入安装目录双击程序）') }
} else {
    Fail '安装目录里没有程序文件，安装未完成'
}

# ---------- 汇总 ----------
Step '安装结果'
$rc = 1
if ($script:fail -eq 0) {
    $rc = 0
    Log ('   安装成功' + $(if ($script:warn -gt 0) { ('（有 ' + $script:warn + ' 条注意，见上面）') } else { '，无异常' }))
    Log ('   程序位置: ' + $exe)
    Log '   使用方法: 双击桌面「Restoria」→ 点【打开文件夹】选照片文件夹'
} else {
    Log ('   安装未完全成功：失败 ' + $script:fail + ' 项，注意 ' + $script:warn + ' 项')
    Log '   请把本窗口内容和下面的日志文件发给老师/维护者：'
    Log ('   ' + $log)
}
Log ''
# 给引导脚本（setup.cmd）用的退出码；-quiet 模式下不等待按键
try { Add-Content -LiteralPath $log -Value ('[退出码] ' + $rc) } catch { }
if (-not $quiet) {
    Log '按回车键关闭本窗口...'
    try { [void](Read-Host) } catch { }
}
exit $rc
