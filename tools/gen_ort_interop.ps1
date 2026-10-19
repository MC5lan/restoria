# gen_ort_interop.ps1 — 从 onnxruntime_c_api.h 自动生成 C# 互操作结构
#
# 为什么需要生成而不是手写：OrtApi 是一个"函数指针数组"（按声明顺序排列的巨型结构体），
# 顺序错一个字段就会调用到错误的函数指针 → 崩溃。手写 400 个字段不可靠，
# 因此直接解析官方头文件，按顺序生成 C# 结构体字段，保证与 DLL 完全一致。
#
# 用法（build.bat 会自动调用）：
#   powershell -File tools\gen_ort_interop.ps1 -Header <路径>\onnxruntime_c_api.h -Out src\OrtApi.Generated.cs
param(
  [Parameter(Mandatory=$true)][string]$Header,
  [Parameter(Mandatory=$true)][string]$Out
)

if (-not (Test-Path $Header)) { Write-Host "找不到头文件: $Header" -ForegroundColor Red; exit 1 }

$lines = Get-Content -LiteralPath $Header -Encoding UTF8

# 1) API 版本号
$apiVersion = 0
foreach ($l in $lines) {
  if ($l -match '^\s*#define\s+ORT_API_VERSION\s+(\d+)') { $apiVersion = [int]$Matches[1]; break }
}
if ($apiVersion -le 0) { Write-Host "未能从头部文件中解析出 ORT_API_VERSION" -ForegroundColor Red; exit 1 }

# 2) 定位 OrtApi 结构体范围（从 "struct OrtApi {" 到它的闭合大括号）
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '^\s*struct\s+OrtApi\s*\{') { $start = $i; break }
}
if ($start -lt 0) { Write-Host "未找到 struct OrtApi" -ForegroundColor Red; exit 1 }
$end = -1
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '^\s*\};') { $end = $i; break }
}
if ($end -lt 0) { Write-Host "未找到 struct OrtApi 的结束位置" -ForegroundColor Red; exit 1 }

# 3) 按顺序提取字段名：三种宏形式
#    ORT_API2_STATUS(Name, ...)         → Name
#    ORT_API_T(type, Name, ...)         → Name
#    ORT_CLASS_RELEASE(Name)            → Name
$fields = New-Object System.Collections.ArrayList
$unknown = New-Object System.Collections.ArrayList
for ($i = $start + 1; $i -lt $end; $i++) {
  $l = $lines[$i]
  if ($l -match 'ORT_API2_STATUS\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,') { [void]$fields.Add($Matches[1]); continue }
  if ($l -match 'ORT_API_T\s*\(\s*[^,]+,\s*([A-Za-z_][A-Za-z0-9_]*)\s*,')   { [void]$fields.Add($Matches[1]); continue }
  if ($l -match 'ORT_CLASS_RELEASE\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)')   { [void]$fields.Add('Release' + $Matches[1]); continue }
  # 裸函数指针声明（GetErrorCode / GetErrorMessage 这类不走宏的条目）
  if ($l -match '\(ORT_API_CALL\*\s+([A-Za-z_][A-Za-z0-9_]*)\)')            { [void]$fields.Add($Matches[1]); continue }
  # 安检：结构体内出现含 ORT_API 却没被识别的行 → 解析必然错位，直接失败（绝不生成错误的偏移）
  if ($l -match 'ORT_API' -and $l -notmatch '^\s*(///|//|\*|/\*)')          { [void]$unknown.Add($l.Trim()) }
}

if ($unknown.Count -gt 0) {
  Write-Host ("以下 " + $unknown.Count + " 行含 ORT_API 但未被识别，不能生成（否则字段偏移会错位）：") -ForegroundColor Red
  foreach ($u in $unknown) { Write-Host ("  " + $u) -ForegroundColor Red }
  exit 1
}

if ($fields.Count -lt 100) {
  Write-Host ("解析出的字段太少（" + $fields.Count + " 个），头文件格式可能变了") -ForegroundColor Red; exit 1
}

# 4) 生成 C# 文件
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// OrtApi.Generated.cs — 由 tools\gen_ort_interop.ps1 自动生成，请勿手改')
[void]$sb.AppendLine('// 来源: ' + [System.IO.Path]::GetFileName($Header) + '（ORT_API_VERSION = ' + $apiVersion + '，共 ' + $fields.Count + ' 个函数指针）')
[void]$sb.AppendLine('// 说明: 字段顺序必须与 DLL 内的 OrtApi 结构体完全一致，因此这里按头文件声明顺序排列。')
[void]$sb.AppendLine('using System;')
[void]$sb.AppendLine('using System.Runtime.InteropServices;')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('namespace PicTool')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    internal static class OrtApiInfo')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        public const int ApiVersion = ' + $apiVersion + ';')
[void]$sb.AppendLine('        public const int FieldCount = ' + $fields.Count + ';')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('    [StructLayout(LayoutKind.Sequential)]')
[void]$sb.AppendLine('    internal struct OrtApiRaw')
[void]$sb.AppendLine('    {')
for ($k = 0; $k -lt $fields.Count; $k++) {
  [void]$sb.AppendLine('        public IntPtr ' + $fields[$k] + ';   // ' + $k)
}
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Set-Content -LiteralPath $Out -Value $sb.ToString() -Encoding UTF8
Write-Host ('已生成 ' + $Out + '：ORT_API_VERSION=' + $apiVersion + '，字段 ' + $fields.Count + ' 个') -ForegroundColor Green
