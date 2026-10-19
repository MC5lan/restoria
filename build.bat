@echo off
rem ============================================================
rem  Image Toolbox build script (keep this file ASCII-only!)
rem  Output: dist\PicTool.exe  (single file, xcopy anywhere, run)
rem  Requires: .NET Framework 4.8 on the target machine.
rem  Compiler: in-box csc from 4.x; references point to the 4.0 runtime,
rem            so the language level is C# 5 and Vector<T> / Parallel.For /
rem            Task / CancellationToken are available. GDI+ and WinForms are
rem            unchanged. (Was CLR2/2.0 until 2026-09-16 - see docs.)
rem  Platform: AnyCPU - runs 64-bit on 64-bit Windows (more memory),
rem            32-bit on 32-bit Windows (same compatibility as before)
rem
rem  Optional AI inpainting (MI-GAN via ONNX Runtime):
rem    put vendor\onnxruntime_c_api.h in place, build.bat then generates
rem    src\Ai\OrtApi.Generated.cs from it (exact struct field order) and
rem    builds with /define:ORT_INTEROP. Without that header the app is
rem    built without AI mode - every other feature is unchanged.
rem ============================================================

cd /d "%~dp0"

rem When the build is driven by a script (tools\make_package.ps1), it sets
rem PICTOOL_NO_PAUSE=1 so the trailing "pause" does not block forever waiting
rem for a keypress that nobody can give. Interactive double-click keeps pausing.
set PICTOOL_PAUSE=pause
if "%PICTOOL_NO_PAUSE%"=="1" set PICTOOL_PAUSE=rem (pause skipped: script mode)

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. Install .NET Framework 3.5 or 4.x first.
if not exist "%CSC%" %PICTOOL_PAUSE%
if not exist "%CSC%" exit /b 1

if not exist dist mkdir dist
if not exist dist\ai mkdir dist\ai

set AI_DEFINE=
set AI_EXTRA=
if not exist "vendor\onnxruntime_c_api.h" goto NOAI
echo [AI] generating interop layer from vendor\onnxruntime_c_api.h ...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\gen_ort_interop.ps1" -Header "vendor\onnxruntime_c_api.h" -Out "src\Ai\OrtApi.Generated.cs"
if errorlevel 1 goto GENFAIL
set AI_DEFINE=/define:ORT_INTEROP
set AI_EXTRA=src\Ai\OrtApi.Generated.cs
echo [AI] AI mode ENABLED
goto AIDONE
:GENFAIL
echo [AI] generation failed - building WITHOUT AI mode
if exist "src\Ai\OrtApi.Generated.cs" del /q "src\Ai\OrtApi.Generated.cs"
goto AIDONE
:NOAI
echo [AI] vendor\onnxruntime_c_api.h not found - building WITHOUT AI mode
:AIDONE

set ICONOPT=
if exist "assets\app.ico" set ICONOPT=/win32icon:assets\app.ico
if not exist "assets\app.ico" echo [ICON] assets\app.ico not found - building without icon (run tools\make_icon.bat)

rem application manifest: declares supportedOS (Win7/8/8.1/10/11) and asInvoker (no auto-UAC).
rem Deliberately NO dpiAware entry - the UI is calibrated against the current layout baseline.
set MANIFESTOPT=
if exist "assets\app.manifest" set MANIFESTOPT=/win32manifest:assets\app.manifest
if not exist "assets\app.manifest" echo [MANIFEST] assets\app.manifest not found - building without manifest

