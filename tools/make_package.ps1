# make_package.ps1 — 生成发行包（自研单文件安装程序，不再用 WinRAR SFX）
#
# 产物：
#   发布包\Restoria-setup.exe      ← 单文件安装程序（双击 → 白底解压 → 协议 → 向导）
#   发布包\Restoria-portable.zip   ← 免安装压缩包（解压后双击 setup-boot.exe）
#   发布包\Restoria-install\       ← 上面两个的主体内容（舞台目录，留着方便核对）
#
# 用法：powershell -File tools\make_package.ps1
#       powershell -File tools\make_package.ps1 -SkipSign     # 跳过签名（排查用）

param(
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
Set-Location $root
$pack = Join-Path $root '发布包'
# ---------- 资产命名 ----------
# 唯一出处。README 的下载表、release 正文、发布脚本都引用这两个名字，
# 改动必须四处同步。名字用英文：README 是英文主版本，Release 附件名会出现在
# GitHub 文件列表和用户的下载记录里，中文名在部分系统和浏览器下载框里会乱码。
$AssetInstaller = 'Restoria-setup.exe'
$AssetPortable = 'Restoria-portable.zip'
$StageDir      = 'Restoria-install'

$stage = Join-Path $pack $StageDir
$outExe = Join-Path $pack $AssetInstaller
$zip = Join-Path $pack $AssetPortable

# Log 用 Write-Output（不是 Write-Host）：这样 `... *>&1 | Out-File` 能把进度落盘，
# 出问题时能看到卡在哪一步。Write-Host 只进宿主，落不了盘。
function Log($m) { Write-Output $m }

# 旧产物先挪走再重建：本机 safe-delete 钩子会对某些删除操作失败关闭，
# 所以"删不动就改名"是这里最稳的做法（历史教训，见项目备忘）。
function Retire($path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    try { Remove-Item -LiteralPath $path -Recurse -Force; return } catch { }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bak = $path + '_old-' + $stamp
    try { Rename-Item -LiteralPath $path -NewName (Split-Path -Leaf $bak) } catch { }
    Log ('  （旧产物改名避让：' + (Split-Path -Leaf $bak) + '）')
}

# 旧资产名退休。命名改过一轮，脚本和用户的收藏可能还指向旧文件名；
# 留着两个名字的安装包只会让人装错版本。
foreach ($old in @('Restoria-安装.exe', 'Restoria-安装包.zip', 'Restoria-安装')) {
    Retire (Join-Path $pack $old)
}

# ---------- 1/5 编译 ----------
Log '=== 1/5 编译 ==='
# build.bat 末尾有 pause。脚本模式必须让它跳过，否则构建会永远等一个没人按的键
# （build.bat 里的 PICTOOL_NO_PAUSE 开关）。也别再走 cmd /c —— 用 PowerShell
# 直接调用 .bat 更稳，且不需要给 stdin 喂 EOF。
# 另注：build.bat 自身必须是无 BOM + CRLF 的纯 ASCII，否则 cmd.exe 会把
#       BOM 当成命令的一部分（历史故障：'锘緻echo' 不是内部或外部命令）。
$env:PICTOOL_NO_PAUSE = '1'
& (Join-Path $root 'build.bat')
if ($LASTEXITCODE -ne 0) { throw ('编译失败：build.bat 退出码 ' + $LASTEXITCODE) }
$exe = Join-Path $root 'dist\Restoria.exe'
if (-not (Test-Path $exe)) { throw '编译失败：找不到 dist\Restoria.exe' }
Log ('  主程序: ' + [Math]::Round((Get-Item $exe).Length / 1KB, 1) + ' KB')

Log ''
Log '  编译安装程序与卸载程序…'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\build_setup.ps1')
$boot = Join-Path $root 'dist\setup-boot.exe'
$uninst = Join-Path $root 'dist\uninst.exe'
if (-not (Test-Path $boot)) { throw '编译失败：找不到 dist\setup-boot.exe' }
if (-not (Test-Path $uninst)) { throw '编译失败：找不到 dist\uninst.exe' }

if (-not (Test-Path $pack)) { New-Item -ItemType Directory -Path $pack -Force | Out-Null }

# ---------- 2/5 组装舞台目录 ----------
Log ''
Log '=== 2/5 组装安装目录 ==='
Retire $stage
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'ai') -Force | Out-Null

