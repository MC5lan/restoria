@echo off
rem ProbeShim.bat - compile and run the shim dependency probe.
rem This is a diagnostic tool, not part of the shipped program: it answers
rem "does ai\win7\ need those twenty api-ms-win-core-*.dll files?" by asking
rem the Windows loader rather than by reading the docs.
rem The exe is compiled into a temp dir, not tools\, so nothing buildable
rem lands next to the sources.
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. & exit /b 1
set OUT=%TEMP%\probeshim.exe
%CSC% /nologo /target:exe /platform:anycpu /optimize+ /out:"%OUT%" tools\ProbeShim.cs
if errorlevel 1 echo BUILD FAILED & exit /b 1

echo === probing ai\win7 with the full shim set ===
"%OUT%" "%~dp0..\ai\win7"
echo.

rem Now park the twenty api-ms-win-core files one by one is too slow; instead
rem park the whole group into a temp copy so nothing in the repo is touched.
echo === building a reduced copy without the api-ms-win-core files ===
set TMPDIR=%TEMP%\probeshim-reduced
if exist "%TMPDIR%" rmdir /s /q "%TMPDIR%"
mkdir "%TMPDIR%"
copy "%~dp0..\ai\win7\onnxruntime.dll" "%TMPDIR%\" >nul
for %%f in ("%~dp0..\ai\win7\w7shim-*.dll") do copy "%%f" "%TMPDIR%\" >nul
echo     %TMPDIR% contains onnxruntime.dll + w7shim only
"%OUT%" "%TMPDIR%"
echo.
echo Note: on a non-Win7 host the loader resolves api-ms-win-* from the system
echo API-set schema, so this probe shows what THIS machine needs. The Win7
echo question can only be settled on Win7, where those API sets are absent
echo from the OS and the shipped files are the only ones that can satisfy them.