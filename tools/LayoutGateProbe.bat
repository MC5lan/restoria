@echo off
rem LayoutGateProbe.bat - does the layout gate notice a one-pixel change?
rem
rem The layout gate is described as evidence that a UI refactor changed
rem nothing. That only holds if the snapshot is sensitive at small scales,
rem which nothing has ever checked. This probe answers it by nudging one
rem control a single pixel and diffing the snapshot.
rem
rem It touches nothing on disk: the nudge happens inside a separate process's
rem in-memory form, and the baseline file is never modified.
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. & exit /b 1

set OUT=%TEMP%\layoutgateprobe.exe
%CSC% /nologo /target:winexe /platform:anycpu /optimize+ ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:"%OUT%" tools\LayoutGateProbe.cs
if errorlevel 1 echo BUILD FAILED & exit /b 1

rem A winexe has no console; redirect the report to a file and print it after.
"%OUT%" > "%TEMP%\layoutprobe.txt" 2>&1
type "%TEMP%\layoutprobe.txt"
exit /b %ERRORLEVEL%