Copy-Item -LiteralPath $exe -Destination $stage -Force
$cfg = Join-Path $root 'dist\Restoria.exe.config'
if (Test-Path $cfg) { Copy-Item -LiteralPath $cfg -Destination $stage -Force }

# 调试符号：跟着主程序一起装。用户报错时我们只要日志 + 这个 pdb 就能把
# 优化过的堆栈还原成"文件:行"，而不是一串没有行号的地址。
$pdb = Join-Path $root 'dist\Restoria.pdb'
if (Test-Path $pdb) {
    Copy-Item -LiteralPath $pdb -Destination $stage -Force
    Log ('  调试符号: Restoria.pdb (' + [Math]::Round((Get-Item $pdb).Length / 1KB, 1) + ' KB)')
}

# 卸载程序：装进安装目录，控制面板的"卸载"指向它
Copy-Item -LiteralPath $uninst -Destination (Join-Path $stage 'Uninstall.exe') -Force
Log ('  卸载程序: Uninstall.exe (' + [Math]::Round((Get-Item $uninst).Length / 1KB, 1) + ' KB)')

# ============ AI 素材：整棵 dist\ai 递归复制 ============
# 布局由 tools\prepare_ai_dir.py 生成，**别再在这里手工拼文件**。旧版手拼漏掉了
# 4 个 w7shim-*.dll，而补丁版 onnxruntime.dll 的导入表把这 4 个名字写成了硬依赖 ——
# 结果 Win10/11 装完一加载就报"错误码 126"（用户 2026-09-20 的现场）。
# 现在包内两套并存，程序按"谁能加载用谁"依次试：
#   ai\onnxruntime.dll       原版（Win8/10/11 用）
#   ai\migan.onnx            模型
#   ai\ocr\...               镜像检测素材
#   ai\win7\onnxruntime.dll  补丁版 + 同目录 4 个 w7shim 垫片 + 其余 API 集垫片（Win7 用）
$aiSrc = Join-Path $root 'dist\ai'
$aiOk = (Test-Path (Join-Path $aiSrc 'onnxruntime.dll')) -and (Test-Path (Join-Path $aiSrc 'migan.onnx'))
if ($aiOk) {
    # ⚠️ 必须用 Get-ChildItem 展开：-LiteralPath 不认通配符（写成 (Join-Path $aiSrc '*')
    #    会一个文件都不拷，且不报错 —— 2026-09-20 实测栽过一次）
    Get-ChildItem -LiteralPath $aiSrc | Copy-Item -Destination (Join-Path $stage 'ai') -Recurse -Force
    $aiFiles = @(Get-ChildItem -LiteralPath (Join-Path $stage 'ai') -Recurse -File)
    Log ('  AI 素材: 运行时 + 模型 + OCR + Win7 兼容层 已包含（' + $aiFiles.Count + ' 个文件）')
    if (Test-Path (Join-Path $stage 'ai\win7\onnxruntime.dll')) { Log '  Win7 兼容层: ai\win7\ 已包含（含 4 个 w7shim 垫片）' }
    else { Log '  !! 缺 ai\win7\ 兼容层 —— Win7 上 AI 会不可用，请重跑 tools\prepare_ai_dir.py dist\ai' }
    if (-not (Test-Path (Join-Path $stage 'ai\ocr\keys.txt'))) { Log '  OCR 素材: 不齐全（镜像检测将自动跳过）' }
} else { Log '  AI 素材: 未包含（结果将是三模式版本）' }

# VC++ 运行库
$vc = Join-Path $root 'vendor\vc_redist.x64.exe'
if (Test-Path $vc) {
    New-Item -ItemType Directory -Path (Join-Path $stage 'vc_redist') -Force | Out-Null
    Copy-Item -LiteralPath $vc -Destination (Join-Path $stage 'vc_redist') -Force
    Log ('  VC++ 运行库: ' + [Math]::Round((Get-Item $vc).Length / 1MB, 1) + ' MB')
} else { Log '  VC++ 运行库: 未找到（AI 模式在缺运行库的机器上可能无法启动）' }

