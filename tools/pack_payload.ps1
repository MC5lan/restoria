# pack_payload.ps1 — 把安装源目录打进"单文件安装程序"的尾部载荷。
#
# 产物结构（见 tools\install\Payload.cs 的注释）：
#   [setup-boot.exe 本体][PTPK0001 容器][32 字节尾部索引 PTBOOT01]
# 双击产物 = 白底进度窗解压 → 安装向导，全程没有命令行窗口。
#
# 用法：
#   powershell -File tools\pack_payload.ps1 -Stage <安装源目录> -BootExe dist\setup-boot.exe -OutExe 发布包\xxx.exe
param(
    [Parameter(Mandatory=$true)][string]$Stage,
    [Parameter(Mandatory=$true)][string]$BootExe,
    [Parameter(Mandatory=$true)][string]$OutExe,
    [long]$CompressUnder = 48MB
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
Set-Location $root

function Log($m) { Write-Output $m }

if (-not (Test-Path -LiteralPath $BootExe)) { throw ('找不到引导器: ' + $BootExe) }
if (-not (Test-Path -LiteralPath $Stage))   { throw ('找不到安装源目录: ' + $Stage) }

# 这些不进载荷（载荷里只放"要被安装到目标机的东西"）：
#   - 旧链路遗留脚本（保留在仓库做参考，不再发行）
#   - setup-boot.exe：它是引导器**本体**，已经作为产物文件头存在，塞进载荷等于自己装自己
$skip = @('install.ps1', 'setup.cmd', '安装.bat', 'setup-gui.exe', 'setup-boot.exe')

$files = @(Get-ChildItem -LiteralPath $Stage -Recurse -File |
           Where-Object { $skip -notcontains $_.Name } |
           Sort-Object FullName)
if ($files.Count -eq 0) { throw '安装源目录里没有文件' }

$stageFull = (Resolve-Path -LiteralPath $Stage).Path.TrimEnd('\')
$outFull = $OutExe
if (-not [IO.Path]::IsPathRooted($outFull)) { $outFull = Join-Path (Get-Location).Path $outFull }
$outFull = [IO.Path]::GetFullPath($outFull)
$outDir = Split-Path -Parent $outFull
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

Log ('  引导器: ' + $BootExe)
Log ('  安装源: ' + $stageFull + '（' + $files.Count + ' 个文件）')
Log ('  产物  : ' + $outFull)

# ---------- 1. 先落引导器本体 ----------
Copy-Item -LiteralPath $BootExe -Destination $outFull -Force

# ---------- 2. 追加容器 ----------
$magic = [Text.Encoding]::ASCII.GetBytes('PTPK0001')
$fs = [IO.File]::Open($outFull, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
$bw = New-Object IO.BinaryWriter($fs)
$payloadOffset = $fs.Position
$stored = 0
$deflated = 0
$rawTotal = 0
$buf = New-Object byte[] (256 * 1024)
try {
    $bw.Write($magic)
    $bw.Write([int]$files.Count)
    $i = 0
    foreach ($f in $files) {
        $i++
        $rel = $f.FullName.Substring($stageFull.Length + 1)
        $nameBytes = [Text.Encoding]::UTF8.GetBytes($rel)
        $bw.Write([int]$nameBytes.Length)
        $bw.Write($nameBytes)
        $raw = $f.Length
        $rawTotal += $raw

        $method = 0
        $data = $null
        if ($raw -gt 0 -and $raw -le $CompressUnder) {
            # 试压：压不小就原样存（ndp48/vc_redist 这类本身已压缩，Deflate 几乎没收益）
            $ms = New-Object IO.MemoryStream
            $in = [IO.File]::OpenRead($f.FullName)
            $dz = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionLevel]::Optimal, $true)
            try {
                $n = 0
                while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $dz.Write($buf, 0, $n) }
            } finally {
                $dz.Dispose(); $in.Dispose()
            }
            if ($ms.Length -lt $raw) { $method = 1; $data = $ms.ToArray(); $deflated++ } else { $ms.Dispose() }
        }

        $bw.Write([long]$raw)
        $bw.Write([int]$method)
        if ($method -eq 1) {
            $bw.Write([long]$data.Length)
            $bw.Write($data)
            $stored += $data.Length
        } else {
            $bw.Write([long]$raw)
            $in = [IO.File]::OpenRead($f.FullName)
            try {
                $n = 0
                while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $bw.Write($buf, 0, $n) }
            } finally { $in.Dispose() }
            $stored += $raw
        }
        if (($i % 10) -eq 0 -or $i -eq $files.Count) {
            Log ('    打包中 ' + $i + '/' + $files.Count + ' …')
        }
    }

    # ---------- 3. 尾部索引 ----------
    $payloadLength = $fs.Position - $payloadOffset
    $bw.Write([Text.Encoding]::ASCII.GetBytes('PTBOOT01'))
    $bw.Write([long]$payloadOffset)
    $bw.Write([long]$payloadLength)
    $bw.Write([int]1)
    $bw.Write([int]0)
} finally {
    $bw.Dispose(); $fs.Dispose()
}

$outLen = (Get-Item -LiteralPath $outFull).Length
Log ''
Log ('  载荷条目: ' + $files.Count + ' 个（Deflate ' + $deflated + ' 个 / 原样 ' + ($files.Count - $deflated) + ' 个）')
Log ('  原始体积: ' + [Math]::Round($rawTotal / 1MB, 1) + ' MB → 载荷 ' + [Math]::Round($payloadLength / 1MB, 1) + ' MB')
Log ('  单文件产物: ' + [Math]::Round($outLen / 1MB, 1) + ' MB')

# ---------- 4. 自校验：调用产物自己的清单功能（走的正是安装时的读取代码） ----------
$listFile = Join-Path $env:TEMP 'pictool-payload-list.txt'
if (Test-Path -LiteralPath $listFile) { Remove-Item -LiteralPath $listFile -Force }
$skipCheck = $false
$p = $null
try {
    $p = Start-Process -FilePath $outFull -ArgumentList ('--list-payload=' + $listFile) -Wait -PassThru
} catch {
    $skipCheck = $true
    Log '  [WARN] no-elevation session: payload self-check skipped (installer needs admin; verify_release.ps1 covers it)'
}
if ($skipCheck) {
    Log '  [WARN] payload self-check skipped'
} elseif (-not (Test-Path -LiteralPath $listFile)) { throw '自校验失败：产物没能读出自己的载荷清单' }
if (-not $skipCheck) {
$lines = Get-Content -LiteralPath $listFile -Encoding UTF8
$entries = 0
$totalRaw = 0
foreach ($ln in $lines) {
    if ($ln -like 'entries=*') { $entries = [int]($ln.Substring(8)) }
    if ($ln -like 'total-raw=*') { $totalRaw = [long]($ln.Substring(10)) }
}
if ($entries -ne $files.Count) { throw ('自校验失败：产物里 ' + $entries + ' 个条目，源目录 ' + $files.Count + ' 个') }
if ($totalRaw -ne $rawTotal) { throw ('自校验失败：原始总字节不一致（' + $totalRaw + ' vs ' + $rawTotal + '）') }
Log ''
Log ('  [校验通过] 产物自报条目 ' + $entries + ' 个 / 原始 ' + [Math]::Round($totalRaw / 1MB, 1) + ' MB')
Log ('  清单: ' + $listFile)
}