%CSC% /noconfig /nostdlib+ /nologo /target:winexe /platform:anycpu /unsafe+ /optimize+ /codepage:65001 /langversion:5 %ICONOPT% %MANIFESTOPT% %AI_DEFINE% /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll" /out:"dist\PicTool.exe" src\Program.cs src\Core\SelfTest.cs src\Core\Bench.cs src\Core\SkewProbe.cs src\Core\Version.cs src\Core\Recovery.cs src\Core\PixelBuffer.cs src\Core\BitmapBridge.cs src\Platform\SysInfo.cs src\Ui\Anim.cs src\Ui\Theme.cs src\Ui\UiSettings.cs src\Ui\Icons.cs src\Ui\UiControls.cs src\Ui\Toast.cs src\Ui\Shortcuts.cs src\Ui\ShortcutSettings.cs src\Ui\Workspace.cs src\Ui\CanvasControl.cs src\Ui\IPageHost.cs src\Ui\RowMenu.cs src\Ui\MainForm.cs src\Ui\MainForm.Layout.cs src\Ui\MainForm.Theme.cs src\Ui\MainForm.Inspector.cs src\Ui\MainForm.Wheel.cs src\Ui\MainForm.UiKit.cs src\Ui\MainForm.Misc.cs src\Ui\MainForm.Keys.cs src\Ui\MainForm.Lifecycle.cs src\Ui\OverwriteConfirm.cs src\Ui\MainForm.Page.Crop.cs src\Ui\MainForm.Page.Rotate.cs src\Ui\MainForm.Page.Color.cs src\Ui\MainForm.Page.Seal.cs src\Ui\MainForm.Page.Remove.cs src\Ui\MainForm.Page.Batch.cs src\Ui\MainForm.Page.Scan.cs src\Ui\MainForm.Page.FilePage.cs src\Ui\MainForm.Workspace.cs src\Ui\MainForm.Tools.cs src\Ui\MainForm.History.cs src\Ui\MainForm.Text.cs src\Ui\MainForm.Batch.cs src\Ui\MainForm.Files.cs src\Ui\NamePrompt.cs src\Imaging\PatchFill.cs src\Imaging\PaperFill.cs src\Imaging\TextStamp.cs src\Imaging\SealTool.cs src\Imaging\DocClean.cs src\Imaging\DocClean.Light.cs src\Imaging\DocClean.Background.cs src\Imaging\DocClean.Seal.cs src\Imaging\DocClean.Binarize.cs src\Imaging\DocClean.Red.cs src\Imaging\DocClean.Stain.cs src\Imaging\DocClean.Edge.cs src\Imaging\ArchiveProcessor.cs src\Imaging\BatchProcessor.cs src\Imaging\Imaging.cs src\Imaging\Enhance.cs src\Ai\AiFill.cs src\Ai\Ocr.cs src\Imaging\Orient.cs src\Platform\Scanner.cs src\Platform\FileTools.cs %AI_EXTRA%

if errorlevel 1 echo BUILD FAILED
if errorlevel 1 %PICTOOL_PAUSE%
if errorlevel 1 exit /b 1

rem runtime config: the exe is compiled against the 4.0 runtime, so it REQUIRES
rem .NET Framework 4.x on the target machine (Win7 SP1 + 4.8 is the target).
rem The v2.0 entry is gone on purpose - a CLR2 host cannot load a mscorlib-4.0 assembly.
> "dist\PicTool.exe.config" echo ^<?xml version="1.0" encoding="utf-8"?^>
>> "dist\PicTool.exe.config" echo ^<configuration^>
>> "dist\PicTool.exe.config" echo   ^<startup^>
>> "dist\PicTool.exe.config" echo     ^<supportedRuntime version="v4.0"/^>
>> "dist\PicTool.exe.config" echo   ^</startup^>
>> "dist\PicTool.exe.config" echo ^</configuration^>

rem copy to the localized product name (read from UTF-8 file, encoding-safe)
if exist appname.txt powershell -NoProfile -Command "$n=(Get-Content -Encoding UTF8 'appname.txt' -TotalCount 1).Trim(); if($n){ $dst=Join-Path 'dist' $n; try { Copy-Item 'dist\PicTool.exe' $dst -Force -ErrorAction Stop; Copy-Item 'dist\PicTool.exe.config' ($dst + '.config') -Force -ErrorAction Stop; Write-Host ('OK -> dist\' + $n) } catch { Write-Host 'COPY FAILED - close the running app and build again' -ForegroundColor Red; exit 1 } }"
if errorlevel 1 exit /b 1

rem deploy AI assets: the repo ships a ready-made ai\ folder (ONNX runtime + MI-GAN
rem model + OCR models + Win7 shims). Copy it next to the exe so AI features work
rem out of the box. Delete ai\ to build without AI.
if exist "ai" (
  if not exist "dist\ai" mkdir "dist\ai"
  xcopy /e /y /q "ai\*" "dist\ai\" >nul
  echo [AI] ai\ deployed to dist\ai
) else (
  echo [AI] ai\ folder not found - building without AI assets
)
echo BUILD OK: dist\PicTool.exe
%PICTOOL_PAUSE%