# .NET Framework 4.8 离线安装包（Win7 SP1 目标机必需；微软官方 fwlink 2088631，x86/x64 合并版）
$ndp = Join-Path $root 'vendor\ndp48\ndp48-x86-x64-allos-enu.exe'
if (Test-Path $ndp) {
    Copy-Item -LiteralPath $ndp -Destination $stage -Force
    Log ('  .NET 4.8 离线安装包: ' + [Math]::Round((Get-Item $ndp).Length / 1MB, 1) + ' MB')
} else { Log '  .NET 4.8 离线安装包: 未包含（目标机需自行安装 4.8）' }

# 文档
foreach ($f in @('README.md', 'THIRD_PARTY_NOTICES')) {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination $stage -Force }
}
$docs = Join-Path $root 'docs'
if (Test-Path $docs) {
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -Force | Out-Null
    foreach ($d in @('AI_SETUP.md', '用户使用手册.md', '更新日志.md', '架构.md')) {
        $p = Join-Path $docs $d
        if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $stage 'docs') -Force }
    }
}
foreach ($a in @('icon-preview.png')) {
    $src = Join-Path $root ('assets\' + $a)
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $stage 'docs') -Force }
}
if (Test-Path -LiteralPath (Join-Path $root 'assets\icon-preview.png')) { Copy-Item -LiteralPath (Join-Path $root 'assets\icon-preview.png') -Destination $stage -Force }

# 给老师用的极简说明（UTF-8 带 BOM，Win7 记事本能正确显示）
$readme = @'
Restoria —— 安装与使用（老师/管理员版）
==========================================

一、怎么装（在目标机上）
  方式 A：通用安装程序（推荐，Inno Setup 标准向导）
   1. 把「Restoria-setup.exe」拷到目标机，双击。
   2. 弹出系统权限确认（UAC），点「是」—— 装到 Program Files 需要管理员权限。
   3. 选择语言（简体中文 / English），点「下一步」。
   4. 阅读许可协议，选「我接受」，点「下一步」。
   5. 安装位置默认  C:\Program Files\Restoria，一般不用改。
   6. 可选择是否创建桌面快捷方式，然后点「安装」，完成后点「结束」。
   7. 从开始菜单（或桌面快捷方式）启动 Restoria。

  方式 B：免安装压缩包（没有管理员权限时）
   1. 解压「Restoria-portable.zip」到任意文件夹。
   2. 双击 setup-boot.exe，按提示装到当前用户目录（不需要管理员权限）。
   3. Win7 若没装 .NET Framework 4.8，先双击 ndp48-x86-x64-allos-enu.exe
      装好并重启一次，再运行主程序。

二、怎么卸
  方法一：控制面板 → 程序和功能 → Restoria → 卸载
  方法二：开始菜单 → Restoria → 卸载 Restoria
  方法三：安装目录里双击  unins000.exe
  注意：您用本软件处理过的图片不会被删除。

三、装了什么
  Restoria.exe      主程序（需要 .NET Framework 4.8）
  Restoria.exe.config       运行时配置
  Restoria.pdb              调试符号（崩溃报告可定位到行号）
  unins000.exe              Inno Setup 卸载程序（控制面板入口指向它）
  ai\onnxruntime.dll          AI 修复运行时（Win8/10/11 用这一份）
  ai\win7\                    Win7 专用兼容层（Win10/11 不加载它，可删）
  ai\migan.onnx               AI 修复模型（约 28MB）
  ai\ocr\                     OCR 素材（镜像检测用，可选）
  ndp48-x86-x64-allos-enu.exe .NET 4.8 离线安装包（系统没有 4.8 时双击它先装）
  vc_redist\                  VC++ 运行库安装包（仅 AI 模式需要）
  docs\用户使用手册.md         完整用户手册（界面 / 功能 / 快捷键 / FAQ）
  docs\AI_SETUP.md            AI 模式的详细说明与故障排查
  THIRD_PARTY_NOTICES          第三方组件许可证全文

四、给学生的三句话
  1. 双击桌面「Restoria」→ 点【打开文件夹】选照片文件夹。
  2. 左边点一张照片，再点最左侧导航栏选功能：涂掉水印/杂物 → 【开始修复】；
     拍歪了 → 【方向】自动纠偏；颜色不好看 → 【调色】自动增强；公章褪色 → 【印章】页。
     修复方式在右侧「修复方式」里选（普通/增强/混合/纸面/AI）；顶栏可切换工具与缩放。
  3. 点顶栏【保存】，处理好的图片在 原文件夹\_已处理\ 里（原图不动）。
     详细说明见安装目录 docs\用户使用手册.md。

五、如果没有 AI（三模式版）
  程序仍然完全可用（普通/增强/混合三种修复方式）。要启用 AI：
  把 onnxruntime.dll 和 migan.onnx 放进安装目录的 ai\ 文件夹，重启程序即可。

六、常见问题
  * 安装程序双击没反应：Inno Setup 生成的安装包是原生程序，不依赖 .NET；
    若确实打不开，检查是否被安全软件拦截，或改用方式 B 的免安装包。
  * 装完启动主程序提示"缺少 .NET Framework"：先双击安装目录里的
    ndp48-x86-x64-allos-enu.exe 装好 4.8（装完重启一次）。
  * AI 模式报错/不可用：确认安装目录 ai\ 里有 dll 和 onnx；若提示缺 DLL，
    运行 vc_redist\vc_redist.x64.exe 安装 VC++ 运行库。
  * 安装窗口一闪而过或提示失败：把 %TEMP%\PicTool-install.log 发给老师（日志文件路径
    也显示在安装窗口的日志区上方）。
  * 杀毒软件拦截：本程序未购买商业代码签名证书，个别安全软件可能提示"未知发布者"。
    选择"仍要运行/允许"即可；学校可把本程序加入白名单。
'@
[IO.File]::WriteAllText((Join-Path $stage '使用说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))
Log '  使用说明.txt 已生成'

# 打包进载荷时跳过的东西（旧链路脚本 + ZIP 自己的入口），见 tools\pack_payload.ps1
Copy-Item -LiteralPath $boot -Destination (Join-Path $stage 'setup-boot.exe') -Force
Log ('  安装程序: setup-boot.exe (' + [Math]::Round((Get-Item $boot).Length / 1KB, 1) + ' KB，ZIP 版入口)')

# ---------- 2.5 给"要装到用户机器上的两个 exe"签名 ----------
# 顺序很关键：**必须在这里签**，也就是在打包之前。一旦 pack_payload 跑完，
# 这两个 exe 就被封进载荷里了，之后再签也改不了包里的那一份 —— 用户装完看到
# 的仍然是"未签名"。单文件安装程序自己在最后一步签（它得等载荷追加完）。
if (-not $SkipSign) {
    Log ''
    Log '  签名主程序与卸载程序（打包之前）…'
    $signScript = Join-Path $root 'tools\sign_setup.ps1'
    if (Test-Path $signScript) {
        foreach ($t in @((Join-Path $stage 'Restoria.exe'), (Join-Path $stage 'Uninstall.exe'))) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $signScript -File $t
        }
    } else {
        Log '  （未找到 tools\sign_setup.ps1，跳过签名；正式发布请自备代码签名证书）'
    }
} else {
    Log '  （已指定 -SkipSign：主程序与卸载程序也不签名）'
}

# ---------- 3/5 打 ZIP ----------
Log ''
Log '=== 3/5 生成 ZIP ==='
Retire $zip
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Log ('  ' + $zip + '  (' + [Math]::Round((Get-Item $zip).Length / 1MB, 1) + ' MB)')

# ---------- 4/5 生成通用安装包（Inno Setup 6） ----------
Log ''
Log '=== 4/5 生成通用安装包（Inno Setup 6）==='
# 旧的自研单文件安装器源码保留在 tools\install\ 作参考，但发行不再使用它。
# 换成 Inno Setup 后：标准向导、标准卸载入口、Win7 SP1+ 兼容，也不需要再维护
# 那套 PTBOOT01/PTPK0001 私有载荷索引（签名后索引会被证书表推走的老坑一并消失）。
try { Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $outExe } | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
Start-Sleep -Milliseconds 400
Retire $outExe

$iss = Join-Path $root 'tools\install\Restoria.iss'
if (-not (Test-Path $iss)) { throw ('找不到 Inno Setup 脚本：' + $iss) }
$iscc = $env:INNO_SETUP_ISCC
if (-not $iscc) {
    $pf86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $pf = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    foreach ($cand in @((Join-Path $pf86 'Inno Setup 6\ISCC.exe'), (Join-Path $pf 'Inno Setup 6\ISCC.exe'))) {
        if ($cand -and (Test-Path $cand)) { $iscc = $cand; break }
    }
}
if (-not $iscc -or -not (Test-Path $iscc)) {
    throw '找不到 Inno Setup 6 编译器 ISCC.exe。请安装 Inno Setup 6，或设置环境变量 INNO_SETUP_ISCC 指向 ISCC.exe。'
}
$vline = Select-String -Path (Join-Path $root 'src\Core\Version.cs') -Pattern 'public const string Version = "([^"]+)"' | Select-Object -First 1
if (-not $vline) { throw '无法从 src\Core\Version.cs 读取版本号' }
$version = $vline.Matches[0].Groups[1].Value
Log ('  编译器: ' + $iscc)
Log ('  版本  : ' + $version)
& $iscc ("/DMyAppVersion=$version") $iss | ForEach-Object { Log ('  ' + $_) }
if ($LASTEXITCODE -ne 0) { throw ('Inno Setup 编译失败，退出码 ' + $LASTEXITCODE) }
if (-not (Test-Path $outExe)) { throw '打包失败：Inno Setup 没有生成 Restoria-setup.exe' }

# 可选代码签名（有 sign_setup.ps1 且未指定 -SkipSign 时）
if (-not $SkipSign) {
    $signScript = Join-Path $root 'tools\sign_setup.ps1'
    if (Test-Path $signScript) {
        Log ''
        Log '  代码签名…'
        & powershell -NoProfile -ExecutionPolicy Bypass -File $signScript -File $outExe
    } else {
        Log '  （未找到 tools\sign_setup.ps1，跳过签名；正式发布请自备代码签名证书）'
    }
} else {
    Log '  （已指定 -SkipSign，跳过签名）'
}

# ---------- 签名后复检：安装包版本信息 ----------
# Inno Setup 自己管理载荷，不再需要旧的 PTBOOT01/PTPK0001 私有索引复检。
Log ''
Log '=== 签名后复检 ==='
$viPkg = (Get-Item -LiteralPath $outExe).VersionInfo
if ($viPkg.FileVersion -notlike ($version + '*')) {
    throw ('安装包版本信息不对：' + $viPkg.FileVersion + '，期望 ' + $version + '*')
}
Log ('  [通过] 安装包版本 ' + $viPkg.FileVersion + '  产品名 ' + $viPkg.ProductName)

# 旧版 WinRAR SFX 产物（187MB 的「一键安装.exe」）退休：留着只会让人装错版本。
# 用改名而不是删除 —— 本机 safe-delete 钩子会拦删除，而且万一还想回头看一眼。
Retire (Join-Path $pack 'Restoria-一键安装.exe')

# ---------- 5/5 汇总 ----------
Log ''
Log '=== 5/5 完成 ==='
Log ('  单文件安装程序: ' + $outExe + '  (' + [Math]::Round((Get-Item $outExe).Length / 1MB, 1) + ' MB)')
Log ('  免安装压缩包  : ' + $zip + '  (' + [Math]::Round((Get-Item $zip).Length / 1MB, 1) + ' MB)')
Log ('  安装目录      : ' + $stage)
Log ''
Log '  安装目录内容：'
Get-ChildItem -LiteralPath $stage | ForEach-Object {
    if ($_.PSIsContainer) {
        $sz = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum
        Log ('    ' + $_.Name + '\  (' + [Math]::Round($sz / 1MB, 1) + ' MB)')
    } else {
        Log ('    ' + $_.Name + '  (' + [Math]::Round($_.Length / 1KB, 1) + ' KB)')
    }
}
